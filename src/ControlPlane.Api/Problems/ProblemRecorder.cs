using ControlPlane.Api.Problems.Forwarding;
using EndpointAgents.Shared.Problems;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace ControlPlane.Api.Problems;

/// <summary>A sliding one-hour counter per key: at most <c>limit</c> per key per hour.</summary>
public sealed class HourlyLimiter(int limit, TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _sends =
        new(StringComparer.OrdinalIgnoreCase);

    public bool Allow(string key)
    {
        var now = clock.GetUtcNow();
        var sends = _sends.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (sends)
        {
            while (sends.Count > 0 && now - sends.Peek() >= TimeSpan.FromHours(1)) sends.Dequeue();
            if (sends.Count >= limit) return false;
            sends.Enqueue(now);
            return true;
        }
    }
}

/// <summary>
/// The one way into the problem store: checks the per-PC limit and the free disk, cleans the text
/// again (a PC is not trusted to have done it), saves, and hands a copy to the log server
/// forwarder. Uses no AI.
/// </summary>
public sealed class ProblemRecorder(
    ProblemStore store,
    LogForwarder forwarder,
    IOptions<ProblemOptions> options,
    TimeProvider clock,
    ILogger<ProblemRecorder> logger)
{
    private readonly ProblemOptions _options = options.Value;
    private readonly HourlyLimiter _limiter = new(options.Value.MaxReportsPerPcPerHour, clock);
    private long _droppedByLimit;
    private long _droppedDiskFull;

    /// <summary>Replaced in tests. Free bytes on the disk that holds the problem database.</summary>
    internal Func<long> FreeDiskBytes { get; set; } = () => 0;

    public long DroppedByLimit => Interlocked.Read(ref _droppedByLimit);
    public long DroppedDiskFull => Interlocked.Read(ref _droppedDiskFull);

    public bool DiskLow => Free() < _options.MinFreeDiskMegabytes * 1024L * 1024;

    private long Free()
    {
        var replaced = FreeDiskBytes();
        if (replaced > 0) return replaced;
        try { return new DriveInfo(Path.GetPathRoot(store.DatabasePath)!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return long.MaxValue;
        }
    }

    /// <summary>
    /// Reports a PC sent. Returns how many were kept; the rest were over a limit, and the PC should
    /// not send them again. A database error throws, so the PC keeps them and tries later.
    /// </summary>
    public async Task<int> AddFromAgentAsync(string agentId, ProblemDeviceFacts facts,
        IReadOnlyList<ProblemReport> reports, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var clean = Clean(facts);
        await store.SaveDeviceAsync(agentId, clean, now, cancellationToken);
        var kept = reports
            .Where(report => ProblemKinds.IsFromEndpoint(report.Kind))
            .Select(report => Clean(report, now))
            .ToArray();
        return await SaveAsync(agentId, clean, kept, null, now, cancellationToken);
    }

    /// <summary>
    /// A problem the control plane saw itself (a failed operation, a rollback...). Never throws:
    /// the work it watches must not fail because of it.
    /// </summary>
    public async Task RecordAsync(string agentId, string? machineName, string? agentVersion, string kind,
        string source, string title, string? identity, string? conversationId = null)
    {
        try
        {
            var now = clock.GetUtcNow();
            var known = (await store.GetDeviceAsync(agentId, CancellationToken.None))?.Facts ?? new ProblemDeviceFacts();
            var facts = known with
            {
                MachineName = machineName ?? known.MachineName,
                AgentVersion = agentVersion ?? known.AgentVersion
            };
            var report = ProblemText.FromMessage(kind, source, title, facts.AgentVersion, now, identity);
            await SaveAsync(agentId, facts, [report], conversationId, now, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "A problem report could not be saved.");
        }
    }

    /// <summary>An installer that failed. There is no agent yet, so the PC name is the only identity.</summary>
    public Task RecordInstallFailureAsync(InstallFailureReport report)
    {
        var facts = Clean(new ProblemDeviceFacts(report.AgentVersion, report.MachineName, report.OsName, null,
            report.OsVersion, report.OsBuild));
        return RecordInternalAsync("install:" + facts.MachineName, facts, ProblemKinds.InstallFailed,
            report.Step ?? "Install", string.IsNullOrWhiteSpace(report.Message) ? "The install failed." : report.Message);
    }

    private async Task RecordInternalAsync(string agentId, ProblemDeviceFacts facts, string kind, string source, string title)
    {
        try
        {
            var now = clock.GetUtcNow();
            var report = ProblemText.FromMessage(kind, ProblemText.Limit(source, ProblemLimits.MaxFieldCharacters),
                title, facts.AgentVersion, now);
            await SaveAsync(agentId, facts, [report], null, now, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "A problem report could not be saved.");
        }
    }

    private async Task<int> SaveAsync(string agentId, ProblemDeviceFacts facts, IReadOnlyList<ProblemReport> reports,
        string? conversationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var allowed = new List<ProblemReport>(reports.Count);
        foreach (var report in reports)
        {
            if (_limiter.Allow(agentId)) allowed.Add(report);
            else Interlocked.Increment(ref _droppedByLimit);
        }
        if (allowed.Count == 0) return 0;

        // The outside log server still gets its copy when this server's disk is nearly full.
        foreach (var report in allowed) forwarder.Enqueue(LogForwardEvent.From(report, agentId, facts));

        if (DiskLow)
        {
            Interlocked.Add(ref _droppedDiskFull, allowed.Count);
            logger.LogWarning("The disk is nearly full: problem reports are not being saved.");
            return allowed.Count;
        }
        await store.AddAsync(agentId, facts, allowed, conversationId, now, cancellationToken);
        if (store.DatabaseBytes() > _options.MaxDatabaseMegabytes * 1024L * 1024)
            await store.EnforceLimitsAsync(now, cancellationToken);
        return allowed.Count;
    }

    internal static ProblemDeviceFacts Clean(ProblemDeviceFacts facts) => new(
        Field(facts.AgentVersion), Field(facts.MachineName), Field(facts.OsName), Field(facts.OsEdition),
        Field(facts.OsVersion), Field(facts.OsBuild), Field(facts.Architecture), Field(facts.Manufacturer),
        Field(facts.Model));

    internal static ProblemReport Clean(ProblemReport report, DateTimeOffset now) => report with
    {
        Fingerprint = ProblemText.Limit(report.Fingerprint, 64),
        Source = ProblemText.Limit(report.Source, ProblemLimits.MaxFieldCharacters),
        Title = ProblemText.Limit(ProblemText.Scrub(report.Title), ProblemLimits.MaxTitleCharacters),
        ExceptionType = Field(report.ExceptionType),
        Stack = report.Stack is null ? null : ProblemText.ScrubStack(report.Stack),
        AgentVersion = Field(report.AgentVersion),
        Count = Math.Clamp(report.Count, 1, 1_000_000),
        // A PC's clock can be wrong; never let it put a report in the future.
        FirstSeenUtc = report.FirstSeenUtc > now ? now : report.FirstSeenUtc,
        LastSeenUtc = report.LastSeenUtc > now ? now : report.LastSeenUtc
    };

    private static string? Field(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null
            : ProblemText.Limit(ProblemText.Scrub(value.Trim()), ProblemLimits.MaxFieldCharacters);
}

/// <summary>Applies the retention and the size limits once an hour.</summary>
public sealed class ProblemMaintenanceWorker(ProblemStore store, TimeProvider clock,
    ILogger<ProblemMaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                var now = clock.GetUtcNow();
                await store.EnforceLimitsAsync(now, stoppingToken);
                await store.DeleteOldBundlesAsync(now, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Problem report cleanup failed."); }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try { return await timer.WaitForNextTickAsync(stoppingToken); }
        catch (OperationCanceledException) { return false; }
    }
}
