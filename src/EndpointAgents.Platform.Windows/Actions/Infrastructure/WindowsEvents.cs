using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Actions.Infrastructure;

public sealed record EventRecordSummary(string Log, string Source, int EventId, int Level,
    DateTimeOffset WhenUtc, string Message);

/// <summary>Reads recent Windows events with XPath queries that are fixed in code.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsEvents
{
    public static IReadOnlyList<EventRecordSummary> Read(string log, string xpath, int max,
        bool includeMessage = true)
    {
        var results = new List<EventRecordSummary>();
        try
        {
            var query = new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            for (var record = reader.ReadEvent(); record is not null && results.Count < max;
                 record = reader.ReadEvent())
            {
                using (record)
                {
                    string message = string.Empty;
                    if (includeMessage)
                    {
                        try { message = record.FormatDescription() ?? string.Empty; }
                        catch (EventLogException) { }
                    }
                    results.Add(new EventRecordSummary(log, record.ProviderName ?? string.Empty,
                        record.Id, record.Level ?? 0,
                        record.TimeCreated is { } time
                            ? new DateTimeOffset(time).ToUniversalTime() : DateTimeOffset.MinValue,
                        FirstLine(message, 200)));
                }
            }
        }
        catch (EventLogNotFoundException) { }
        catch (EventLogException) { }
        catch (UnauthorizedAccessException) { }
        return results;
    }

    /// <summary>XPath time filter for the last <paramref name="days"/> days.</summary>
    public static string Since(int days) =>
        $"TimeCreated[timediff(@SystemTime) <= {(long)TimeSpan.FromDays(days).TotalMilliseconds}]";

    public static string FirstLine(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var line = value.Split('\n', 2)[0].Trim();
        return line.Length <= max ? line : line[..max];
    }
}
