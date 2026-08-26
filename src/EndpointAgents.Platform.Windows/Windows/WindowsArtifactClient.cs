using EndpointAgents.Platform.Windows.Office;
using EndpointAgents.Shared.Security;
using EndpointAgents.Shared.Windows;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsArtifactClient(
    EndpointArtifactTransport transport,
    IOptions<WindowsRemediationOptions> options)
{
    private const long MaxDownloadBytes = 100 * 1024 * 1024;
    private const string CatalogPath = "/api/files/windows/bugfix/catalog";
    private readonly WindowsRemediationOptions _options = options.Value;

    public async Task<WindowsBugfixArtifactDescriptor?> GetArtifactAsync(
        CancellationToken cancellationToken)
        => await GetArtifactAsync(WindowsRemediationPaths.ArtifactId, cancellationToken);

    public async Task<WindowsBugfixArtifactDescriptor?> GetArtifactAsync(
        string artifactId, CancellationToken cancellationToken)
    {
        using var client = transport.CreateClient();
        using var request = await transport.CreateGetRequestAsync(CatalogPath,
            cancellationToken);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var catalog = await response.Content.ReadFromJsonAsync<WindowsBugfixCatalog>(
            cancellationToken: cancellationToken);
        return catalog?.Artifacts.SingleOrDefault(item =>
            item.ArtifactId == artifactId);
    }

    public async Task<(string Path, bool Downloaded)> EnsureAsync(
        WindowsBugfixArtifactDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        ValidateDescriptor(descriptor);
        var rootPath = Path.GetFullPath(_options.StagingRoot);
        EnsureDirectory(rootPath, requireProtectedAcl: true);
        var root = rootPath
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var artifactDirectory = Path.GetFullPath(Path.Combine(root,
            descriptor.ArtifactId));
        if (!artifactDirectory.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Windows remediation path is invalid.");
        var directory = Path.GetFullPath(Path.Combine(artifactDirectory,
            descriptor.Sha256.ToUpperInvariant()));
        var artifactRoot = artifactDirectory
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(artifactRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The Windows remediation revision path is invalid.");
        EnsureDirectory(artifactDirectory, requireProtectedAcl: false);
        EnsureDirectory(directory, requireProtectedAcl: false);

        var destination = Path.Combine(directory, descriptor.FileName);
        if (File.Exists(destination))
            EnsureFile(destination);
        if (File.Exists(destination)
            && await OfficeArtifactDownloader.IsApprovedLocalFileAsync(destination,
                descriptor.Sha256, cancellationToken, requirePortableExecutable: false))
            return (destination, false);

        using var client = transport.CreateClient();
        using var request = await transport.CreateGetRequestAsync(
            descriptor.DownloadPath, cancellationToken);
        using var response = await client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException(
                $"The control plane returned HTTP {(int)response.StatusCode} for the Windows remediation.");
        if (response.Content.Headers.ContentLength is long length
            && length > MaxDownloadBytes)
            throw new InvalidDataException("The Windows remediation exceeds the download limit.");

        var partial = destination + $".{Guid.NewGuid():N}.download";
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(partial, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    total += read;
                    if (total > MaxDownloadBytes)
                        throw new InvalidDataException(
                            "The Windows remediation exceeds the download limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            if (!await OfficeArtifactDownloader.IsApprovedLocalFileAsync(partial,
                    descriptor.Sha256, cancellationToken,
                    requirePortableExecutable: false))
                throw new InvalidDataException(
                    "The Windows remediation does not match its approved SHA-256.");
            EnsureFile(partial);
            File.Move(partial, destination, overwrite: true);
            EnsureFile(destination);
            return (destination, true);
        }
        finally
        {
            try { File.Delete(partial); } catch { }
        }
    }

    private void EnsureDirectory(string path, bool requireProtectedAcl)
    {
        if (_options.RequireProtectedAcl)
        {
            if (!requireProtectedAcl) Directory.CreateDirectory(path);
            ProtectedWindowsPath.EnsureDirectory(path, requireProtectedAcl);
            return;
        }
        Directory.CreateDirectory(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(
                "The development Windows remediation directory is redirected.");
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
                "The development Windows remediation file is missing or redirected.");
    }

    private static void ValidateDescriptor(WindowsBugfixArtifactDescriptor descriptor)
    {
        var approved = descriptor.ArtifactId switch
        {
            WindowsRemediationPaths.ArtifactId =>
                descriptor.FileName == WindowsRemediationPaths.FileName
                && descriptor.DownloadPath ==
                    "/api/files/windows/bugfix/windows-activation-repair",
            WindowsHelloSamplePaths.ArtifactId =>
                descriptor.FileName == WindowsHelloSamplePaths.FileName
                && descriptor.DownloadPath ==
                    "/api/files/windows/bugfix/windows-hello-sample",
            _ => false
        };
        if (!approved
            || descriptor.Sha256.Length != 64
            || !descriptor.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException(
                "The control plane returned an invalid Windows remediation descriptor.");
    }
}
