using EndpointAgents.Core.Commands;
using EndpointAgents.Shared.Problems;
using EndpointAgents.Shared.Rpc;
using EndpointAgents.Shared.Support;
using Microsoft.Extensions.Logging;
using System.Diagnostics.Eventing.Reader;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EndpointAgents.Platform.Windows.Diagnostics;

/// <summary>Builds the zip for "Get logs": every line cleaned, every part and the whole limited.</summary>
public static class LogBundleBuilder
{
    public const int MaxBytesPerPart = 3 * 1024 * 1024;

    /// <summary>
    /// Adds parts in the order given until the size limit. Each part keeps its newest lines. Parts
    /// are written uncompressed-size-checked, so the zip can never go over the limit.
    /// </summary>
    public static byte[] Build(IEnumerable<(string Name, string Text)> parts, long maxBytes = AgentLogRoutes.MaxBundleBytes)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in parts)
            {
                // Room for the zip's own bookkeeping at the end.
                var room = maxBytes - output.Length - 64 * 1024;
                if (room <= 0) break;
                var content = Encoding.UTF8.GetBytes(Clean(text, (int)Math.Min(room, MaxBytesPerPart)));
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(content);
            }
        }
        return output.ToArray();
    }

    /// <summary>Cleans line by line and keeps the newest lines that fit.</summary>
    public static string Clean(string text, int maxBytes)
    {
        var kept = new List<string>();
        var size = 0;
        foreach (var line in text.Split('\n').Reverse())
        {
            var clean = ProblemText.Scrub(line.TrimEnd('\r'));
            size += Encoding.UTF8.GetByteCount(clean) + 1;
            if (size > maxBytes) break;
            kept.Add(clean);
        }
        kept.Reverse();
        return string.Join('\n', kept);
    }
}

/// <summary>
/// "Get logs" for one PC, asked for by an admin. It always collects the same fixed files of our own
/// and a fixed slice of the Windows event log, and uploads them to one fixed route. The request
/// carries only an id: no paths, no scripts, nothing the agent or a caller can choose.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class CollectLogsCommandHandler(
    EndpointArtifactTransport transport,
    ILogger<CollectLogsCommandHandler> logger) : IEndpointCommandHandler
{
    private const int MaxEvents = 500;
    private static readonly TimeSpan EventAge = TimeSpan.FromDays(7);

    public string Method => AgentLogRoutes.CollectMethod;

    public async Task<RpcResponse> HandleAsync(RpcRequest request, CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        var requestId = request.Params is { ValueKind: JsonValueKind.Object } value
            && value.TryGetProperty("requestId", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString() : null;
        if (requestId is null || !RequestId().IsMatch(requestId))
            return RpcResponse.Failure(request.Id, 400, "params.requestId must be a 32-character id.");

        var bundle = await Task.Run(() => LogBundleBuilder.Build(Parts(cancellationToken)), cancellationToken);
        using var content = new ByteArrayContent(bundle);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        using var client = transport.CreateClient();
        using var upload = await transport.CreateLogUploadRequestAsync(requestId, content, cancellationToken);
        using var response = await client.SendAsync(upload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("The control plane refused the logs: {Status}", (int)response.StatusCode);
            return RpcResponse.Failure(request.Id, 502, $"The control plane refused the logs ({(int)response.StatusCode}).");
        }
        return RpcResponse.Success(request.Id, new { requestId, sizeBytes = bundle.Length });
    }

    private static IEnumerable<(string Name, string Text)> Parts(CancellationToken cancellationToken)
    {
        foreach (var path in new[] { EndpointActivityJournal.ServicePath, EndpointActivityJournal.ServicePath + ".1" })
            if (Read(path) is { } text) yield return (Path.GetFileName(path), text);
        foreach (var path in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "EndpointAgents", "Updates", "updater.log"),
            ProblemPaths.InstallLog
        })
            if (Read(path) is { } text) yield return (Path.GetFileName(path), text);

        // Each user's Tray keeps its own journal. The file name in the zip has no user name in it.
        var number = 0;
        foreach (var path in TrayJournals())
            if (Read(path) is { } text) yield return ($"tray-events-{++number}.jsonl", text);

        yield return ("windows-events.txt", WindowsEvents(cancellationToken));
    }

    private static IEnumerable<string> TrayJournals()
    {
        var users = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Users");
        string[] profiles;
        try { profiles = Directory.GetDirectories(users); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var profile in profiles)
            yield return Path.Combine(profile, "AppData", "Local", "EndpointAgents", "Reports", "tray-events.jsonl");
    }

    private static string? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Only the end of a long file is read: the newest lines are the ones that matter.
            if (stream.Length > LogBundleBuilder.MaxBytesPerPart)
                stream.Seek(-LogBundleBuilder.MaxBytesPerPart, SeekOrigin.End);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Our own event sources, and crashes of our own programs; nothing else from the log.</summary>
    private static string WindowsEvents(CancellationToken cancellationToken)
    {
        var since = (long)EventAge.TotalMilliseconds;
        var query = new EventLogQuery("Application", PathType.LogName,
            "*[System[Provider[@Name='EndpointAgents.Service' or @Name='EndpointAgents.Tray' or " +
            $"@Name='Application Error' or @Name='.NET Runtime'] and TimeCreated[timediff(@SystemTime) <= {since}]]]")
        {
            ReverseDirection = true
        };
        var text = new StringBuilder();
        try
        {
            using var reader = new EventLogReader(query);
            var count = 0;
            while (count < MaxEvents && !cancellationToken.IsCancellationRequested && reader.ReadEvent() is { } record)
            {
                using (record)
                {
                    string message;
                    try { message = record.FormatDescription() ?? ""; }
                    catch (EventLogException) { message = ""; }
                    // The two general sources speak for every program on the PC: keep only ours.
                    if (record.ProviderName is "Application Error" or ".NET Runtime"
                        && !message.Contains("EndpointAgents", StringComparison.OrdinalIgnoreCase)) continue;
                    text.Append(record.TimeCreated?.ToUniversalTime().ToString("O")).Append(' ')
                        .Append(record.ProviderName).Append(' ').Append(record.Id).Append(' ')
                        .AppendLine(message.Replace('\r', ' ').Replace('\n', ' '));
                    count++;
                }
            }
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            text.AppendLine("The Windows event log could not be read: " + ex.GetType().Name);
        }
        return text.ToString();
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex RequestId();
}
