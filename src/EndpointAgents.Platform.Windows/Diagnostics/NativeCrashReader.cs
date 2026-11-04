using EndpointAgents.Shared.Problems;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace EndpointAgents.Platform.Windows.Diagnostics;

/// <summary>
/// A stack overflow or a crash in native code ends the process before our own handlers can run.
/// Windows still writes it to its Application log (Application Error, event 1000), so the service
/// looks there for crashes of our own programs and turns them into problem reports.
/// </summary>
public static partial class NativeCrashParser
{
    /// <summary>A managed exception: the program's own crash handler has already reported it.</summary>
    private const string ManagedExceptionCode = "0xe0434352";

    /// <summary>Returns a report for a crash of one of our programs, or null for anything else.</summary>
    public static ProblemReport? Parse(string? message, DateTimeOffset whenUtc, string? agentVersion)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var program = Application().Match(message);
        if (!program.Success) return null;
        var code = ExceptionCode().Match(message).Groups[1].Value.ToLowerInvariant();
        if (code.Length == 0 || code == ManagedExceptionCode) return null;
        var module = Module().Match(message).Groups[1].Value;
        var source = program.Groups[1].Value switch
        {
            "Service" => "Service",
            "Updater" => "Updater",
            _ => "Tray"
        };
        var text = $"{program.Value} stopped with exception code {code} in {module}";
        return ProblemText.FromMessage(ProblemKinds.Crash, source, text, agentVersion, whenUtc, $"native:{code}:{module}") with
        {
            ExceptionType = code
        };
    }

    [GeneratedRegex(@"EndpointAgents\.(Service|TrayWeb|Tray|Updater)\.exe", RegexOptions.IgnoreCase)]
    private static partial Regex Application();

    [GeneratedRegex(@"Exception code:\s*(0x[0-9a-fA-F]+)")]
    private static partial Regex ExceptionCode();

    [GeneratedRegex(@"Faulting module name:\s*([^,\r\n]+)")]
    private static partial Regex Module();
}

[SupportedOSPlatform("windows")]
public static class NativeCrashReader
{
    private const int MaxEvents = 50;
    private static readonly TimeSpan FirstLook = TimeSpan.FromDays(1);

    /// <summary>
    /// Crashes since the last look, which is remembered in <paramref name="cursorPath"/> so each is
    /// reported once. A fixed query on the Application log; it reads nothing else.
    /// </summary>
    public static IReadOnlyList<ProblemReport> ReadNew(string cursorPath, string? agentVersion, DateTimeOffset now)
    {
        var since = ReadCursor(cursorPath) ?? now - FirstLook;
        var age = (long)Math.Max(1, (now - since).TotalMilliseconds);
        var reports = new List<ProblemReport>();
        try
        {
            var query = new EventLogQuery("Application", PathType.LogName,
                $"*[System[Provider[@Name='Application Error'] and EventID=1000 and TimeCreated[timediff(@SystemTime) <= {age}]]]")
            {
                ReverseDirection = true
            };
            using var reader = new EventLogReader(query);
            for (var count = 0; count < MaxEvents && reader.ReadEvent() is { } record; count++)
                using (record)
                {
                    string? message;
                    try { message = record.FormatDescription(); }
                    catch (EventLogException) { continue; }
                    var when = record.TimeCreated is { } time ? new DateTimeOffset(time).ToUniversalTime() : now;
                    if (NativeCrashParser.Parse(message, when, agentVersion) is { } report) reports.Add(report);
                }
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            // Without access to the log there is nothing to report, and the next look tries again.
            return [];
        }
        WriteCursor(cursorPath, now);
        return reports;
    }

    private static DateTimeOffset? ReadCursor(string path)
    {
        try
        {
            return File.Exists(path) && DateTimeOffset.TryParse(File.ReadAllText(path), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var value) ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void WriteCursor(string path, DateTimeOffset now)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, now.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
