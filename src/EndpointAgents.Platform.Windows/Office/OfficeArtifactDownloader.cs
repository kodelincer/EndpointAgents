using EndpointAgents.Platform.Windows;
using EndpointAgents.Shared.Office;
using EndpointAgents.Shared.Security;
using Microsoft.Extensions.Options;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace EndpointAgents.Platform.Windows.Office;

/// <summary>Stages one catalog-approved Office executable with atomic hash verification.</summary>
[SupportedOSPlatform("windows")]
public sealed class OfficeArtifactDownloader(
    IOptions<OfficeRemediationOptions> options,
    EndpointArtifactTransport transport)
{
    private readonly OfficeRemediationOptions _options = options.Value;

    public async Task<OfficeArtifactDownload> EnsureAsync(string artifactId,
        string fileName, string sha256, string downloadPath,
        CancellationToken cancellationToken, bool requirePortableExecutable = true,
        string? revisionSha256 = null)
    {
        if (Path.GetFileName(fileName) != fileName)
            throw new InvalidDataException("The Office artifact filename is invalid.");
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("The Office artifact SHA-256 is invalid.");
        if (revisionSha256 is not null
            && (revisionSha256.Length != 64
                || !revisionSha256.All(Uri.IsHexDigit)))
            throw new InvalidDataException("The Office artifact revision is invalid.");
        if (string.IsNullOrWhiteSpace(downloadPath)
            || (downloadPath != $"/api/files/office/{artifactId}"
                && downloadPath != $"/api/files/office/bugfix/{artifactId}"))
            throw new InvalidDataException("The Office artifact download path is invalid.");

        var rootPath = Path.GetFullPath(_options.StagingRoot);
        EnsureDirectory(rootPath, requireProtectedAcl: true);
        var stagingRoot = rootPath
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var artifactDirectory = Path.GetFullPath(
            Path.Combine(_options.StagingRoot, artifactId));
        if (!artifactDirectory.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Office artifact staging path is invalid.");
        var revisionDirectory = Path.GetFullPath(Path.Combine(artifactDirectory,
            (revisionSha256 ?? sha256).ToUpperInvariant()));
        var artifactRoot = artifactDirectory
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!revisionDirectory.StartsWith(artifactRoot,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Office artifact revision path is invalid.");

        Directory.CreateDirectory(revisionDirectory);
        EnsureDirectory(rootPath, requireProtectedAcl: true);
        EnsureDirectory(artifactDirectory,
            requireProtectedAcl: false);
        EnsureDirectory(revisionDirectory,
            requireProtectedAcl: false);
        var destination = Path.Combine(revisionDirectory, fileName);
        if (File.Exists(destination))
            EnsureFile(destination);
        if (await IsApprovedLocalFileAsync(destination, sha256, cancellationToken,
                requirePortableExecutable))
            return new OfficeArtifactDownload(destination, false);

        using var client = transport.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        using var request = await transport.CreateGetRequestAsync(downloadPath,
            cancellationToken);
        using var response = await client.SendAsync(request,
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

            if (!await IsApprovedLocalFileAsync(partial, sha256, cancellationToken,
                    requirePortableExecutable))
                throw new InvalidDataException(
                    "The downloaded Office artifact does not match its approved executable hash.");
            EnsureFile(partial);
            File.Move(partial, destination, overwrite: true);
            EnsureFile(destination);
            return new OfficeArtifactDownload(destination, true);
        }
        finally
        {
            try { File.Delete(partial); } catch { }
        }
    }

    public Task EnsureSupportFilesAsync(string batchPath,
        IReadOnlyList<BatchSupportFile>? files,
        CancellationToken cancellationToken) =>
        BatchSupportFileDownloader.EnsureAsync(batchPath, files, transport,
            _options.RequireProtectedAcl, cancellationToken);

    private void EnsureDirectory(string path, bool requireProtectedAcl)
    {
        if (_options.RequireProtectedAcl)
        {
            ProtectedWindowsPath.EnsureDirectory(path, requireProtectedAcl);
            return;
        }
        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(
                "The development Office remediation directory is redirected.");
    }

    private void EnsureFile(string path)
    {
        if (_options.RequireProtectedAcl)
        {
            ProtectedWindowsPath.EnsureFile(path);
            return;
        }
        if (!File.Exists(path)
            || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(
                "The development Office remediation file is missing or redirected.");
    }

    internal static async Task<bool> IsApprovedLocalFileAsync(string path,
        string expectedSha256, CancellationToken cancellationToken,
        bool requirePortableExecutable = true)
    {
        if (!File.Exists(path)) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous
            | FileOptions.SequentialScan);
        if (requirePortableExecutable)
        {
            var header = new byte[2];
            if (await stream.ReadAsync(header, cancellationToken) != 2
                || header[0] != (byte)'M' || header[1] != (byte)'Z')
                return false;
            stream.Position = 0;
        }
        var actual = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
        return actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record OfficeArtifactDownload(string FilePath, bool Downloaded);
