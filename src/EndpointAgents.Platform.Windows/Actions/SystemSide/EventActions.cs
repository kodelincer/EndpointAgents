using EndpointAgents.Platform.Windows.Actions.Infrastructure;
using EndpointAgents.Shared.Actions;
using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Actions.SystemSide;

/// <summary>
/// Why the PC restarted or shut down: planned restarts (with the program that asked and its
/// reason, never the user), unexpected shutdowns, power loss and blue screens, newest first.
/// Reads only the System log, a few event ids, the last 30 days, at most 20 events.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RestartReasonsAction : IEndpointAction
{
    public string Id => "events.restart_reasons";
    public const int MaxEvents = 20;

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation, CancellationToken cancellationToken)
    {
        var days = Math.Clamp(invocation.IntArgument("days", 14), 1, 30);
        var xpath = $"*[System[(EventID=1074 or EventID=6008 or EventID=41 or EventID=1001) and {WindowsEvents.Since(days)}]]";
        var items = new List<RestartEvent>();
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, xpath) { ReverseDirection = true });
            for (var record = reader.ReadEvent(); record is not null && items.Count < MaxEvents; record = reader.ReadEvent())
                using (record)
                    if (Describe(record.Id, record.ProviderName, Values(record)) is { } kind)
                        items.Add(kind with { WhenUtc = record.TimeCreated is { } time ? new DateTimeOffset(time).ToUniversalTime() : DateTimeOffset.MinValue });
        }
        catch (EventLogException) { }
        catch (UnauthorizedAccessException) { }
        var unexpected = items.Count(item => item.Kind != "planned");
        return Task.FromResult(EndpointActionResult.Ok(items.Count == 0
                ? $"No restarts or shutdowns recorded in the last {days} days."
                : $"{items.Count} restarts or shutdowns in the last {days} days, {unexpected} not planned.",
            new { Days = days, Unexpected = unexpected, Events = items }));
    }

    private static IReadOnlyList<string> Values(EventRecord record)
    {
        try { return [.. record.Properties.Select(item => item.Value?.ToString() ?? string.Empty)]; }
        catch (EventLogException) { return []; }
    }

    /// <summary>Turns one event into a plain restart record; null for events that are not one.</summary>
    public static RestartEvent? Describe(int eventId, string? provider, IReadOnlyList<string> values) => eventId switch
    {
        // User32 1074: [0] process path, [1] computer, [2] reason, [3] reason code, [4] type, [5] comment, [6] user (dropped).
        1074 when values.Count > 4 => new RestartEvent(default, "planned",
            EventText.FileName(values[0].Split(' ', 2)[0]),
            EventText.Scrub(WindowsEvents.FirstLine(values[2], 120)), WindowsEvents.FirstLine(values[4], 40)),
        6008 => new RestartEvent(default, "unexpected", null, "The previous shutdown was unexpected.", null),
        41 => new RestartEvent(default, "power-loss", null,
            "Windows restarted without shutting down cleanly (power loss, a hang or a hard reset).", null),
        1001 when string.Equals(provider, "Microsoft-Windows-WER-SystemErrorReporting", StringComparison.OrdinalIgnoreCase)
            || values.Count > 0 && values[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) =>
            new RestartEvent(default, "bluescreen", null,
                $"Blue screen, stop code {WindowsEvents.FirstLine(values.Count > 0 ? values[0] : "unknown", 40)}.", null),
        _ => null
    };
}

public sealed record RestartEvent(DateTimeOffset WhenUtc, string Kind, string? Program, string Reason, string? Type);

/// <summary>
/// Crashes and hangs of one program the user names (for example "outlook"), from the
/// Application log only, the last few days, at most 50 events, grouped by faulting module.
/// The name is matched in code; it never becomes part of the query.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AppErrorsAction : IEndpointAction
{
    public string Id => "events.app_errors";
    public const int MaxEvents = 50;

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation, CancellationToken cancellationToken)
    {
        var app = Normalize(invocation.Argument("app"));
        var days = Math.Clamp(invocation.IntArgument("days", 1), 1, 7);
        if (app.Length < 2) return Task.FromResult(EndpointActionResult.Failed("Name the program, for example outlook."));
        var xpath = $"*[System[(EventID=1000 or EventID=1002 or EventID=1026) and {WindowsEvents.Since(days)}]]";
        var matches = new List<(DateTimeOffset When, string Kind, string Module, string Code)>();
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, xpath) { ReverseDirection = true });
            var read = 0;
            for (var record = reader.ReadEvent(); record is not null && matches.Count < MaxEvents && read < 500; record = reader.ReadEvent(), read++)
                using (record)
                {
                    IReadOnlyList<string> values;
                    try { values = [.. record.Properties.Select(item => item.Value?.ToString() ?? string.Empty)]; }
                    catch (EventLogException) { continue; }
                    if (Match(record.Id, values, app) is { } hit)
                        matches.Add((record.TimeCreated is { } time ? new DateTimeOffset(time).ToUniversalTime() : DateTimeOffset.MinValue,
                            hit.Kind, hit.Module, hit.Code));
                }
        }
        catch (EventLogException) { }
        catch (UnauthorizedAccessException) { }
        var groups = matches.GroupBy(item => (item.Kind, item.Module, item.Code)).Select(group => new
        {
            group.Key.Kind, group.Key.Module, ExceptionCode = group.Key.Code, Count = group.Count(),
            LastUtc = group.Max(item => item.When)
        }).OrderByDescending(item => item.Count).Take(10).ToArray();
        return Task.FromResult(EndpointActionResult.Ok(matches.Count == 0
                ? $"No crashes or hangs of {app} in the last {days} day(s)."
                : $"{app} crashed or hung {matches.Count} time(s) in the last {days} day(s).",
            new { App = app, Days = days, Total = matches.Count, Groups = groups }));
    }

    /// <summary>"Outlook", "outlook.exe" and "OUTLOOK" all become "outlook".</summary>
    public static string Normalize(string? value)
    {
        var name = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (name.EndsWith(".exe", StringComparison.Ordinal)) name = name[..^4];
        return new string([.. name.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')]);
    }

    /// <summary>
    /// 1000 Application Error: [0] app, [3] faulting module, [6] exception code.
    /// 1002 Application Hang: [0] app. 1026 .NET Runtime: one text with "Application: x.exe".
    /// </summary>
    public static (string Kind, string Module, string Code)? Match(int eventId, IReadOnlyList<string> values, string app)
    {
        bool Is(string candidate) => Normalize(EventText.FileName(candidate)) == app;
        return eventId switch
        {
            1000 when values.Count > 6 && Is(values[0]) => ("crash", EventText.FileName(values[3]), values[6]),
            1002 when values.Count > 0 && Is(values[0]) => ("hang", "", ""),
            1026 when values.Count > 0 && values[0].Split('\n', 2)[0].Replace("Application:", "", StringComparison.OrdinalIgnoreCase).Trim() is var exe && Is(exe)
                => ("crash", ".NET", WindowsEvents.FirstLine(values[0].Split('\n').FirstOrDefault(line =>
                    line.StartsWith("Exception Info:", StringComparison.OrdinalIgnoreCase))?.Replace("Exception Info:", "").Trim(), 80)),
            _ => null
        };
    }
}
