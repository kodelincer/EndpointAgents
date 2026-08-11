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
        var cutoff = DateTimeOffset.UtcNow - age;
        var entries = new List<EndpointEventLogEntry>();
        AddJournal(entries, EndpointActivityJournal.ServicePath, cancellationToken);
        AddJournal(entries, EndpointActivityJournal.TrayPath, cancellationToken);

        try
        {
            AddWindowsEvents(entries, age, cancellationToken);
        }
        catch (EventLogNotFoundException) { }
        catch (UnauthorizedAccessException) { }

        return entries
            .Where(item => item.TimestampUtc >= cutoff)
            .Where(item => string.IsNullOrWhiteSpace(level)
                || item.Level.Equals(level, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(search)
                || item.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
                || item.Source.Contains(search, StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => new
            {
                Second = item.TimestampUtc.ToUnixTimeSeconds(),
                item.Level,
                item.Message
            })
            .Select(group => group.First())
            .OrderByDescending(item => item.TimestampUtc)
            .Take(500)
            .ToArray();
    }

    private static void AddJournal(List<EndpointEventLogEntry> entries, string path,
        CancellationToken cancellationToken)
    {
        entries.AddRange(EndpointActivityJournal.Read(path, cancellationToken)
            .Select(item => new EndpointEventLogEntry(item.TimestampUtc, item.Level,
                item.Source, item.EventId, item.Message)));
    }

    private static void AddWindowsEvents(List<EndpointEventLogEntry> entries, TimeSpan age,
        CancellationToken cancellationToken)
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
        while (entries.Count < 500)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var item = reader.ReadEvent();
            if (item is null) break;
            var itemLevel = MapLevel(item.Level);
            string message;
            try { message = item.FormatDescription() ?? "No message was recorded."; }
            catch { message = "The event message could not be formatted on this computer."; }
            var timestamp = item.TimeCreated is { } created
                ? new DateTimeOffset(created) : DateTimeOffset.UtcNow;
            entries.Add(new EndpointEventLogEntry(timestamp,
                itemLevel, item.ProviderName ?? "EndpointAgents", item.Id, message));
        }
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
    public static void Activity(DateTimeOffset timestampUtc, string message) =>
        Write(message, EventLogEntryType.Information, timestampUtc, writeWindowsEvent: false);

    private static void Write(string message, EventLogEntryType type,
        DateTimeOffset? timestampUtc = null, bool writeWindowsEvent = true)
    {
        var level = type switch
        {
            EventLogEntryType.Warning => "Warning",
            EventLogEntryType.Error => "Error",
            _ => "Information"
        };
        EndpointActivityJournal.TryAppend(EndpointActivityJournal.TrayPath,
            new EndpointActivityEntry(timestampUtc ?? DateTimeOffset.UtcNow, level,
                EndpointAgentProduct.TrayEventSource, 0, message));
        if (!writeWindowsEvent) return;
        try { EventLog.WriteEntry(EndpointAgentProduct.TrayEventSource, message, type); }
        catch { Debug.WriteLine(message); }
    }
}
