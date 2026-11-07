using ControlPlane.Api.Actions;
using ControlPlane.Api.Agents;
using EndpointAgents.Shared.Actions;
using System.Security.Cryptography;
using System.Text;

namespace ControlPlane.Api.Health;

/// <summary>
/// Runs health rules on online PCs: the fixed check, then (after the report-only week) the
/// chosen fix or the fixed Tray notice. Every step goes through <see cref="ActionRunner"/>, so
/// kill switches, hourly limits and before/after checks apply. No AI is involved.
/// </summary>
public sealed class HealthRuleRunner(HealthRuleStore store, ActionRunner actions, TimeProvider clock,
    ILogger<HealthRuleRunner> logger, ControlPlane.Api.Problems.ProblemRecorder? problems = null)
{
    /// <summary>At most this many checks start per minute, so PCs are not all checked at once.</summary>
    public const int MaxRunsPerTick = 20;

    public static bool Targets(HealthRule rule, string machineName) =>
        rule.Machines.Count == 0 || rule.Machines.Contains(machineName.ToUpperInvariant());

    /// <summary>A stable spread per rule and PC: up to 30 minutes for daily rules, 2 hours for weekly.</summary>
    public static TimeSpan Jitter(string ruleId, string agentId, string frequency)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(ruleId + "|" + agentId));
        var spread = frequency == HealthFrequencies.Weekly ? 120 : 30;
        return TimeSpan.FromMinutes(BitConverter.ToUInt16(hash, 0) % spread);
    }

    public static bool IsDue(HealthRule rule, string agentId, DateTimeOffset? lastChecked, DateTimeOffset now)
    {
        if (!rule.Enabled) return false;
        var jitter = Jitter(rule.RuleId, agentId, rule.Frequency);
        return lastChecked is null ? now >= rule.CreatedAtUtc + jitter : now >= lastChecked.Value + rule.Period;
    }

    /// <summary>One pass over every rule and online PC; returns how many checks ran.</summary>
    public async Task<int> RunDueAsync(IReadOnlyCollection<OnlineAgentDto> agents, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var last = (await store.ResultsAsync(null, cancellationToken))
            .ToDictionary(result => (result.RuleId, result.AgentId), result => result.CheckedAtUtc);
        var ran = 0;
        foreach (var rule in await store.ListAsync(cancellationToken))
            foreach (var agent in agents.Where(agent => Targets(rule, agent.MachineName)))
            {
                if (ran >= MaxRunsPerTick) return ran;
                if (!IsDue(rule, agent.AgentId, last.TryGetValue((rule.RuleId, agent.AgentId), out var at) ? at : null, now))
                    continue;
                await RunAsync(rule, agent.AgentId, agent.MachineName, cancellationToken);
                ran++;
            }
        return ran;
    }

    public async Task<HealthResult> RunAsync(HealthRule rule, string agentId, string machineName,
        CancellationToken cancellationToken)
    {
        var check = HealthCheckCatalog.Find(rule.CheckId)!;
        var context = new ActionRunContext(agentId, null, null, ActionRunSources.HealthRule, "health rule " + rule.Name);
        var result = await CheckAsync(rule, check, context, machineName, cancellationToken);
        if (result.Outcome == HealthOutcomes.Failed)
        {
            var reportOnly = rule.ReportOnly(clock.GetUtcNow());
            if (rule.OnFailure == HealthOnFailure.Report) { }
            else if (reportOnly) result = result with { WouldAct = true };
            else if (rule.OnFailure == HealthOnFailure.Fix && rule.FixActionId is { } fix)
            {
                var outcome = await actions.RunAsync(context, fix, check.FixArguments(rule.Option), false, cancellationToken);
                var taken = $"{fix}: {(outcome.Accepted ? outcome.Status.ToString() : outcome.Error)}";
                if (outcome.Accepted && outcome.Status is ActionStatus.Done or ActionStatus.NotNeeded)
                {
                    var after = await CheckAsync(rule, check, context, machineName, cancellationToken);
                    result = after.Outcome == HealthOutcomes.Passed
                        ? after with { Outcome = HealthOutcomes.Fixed, ActionTaken = taken }
                        : after with { ActionTaken = taken };
                }
                else result = result with { ActionTaken = taken };
            }
            else if (rule.OnFailure == HealthOnFailure.TellUser && check.NoticeId is { } notice)
            {
                var outcome = await actions.RunAsync(context, "notice.show",
                    new Dictionary<string, string?> { ["notice"] = notice }, false, cancellationToken);
                result = result with { ActionTaken = outcome.Accepted && outcome.Status == ActionStatus.Done
                    ? "told the user" : $"notice not shown: {outcome.Error ?? outcome.Summary}" };
            }
        }
        await store.SaveResultAsync(result, cancellationToken);
        logger.LogInformation("Health rule {Rule} on {Machine}: {Outcome}", rule.Name, machineName, result.Outcome);
        if (result.Outcome == HealthOutcomes.Error && problems is not null)
            await problems.RecordAsync(agentId, machineName, null,
                EndpointAgents.Shared.Problems.ProblemKinds.HealthCheckFailed, rule.CheckId,
                $"The health check {rule.CheckId} could not run: {check.LookActionId}", "not-run");
        return result;
    }

    private async Task<HealthResult> CheckAsync(HealthRule rule, HealthCheckDefinition check,
        ActionRunContext context, string machineName, CancellationToken cancellationToken)
    {
        var look = await actions.RunAsync(context, check.LookActionId, check.LookArguments(rule.Option), false, cancellationToken);
        var now = clock.GetUtcNow();
        if (!look.Accepted || look.Status != ActionStatus.Ok)
            return new(rule.RuleId, context.AgentId, machineName, now, HealthOutcomes.Error,
                look.Error ?? look.Summary ?? "The check did not run.", null, false);
        var finding = check.Evaluate(look.Data, rule.Option);
        return new(rule.RuleId, context.AgentId, machineName, now,
            finding.Passed ? HealthOutcomes.Passed : HealthOutcomes.Failed, finding.Text, null, false);
    }
}

/// <summary>Checks once a minute which rules are due on the PCs that are online.</summary>
public sealed class HealthRuleWorker(HealthRuleRunner runner, AgentRegistry registry,
    ILogger<HealthRuleWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await runner.RunDueAsync(registry.GetOnlineAgents(), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogWarning(ex, "Health rules could not run this minute."); }
        }
    }
}
