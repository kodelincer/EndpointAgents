using System.Text;
using System.Text.Json;

namespace EndpointAgents.Shared.Support;

public sealed record EndpointActivityEntry(
    DateTimeOffset TimestampUtc,
    string Level,
    string Source,
    long EventId,
    string Message);

public static class EndpointActivityJournal
{
    private const long MaximumBytes = 8 * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static string ServicePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "EndpointAgents", "Reports", "service-events.jsonl");

    public static string TrayPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EndpointAgents", "Reports", "tray-events.jsonl");

    public static bool TryAppend(string path, EndpointActivityEntry entry)
    {
        try
        {
            lock (Gate)
            {
                var directory = Path.GetDirectoryName(path)
                    ?? throw new InvalidOperationException("The activity journal has no directory.");
                Directory.CreateDirectory(directory);
                RotateIfNeeded(path);
                File.AppendAllText(path, JsonSerializer.Serialize(entry) + Environment.NewLine,
                    Utf8);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyList<EndpointActivityEntry> Read(string path,
        CancellationToken cancellationToken = default)
    {
        var entries = new List<EndpointActivityEntry>();
        foreach (var candidate in new[] { path + ".1", path })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                using var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Utf8,
                    detectEncodingFromByteOrderMarks: true);
                while (reader.ReadLine() is { } line)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var entry = JsonSerializer.Deserialize<EndpointActivityEntry>(line);
                        if (entry is not null) entries.Add(entry);
                    }
                    catch (JsonException)
                    {
                        // A partial final line can be left behind by an interrupted process.
                    }
                }
            }
            catch (IOException)
            {
                // A rotating writer can briefly replace the file while Reports is refreshing.
            }
            catch (UnauthorizedAccessException)
            {
                // Installed service journals are optional for restricted local users.
            }
        }
        return entries;
    }

    private static void RotateIfNeeded(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MaximumBytes) return;
        var archive = path + ".1";
        if (File.Exists(archive)) File.Delete(archive);
        File.Move(path, archive);
    }
}
