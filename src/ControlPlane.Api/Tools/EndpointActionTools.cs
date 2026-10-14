using ControlPlane.Api.Actions;
using ControlPlane.Api.Chat;
using ControlPlane.Api.Hermes;
using EndpointAgents.Shared.Actions;
using EndpointAgents.Shared.Chat;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace ControlPlane.Api.Tools;

/// <summary>
/// The agent's tools for the general action catalogue, the environment map and fleet-wide
/// context. Every check (catalogue, kill switch, limits, user go-ahead, verification) happens in
/// <see cref="ActionRunner"/>; these tools only resolve the run capability and shape results.
/// </summary>
[McpServerToolType]
public sealed class EndpointActionTools
{
    private const string InvalidCapability =
        """{"ok":false,"error":"The run capability is invalid or expired."}""";

    [McpServerTool(Name = "list_endpoint_actions"), Description(
        "List the catalogued actions the agent can run on the user's PC with run_endpoint_action: id, area, tier (Look = read-only; " +
        "SafeFix = small reversible fix that runs without asking; Disruptive = interrupts the user and needs their go-ahead), " +
        "what it returns and its parameters. Call it once per conversation, filtered by area when you know it.")]
    public static async Task<string> ListEndpointActions(
        ChatStore chat,
        ActionStore store,
        [Description("The random capability supplied in this run's instructions.")]
        string runCapability,
        [Description("Optional area: network, domain, printers, drives, browsers, office, health, desktop.")]
        string? area = null,
        CancellationToken cancellationToken = default)
    {
        if (await chat.ResolveCapabilityAsync(runCapability, cancellationToken) is null)
            return InvalidCapability;
        var switchedOff = (await store.GetActionSettingsAsync(cancellationToken)).Values
            .Where(item => !item.Enabled).Select(item => item.ActionId)
            .ToHashSet(StringComparer.Ordinal);
        var actions = EndpointActionCatalog.All
            .Where(item => item.Tier != ActionTier.OperatorOnly && !switchedOff.Contains(item.Id))
            .Where(item => string.IsNullOrWhiteSpace(area)
                || item.Area.Equals(area.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(item => new
            {
                id = item.Id,
                area = item.Area,
                tier = item.Tier.ToString(),
                name = item.Name,
                description = item.Description,
                returns = item.Returns,
                verifiedBy = item.Verify?.ActionId,
                parameters = item.Parameters.Select(parameter => new
                {
                    name = parameter.Name,
                    kind = parameter.Kind.ToString(),
                    required = parameter.Required,
                    description = parameter.Description,
                    choices = parameter.Choices?.Select(choice => choice.Value),
                    targetKinds = parameter.TargetKinds,
                    min = parameter.Kind == ActionParameterKind.Integer ? parameter.Min : (int?)null,
                    max = parameter.Kind == ActionParameterKind.Integer ? parameter.Max : (int?)null
                })
            })
            .ToArray();
        return ToolResultSanitizer.Sanitize(new { ok = true, actions });
    }

    [McpServerTool(Name = "list_environment_targets"), Description(
        "List the organisation's environment map: internal web apps, printers, shared folders and servers, by id and label. " +
        "Pass these ids as Target parameters; the real addresses stay on the server.")]
    public static async Task<string> ListEnvironmentTargets(
        ChatStore chat,
        ActionStore store,
        [Description("The random capability supplied in this run's instructions.")]
        string runCapability,
        [Description("Optional kind: host, webapp, printer, share.")]
        string? kind = null,
        CancellationToken cancellationToken = default)
    {
        if (await chat.ResolveCapabilityAsync(runCapability, cancellationToken) is null)
            return InvalidCapability;
        var targets = (await store.GetTargetsAsync(cancellationToken))
            .Where(item => string.IsNullOrWhiteSpace(kind)
                || item.Kind.Equals(kind.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(item => new
            {
                id = item.Id,
                kind = item.Kind,
                label = item.Label,
                location = item.Location,
                driveLetter = item.DriveLetter,
                ports = item.Ports
            });
        return ToolResultSanitizer.Sanitize(new { ok = true, targets });
    }

    [McpServerTool(Name = "run_endpoint_action"), Description(
        "Run one catalogued action from list_endpoint_actions on the user's PC. Look actions only read. SafeFix actions run " +
        "without asking, are rate-limited and verified with a before/after check. Disruptive actions need userConfirmed=true, " +
        "which you may set only after the user agreed in this chat (offer now or later). Arguments are strings keyed by parameter name.")]
    public static async Task<string> RunEndpointAction(
        ChatStore chat,
        ActionRunner runner,
        [Description("The random capability supplied in this run's instructions.")]
        string runCapability,
        [Description("The exact action id, e.g. network.triage.")]
        string actionId,
        [Description("Parameter values by name, e.g. {\"target\":\"hr-portal\"}. Omit when the action has no parameters.")]
        Dictionary<string, string?>? arguments = null,
        [Description("true only after the user agreed to a Disruptive action in this chat.")]
        bool userConfirmed = false,
        CancellationToken cancellationToken = default)
    {
        var scope = await chat.ResolveCapabilityAsync(runCapability, cancellationToken);
        if (scope is null) return InvalidCapability;
        var definition = EndpointActionCatalog.Find(actionId);
        var isFix = definition is { Tier: ActionTier.SafeFix or ActionTier.Disruptive };

        await chat.WriteProgressAsync(scope.RunId,
            isFix ? ChatProgressStage.ActionStarted : ChatProgressStage.EndpointCheckStarted,
            ChatProgressActivity.EndpointCheck, ChatProgressOutcome.None, cancellationToken);
        var outcome = await runner.RunAsync(new ActionRunContext(scope.AgentId, scope.UserSid,
                scope.ConversationId, ActionRunSources.Agent), actionId, arguments, userConfirmed,
            cancellationToken);
        var succeeded = outcome.Accepted && outcome.Status is not ActionStatus.Failed;

        await chat.WriteToolAuditAsync(scope, $"run_endpoint_action:{actionId}",
            !outcome.Accepted ? "rejected" : outcome.Status?.ToString() ?? "error",
            outcome.Error, cancellationToken);
        if (isFix && outcome.Accepted)
            await chat.RecordRepairAttemptAsync(scope, "run_endpoint_action", actionId,
                outcome.Status is ActionStatus.Done or ActionStatus.NotNeeded,
                outcome.Status is ActionStatus.Done or ActionStatus.NotNeeded ? null : outcome.Summary,
                cancellationToken);
        await chat.WriteProgressAsync(scope.RunId,
            isFix ? ChatProgressStage.ActionCompleted : ChatProgressStage.EndpointCheckCompleted,
            ChatProgressActivity.EndpointCheck,
            succeeded ? ChatProgressOutcome.Success : ChatProgressOutcome.Failure,
            cancellationToken);
        return ActionRunner.ToToolResult(outcome);
    }

    [McpServerTool(Name = "check_known_incidents"), Description(
        "List outages the helpdesk has declared (e.g. 'Finance app down'). Call it first for any problem that may affect more " +
        "than one PC; if one matches, tell the user it is known and being worked on instead of repairing the PC.")]
    public static async Task<string> CheckKnownIncidents(
        ChatStore chat,
        ActionStore store,
        [Description("The random capability supplied in this run's instructions.")]
        string runCapability,
        CancellationToken cancellationToken = default)
    {
        if (await chat.ResolveCapabilityAsync(runCapability, cancellationToken) is null)
            return InvalidCapability;
        var targets = (await store.GetTargetsAsync(cancellationToken))
            .ToDictionary(item => item.Id, item => item.Label, StringComparer.Ordinal);
        var incidents = (await store.GetIncidentsAsync(true, cancellationToken))
            .Select(item => new
            {
                title = item.Title,
                description = item.Description,
                affects = item.AffectedTargets.Select(id => new
                {
                    id,
                    label = targets.GetValueOrDefault(id)
                }),
                since = item.StartedAtUtc
            });
        return ToolResultSanitizer.Sanitize(new { ok = true, incidents });
    }

    [McpServerTool(Name = "recent_reports_from_others"), Description(
        "Show what other users reported in the last hours (first message of each recent conversation from other PCs, shortened). " +
        "Many similar reports point to a shared outage: escalate once instead of repairing each PC.")]
    public static async Task<string> RecentReportsFromOthers(
        ChatStore chat,
        ActionStore store,
        [Description("The random capability supplied in this run's instructions.")]
        string runCapability,
        [Description("How many hours back to look, 1 to 24. Default 2.")]
        int hours = 2,
        CancellationToken cancellationToken = default)
    {
        var scope = await chat.ResolveCapabilityAsync(runCapability, cancellationToken);
        if (scope is null) return InvalidCapability;
        var masker = new TargetMasker(await store.GetTargetsAsync(cancellationToken),
            await store.GetSettingsAsync(cancellationToken));
        var reports = (await store.GetRecentFirstMessagesAsync(scope.AgentId,
                TimeSpan.FromHours(Math.Clamp(hours, 1, 24)), 40, cancellationToken))
            .Select(item => new
            {
                text = Shorten(masker.Mask(PromptRedactor.RedactSecrets(item.Body)), 160),
                at = item.CreatedAtUtc,
                status = item.HelpdeskStatus
            })
            .ToArray();
        return ToolResultSanitizer.Sanitize(new { ok = true, count = reports.Length, reports });
    }

    [McpServerTool(Name = "probe_from_server"), Description(
        "Check an environment-map target from the control-plane server (name lookup, ports, web status). If it is down from the " +
        "server too, the problem is not the user's PC: do not repair the PC, escalate instead.")]
    public static async Task<string> ProbeFromServer(
        ChatStore chat,
        ActionStore store,
        ServerProbe probe,
        [Description("The random capability supplied in this run's instructions.")]
        string runCapability,
        [Description("An id from list_environment_targets.")]
        string targetId,
        CancellationToken cancellationToken = default)
    {
        var scope = await chat.ResolveCapabilityAsync(runCapability, cancellationToken);
        if (scope is null) return InvalidCapability;
        var target = EndpointActionValidator.IsValidTargetId(targetId)
            ? await store.GetTargetAsync(targetId, cancellationToken) : null;
        if (target is null)
            return """{"ok":false,"error":"Unknown target. Use list_environment_targets."}""";
        var result = await probe.ProbeAsync(target, cancellationToken);
        await chat.WriteToolAuditAsync(scope, "probe_from_server", result.Verdict, null,
            cancellationToken);
        return ToolResultSanitizer.Sanitize(new
        {
            ok = true,
            target = result.TargetId,
            label = result.Label,
            nameResolves = result.NameResolves,
            expectedAddress = result.ExpectedAddressMatches,
            ports = result.Ports.Select(port => new { port.Port, port.Open }),
            httpStatus = result.HttpStatus,
            verdict = result.Verdict
        });
    }

    [McpServerTool(Name = "check_user_account"), Description(
        "Read-only check of the signed-in user's domain account: disabled, locked out, password expired or must change, " +
        "account expired. It never unlocks or resets; if the account is locked or expired, escalate to the helpdesk.")]
    public static async Task<string> CheckUserAccount(
        ChatStore chat,
        ActiveDirectoryAccountChecker directory,
        [Description("The random capability supplied in this run's instructions.")]
        string runCapability,
        CancellationToken cancellationToken = default)
    {
        var scope = await chat.ResolveCapabilityAsync(runCapability, cancellationToken);
        if (scope is null) return InvalidCapability;
        if (!directory.Configured)
            return """{"ok":false,"error":"Account checks are not set up. If you suspect a locked or expired account, escalate."}""";
        var status = await directory.GetStatusAsync(scope.UserSid, cancellationToken);
        await chat.WriteToolAuditAsync(scope, "check_user_account",
            status is null ? "error" : "success", null, cancellationToken);
        return status is null
            ? """{"ok":false,"error":"The directory could not be reached. Escalate if sign-in problems continue."}"""
            : ToolResultSanitizer.Sanitize(new
            {
                ok = true,
                found = status.Found,
                enabled = status.Enabled,
                lockedOut = status.LockedOut,
                passwordExpired = status.PasswordExpired,
                mustChangePassword = status.MustChangePassword,
                passwordAgeDays = status.PasswordAgeDays,
                passwordNeverExpires = status.PasswordNeverExpires,
                accountExpired = status.AccountExpired
            });
    }

    [McpServerTool(Name = "note_skill_used"), Description(
        "Record which skill guided this conversation, so the helpdesk can see how well each skill works. Call it once when you " +
        "start following a skill. It changes nothing on the PC.")]
    public static async Task<string> NoteSkillUsed(
        ChatStore chat,
        ActionStore store,
        [Description("The random capability supplied in this run's instructions.")]
        string runCapability,
        [Description("The skill's name, e.g. printing-support or hd-vpn-after-password-change.")]
        string skillName,
        CancellationToken cancellationToken = default)
    {
        var scope = await chat.ResolveCapabilityAsync(runCapability, cancellationToken);
        if (scope is null) return InvalidCapability;
        var name = (skillName ?? "").Trim().ToLowerInvariant();
        if (name.StartsWith(SkillService.FolderPrefix, StringComparison.Ordinal))
            name = name[SkillService.FolderPrefix.Length..];
        if (!SkillService.IsValidName(name))
            return """{"ok":false,"error":"Unknown skill name."}""";
        await store.RecordSkillUseAsync(name, scope.ConversationId, scope.RunId, cancellationToken);
        return """{"ok":true}""";
    }

    private static string Shorten(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
