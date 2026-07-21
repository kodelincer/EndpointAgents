using EndpointAgents.Shared.Support;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;

namespace EndpointAgents.Tray;

public sealed record EndpointEventLogEntry(
    DateTimeOffset TimestampUtc,
    string Level,
    string Source,
    long EventId,
    string Message)
{
    public EndpointLogExcerpt ToExcerpt() => new(TimestampUtc, Level, Source, EventId, Message);
}

public sealed class EndpointEventLogReader
{
    public Task<IReadOnlyList<EndpointEventLogEntry>> ReadAsync(TimeSpan age,
        string? level, string? search, CancellationToken cancellationToken) =>
        Task.Run(() => Read(age, level, search, cancellationToken), cancellationToken);

    private static IReadOnlyList<EndpointEventLogEntry> Read(TimeSpan age,
        string? level, string? search, CancellationToken cancellationToken)
    {
        var milliseconds = Math.Max(1, (long)age.TotalMilliseconds);
        var queryText = $"*[System[((Provider[@Name='{EndpointAgentProduct.ServiceEventSource}']) " +
            $"or (Provider[@Name='{EndpointAgentProduct.TrayEventSource}'])) and " +
            $"TimeCreated[timediff(@SystemTime) <= {milliseconds}]]]";
        var query = new EventLogQuery("Application", PathType.LogName, queryText)
        {
            ReverseDirection = true,
            TolerateQueryErrors = true
        };
        using var reader = new EventLogReader(query);
        var entries = new List<EndpointEventLogEntry>();
        while (entries.Count < 500)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var item = reader.ReadEvent();
            if (item is null) break;
            var itemLevel = item.LevelDisplayName ?? MapLevel(item.Level);
            string message;
            try { message = item.FormatDescription() ?? "No message was recorded."; }
            catch { message = "The event message could not be formatted on this computer."; }
            if (!string.IsNullOrWhiteSpace(level)
                && !itemLevel.Equals(level, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(search)
                && !message.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !(item.ProviderName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
                continue;
            var timestamp = item.TimeCreated is { } created
                ? new DateTimeOffset(created) : DateTimeOffset.UtcNow;
            entries.Add(new EndpointEventLogEntry(timestamp,
                itemLevel, item.ProviderName ?? "EndpointAgents", item.Id, message));
        }
        return entries;
    }

    private static string MapLevel(byte? level) => level switch
    {
        1 or 2 => "Error",
        3 => "Warning",
        4 => "Information",
        _ => "Information"
    };
}

public static class TrayEventLogger
{
    public static void Information(string message) => Write(message, EventLogEntryType.Information);
    public static void Warning(string message) => Write(message, EventLogEntryType.Warning);
    public static void Error(string message) => Write(message, EventLogEntryType.Error);

    private static void Write(string message, EventLogEntryType type)
    {
        try { EventLog.WriteEntry(EndpointAgentProduct.TrayEventSource, message, type); }
        catch { Debug.WriteLine(message); }
    }
}
