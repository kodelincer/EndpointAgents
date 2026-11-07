using EndpointAgents.Platform.Windows.Diagnostics;
using EndpointAgents.Shared.Problems;
using Microsoft.Extensions.Options;
using System.Runtime.Versioning;

namespace EndpointAgents.Service;

/// <summary>
/// Sends waiting problem reports when the PC connects and every few minutes, and tells the control
/// plane once an hour that this PC is alive, so a PC that goes quiet stands out on the Problems page.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProblemReportWorker(
    AgentWorker agent,
    ProblemOutbox outbox,
    IOptions<ControlPlaneOptions> options,
    ILogger<ProblemReportWorker> logger) : BackgroundService
{
    private static readonly TimeSpan SendInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AliveInterval = TimeSpan.FromHours(1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private ProblemDeviceFacts? _facts;
    private DateTimeOffset _lastCall = DateTimeOffset.MinValue;
    private volatile bool _connectedAgain;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        agent.Registered += OnRegistered;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                TakeUpdaterReports();
                foreach (var crash in NativeCrashReader.ReadNew(ProblemPaths.NativeCrashCursor,
                             options.Value.Version, DateTimeOffset.UtcNow))
                    outbox.Add(crash, saveNow: true);
                outbox.Flush();
                await SendAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Warning, not error: a failure to send must not create a report of its own.
                logger.LogWarning("Problem reports could not be sent now: {Problem}", ex.GetType().Name);
            }

            try { await _wake.WaitAsync(SendInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        outbox.Flush();
    }

    private void OnRegistered()
    {
        _connectedAgain = true;
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task SendAsync(CancellationToken cancellationToken)
    {
        var alive = _connectedAgain || DateTimeOffset.UtcNow - _lastCall >= AliveInterval;
        _facts ??= WindowsDeviceFacts.Read(options.Value.Version, Environment.MachineName);
        while (true)
        {
            var batch = outbox.NextBatch();
            if (batch.Count == 0 && !alive) return;
            var kept = await agent.InvokeHubAsync<int?>(ProblemHubMethods.Report,
                new ProblemReportBatch(_facts, batch), cancellationToken);
            if (kept is null) return; // not connected: everything keeps waiting
            _lastCall = DateTimeOffset.UtcNow;
            _connectedAgain = false;
            alive = false;
            if (batch.Count == 0) return;
            outbox.MarkSent(batch);
        }
    }

    /// <summary>The updater runs on its own and leaves its reports in a file.</summary>
    private void TakeUpdaterReports()
    {
        var path = ProblemPaths.UpdaterInbox;
        if (!File.Exists(path)) return;
        var inbox = new ProblemOutbox(path);
        foreach (var report in inbox.NextBatch()) outbox.Add(report, saveNow: true);
        File.Delete(path);
    }
}
