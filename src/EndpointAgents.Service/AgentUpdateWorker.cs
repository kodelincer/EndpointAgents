using EndpointAgents.Platform.Windows.Updates;
using EndpointAgents.Shared.Updates;
using Microsoft.Extensions.Options;
using System.Runtime.Versioning;

namespace EndpointAgents.Service;

/// <summary>
/// Asks the control plane every hour (or when told "check now") whether a newer agent should be
/// installed, hands an offer to <see cref="AgentUpdateInstaller"/>, confirms a fresh update once
/// the new version is connected, and reports every result back to the dashboard.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentUpdateWorker(
    AgentWorker agent,
    AgentUpdateInstaller installer,
    IOptions<AgentUpdateOptions> options,
    ILogger<AgentUpdateWorker> logger) : BackgroundService
{
    private readonly AgentUpdateOptions _options = options.Value;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private volatile bool _registered;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        agent.Registered += OnRegistered;
        agent.UpdateCheckRequested += Wake;
        if (!_options.Enabled)
        {
            logger.LogInformation("Agent self-update is turned off (AgentUpdate:Enabled).");
            return;
        }
        // The first check waits a little, so a PC that just booted settles first.
        var nextCheck = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (RecoverInterruptedUpdate()) return; // the updater stops this service and rolls back
                await ReportFinishedUpdateAsync(stoppingToken);
                if (DateTimeOffset.UtcNow >= nextCheck && _registered)
                {
                    await CheckAsync(stoppingToken);
                    var interval = TimeSpan.FromMinutes(Math.Clamp(_options.CheckIntervalMinutes, 5, 24 * 60));
                    // Spread checks so hundreds of PCs do not ask at the same second.
                    nextCheck = DateTimeOffset.UtcNow + interval * (0.9 + Random.Shared.NextDouble() * 0.2);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Agent update check failed.");
                nextCheck = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(15);
            }

            if (await WaitAsync(TimeSpan.FromMinutes(1), stoppingToken))
                nextCheck = DateTimeOffset.UtcNow; // "check now" from the control plane
        }
    }

    /// <summary>
    /// Finishes an update whose updater died half-way (for example the PC lost power during the
    /// switch). True when the previous version is being put back.
    /// </summary>
    private bool RecoverInterruptedUpdate()
    {
        try
        {
            var outcome = AgentUpdateProbation.RecoverOrphaned(installer.StatePath, agent.Version,
                DateTimeOffset.UtcNow, connected: _registered);
            if (outcome != AgentUpdateProbation.OrphanOutcome.None)
                logger.LogWarning("The updater did not finish the update to {Version}: {Outcome}.",
                    agent.Version, outcome);
            return outcome == AgentUpdateProbation.OrphanOutcome.RollingBack;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(ex, "Could not finish an interrupted agent update.");
            return false;
        }
    }

    private async Task<bool> WaitAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try { return await _wake.WaitAsync(delay, stoppingToken); }
        catch (OperationCanceledException) { return false; }
    }

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    /// <summary>
    /// After an update the updater waits for this: the new version proves it works by connecting.
    /// </summary>
    private void OnRegistered()
    {
        _registered = true;
        try
        {
            var state = AgentUpdateState.Read(installer.StatePath);
            if (state is { Phase: AgentUpdatePhases.AwaitingHealth }
                && string.Equals(state.Version, agent.Version, StringComparison.OrdinalIgnoreCase))
            {
                (state with { Phase = AgentUpdatePhases.Healthy, Message = "The new version connected." })
                    .Write(installer.StatePath);
                logger.LogInformation("Agent update to {Version} is healthy.", agent.Version);
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Could not confirm the agent update."); }
        Wake();
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        var state = AgentUpdateState.Read(installer.StatePath);
        if (state is not null && AgentUpdatePhases.IsInProgress(state.Phase)
            && DateTimeOffset.UtcNow - state.UpdatedAtUtc < TimeSpan.FromMinutes(30))
            return; // an update is being installed right now
        var failed = state?.FailedVersions ?? [];
        var check = new AgentUpdateCheck(agent.Version, Environment.OSVersion.Version.Build,
            Environment.Is64BitOperatingSystem ? "x64" : "x86", failed, agent.IsBusy);
        var offer = await agent.InvokeHubAsync<AgentUpdateOffer?>(AgentUpdateHubMethods.Check, check, cancellationToken);
        if (offer is null) return;

        var rejected = installer.Reject(offer, agent.Version, failed);
        if (rejected is not null)
        {
            logger.LogWarning("Refused agent update {Version}: {Reason}", offer.Manifest?.Version, rejected);
            await ReportAsync(offer.Manifest?.Version ?? "?", AgentUpdateStatuses.Failed, rejected, cancellationToken);
            return;
        }
        if (agent.IsBusy) return; // a repair started meanwhile; try at the next check
        await ReportAsync(offer.Manifest.Version, AgentUpdateStatuses.Downloading,
            "Downloading and checking the signed update.", cancellationToken);
        try
        {
            await installer.StageAndLaunchAsync(offer, agent.Version, state, cancellationToken);
            await ReportAsync(offer.Manifest.Version, AgentUpdateStatuses.Installing,
                "The update is verified; the agent restarts on the new version.", cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Agent update {Version} could not be staged.", offer.Manifest.Version);
            await ReportAsync(offer.Manifest.Version, AgentUpdateStatuses.Failed, ex.Message, cancellationToken);
        }
    }

    /// <summary>Sends the updater's final result once (succeeded, or rolled back with the reason).</summary>
    private async Task ReportFinishedUpdateAsync(CancellationToken cancellationToken)
    {
        var state = AgentUpdateState.Read(installer.StatePath);
        if (state is null || state.Reported || !AgentUpdatePhases.IsFinished(state.Phase)) return;
        var status = state.Phase switch
        {
            AgentUpdatePhases.Succeeded => AgentUpdateStatuses.Succeeded,
            AgentUpdatePhases.RolledBack => AgentUpdateStatuses.RolledBack,
            _ => AgentUpdateStatuses.Failed
        };
        if (await agent.SendHubAsync(AgentUpdateHubMethods.Report,
                new AgentUpdateReport(state.FromVersion, state.Version, status, state.Message), cancellationToken))
            (state with { Reported = true }).Write(installer.StatePath);
    }

    private Task ReportAsync(string toVersion, string status, string message, CancellationToken cancellationToken) =>
        agent.SendHubAsync(AgentUpdateHubMethods.Report,
            new AgentUpdateReport(agent.Version, toVersion, status, message), cancellationToken);
}
