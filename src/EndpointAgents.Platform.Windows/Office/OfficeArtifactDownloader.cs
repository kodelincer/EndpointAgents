using Microsoft.Extensions.Options;
using System.Net;
using System.Security.Cryptography;

namespace EndpointAgents.Platform.Windows.Office;

/// <summary>Stages one catalog-approved Office executable with atomic hash verification.</summary>
public sealed class OfficeArtifactDownloader(
    IOptions<OfficeRemediationOptions> options,
    Uri hubUri)
{
    private readonly OfficeRemediationOptions _options = options.Value;

    public async Task<OfficeArtifactDownload> EnsureAsync(string artifactId,
        string fileName, string sha256, string downloadPath,
        CancellationToken cancellationToken)
    {
        if (Path.GetFileName(fileName) != fileName)
            throw new InvalidDataException("The Office artifact filename is invalid.");
        if (!downloadPath.StartsWith("/api/files/office/",
                StringComparison.OrdinalIgnoreCase)
            || downloadPath.Contains("://", StringComparison.Ordinal))
            throw new InvalidDataException("The Office artifact download path is invalid.");

        var stagingRoot = Path.GetFullPath(_options.StagingRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var artifactDirectory = Path.GetFullPath(
            Path.Combine(_options.StagingRoot, artifactId));
        if (!artifactDirectory.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Office artifact staging path is invalid.");

        Directory.CreateDirectory(artifactDirectory);
        var destination = Path.Combine(artifactDirectory, fileName);
        if (await IsApprovedLocalFileAsync(destination, sha256, cancellationToken))
            return new OfficeArtifactDownload(destination, false);

        var artifactUri = new Uri(hubUri, downloadPath);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        using var response = await client.GetAsync(artifactUri,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException(
                $"The control plane returned HTTP {(int)response.StatusCode} for the approved Office artifact.");
        if (response.Content.Headers.ContentLength is long length
            && length > _options.MaxDownloadBytes)
            throw new InvalidDataException(
                "The Office artifact exceeds the configured download limit.");

        var partial = destination + $".{Guid.NewGuid():N}.download";
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(partial, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous
                | FileOptions.SequentialScan))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    total += read;
                    if (total > _options.MaxDownloadBytes)
                        throw new InvalidDataException(
                            "The Office artifact exceeds the configured download limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
            }

            if (!await IsApprovedLocalFileAsync(partial, sha256, cancellationToken))
                throw new InvalidDataException(
                    "The downloaded Office artifact does not match its approved executable hash.");
            File.Move(partial, destination, overwrite: true);
            return new OfficeArtifactDownload(destination, true);
        }
        finally
        {
            try { File.Delete(partial); } catch { }
        }
    }

    internal static async Task<bool> IsApprovedLocalFileAsync(string path,
        string expectedSha256, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous
            | FileOptions.SequentialScan);
        var header = new byte[2];
        if (await stream.ReadAsync(header, cancellationToken) != 2
            || header[0] != (byte)'M' || header[1] != (byte)'Z')
            return false;
        stream.Position = 0;
        var actual = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
        return actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record OfficeArtifactDownload(string FilePath, bool Downloaded);
