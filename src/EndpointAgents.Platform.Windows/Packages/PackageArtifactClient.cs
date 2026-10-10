using EndpointAgents.Platform.Windows.Office;
using EndpointAgents.Platform.Windows.Windows;
using EndpointAgents.Shared.Packages;
using EndpointAgents.Shared.Security;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Packages;

/// <summary>
/// Reads the general repair catalog and stages one package (file plus supporting
/// files) under <c>&lt;Windows remediation staging root&gt;\packages\&lt;id&gt;\&lt;revision&gt;</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PackageArtifactClient(
    EndpointArtifactTransport transport,
    IOptions<WindowsRemediationOptions> options)
{
    public const string StagingDirectoryName = "packages";
    private const long MaxDownloadBytes = 500 * 1024 * 1024;
    private readonly WindowsRemediationOptions _options = options.Value;

    public bool RequireProtectedAcl => _options.RequireProtectedAcl;

    public bool RequireAdministrator => _options.RequireAdministrator;

    public string StagingRoot => Path.Combine(Path.GetFullPath(_options.StagingRoot),
        StagingDirectoryName);

    public async Task<IReadOnlyList<PackageDescriptor>> GetCatalogAsync(
        CancellationToken cancellationToken)
    {
        using var client = transport.CreateClient();
        using var request = await transport.CreateGetRequestAsync(PackageRoutes.Catalog,
            cancellationToken);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var catalog = await response.Content.ReadFromJsonAsync<PackageCatalog>(
            cancellationToken: cancellationToken);
        return catalog?.Packages ?? [];
    }

    /// <summary>The protected path a verified package file is staged at.</summary>
    public string GetStagedPath(PackageDescriptor descriptor) => Path.Combine(StagingRoot,
        descriptor.PackageId,
        (descriptor.BundleSha256 ?? descriptor.Sha256).ToUpperInvariant(),
        descriptor.FileName);

    public async Task<(string Path, bool Downloaded)> EnsureAsync(
        PackageDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (!PackageDescriptorPolicy.TryValidate(descriptor, out _))
            throw new InvalidDataException(
                "The control plane returned an invalid package descriptor.");
        var root = StagingRoot;
        EnsureDirectory(Path.GetFullPath(_options.StagingRoot), requireProtectedAcl: true);
        EnsureDirectory(root, requireProtectedAcl: true);
        var destination = GetStagedPath(descriptor);
        var packageDirectory = Path.Combine(root, descriptor.PackageId);
        var directory = Path.GetDirectoryName(destination)!;
        if (!Path.GetFullPath(destination).StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The package staging path is invalid.");
        EnsureDirectory(packageDirectory, requireProtectedAcl: false);
        EnsureDirectory(directory, requireProtectedAcl: false);

        if (File.Exists(destination))
        {
            EnsureFile(destination);
            if (await OfficeArtifactDownloader.IsApprovedLocalFileAsync(destination,
                    descriptor.Sha256, cancellationToken, requirePortableExecutable: false))
            {
                await BatchSupportFileDownloader.EnsureAsync(destination,
                    descriptor.SupportFiles, transport, _options.RequireProtectedAcl,
                    cancellationToken);
                return (destination, false);
            }
        }

        using var client = transport.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(10);
        using var request = await transport.CreateGetRequestAsync(
            descriptor.DownloadPath, cancellationToken);
        using var response = await client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException(
                $"The control plane returned HTTP {(int)response.StatusCode} for the package.");
        if (response.Content.Headers.ContentLength is long length
            && length > MaxDownloadBytes)
            throw new InvalidDataException("The package exceeds the download limit.");

        var partial = destination + $".{Guid.NewGuid():N}.download";
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(
                             cancellationToken))
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
                            "The package exceeds the download limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            if (!await OfficeArtifactDownloader.IsApprovedLocalFileAsync(partial,
                    descriptor.Sha256, cancellationToken,
                    requirePortableExecutable: false))
                throw new InvalidDataException(
                    "The downloaded package does not match its approved SHA-256.");
            EnsureFile(partial);
            File.Move(partial, destination, overwrite: true);
            EnsureFile(destination);
            await BatchSupportFileDownloader.EnsureAsync(destination,
                descriptor.SupportFiles, transport, _options.RequireProtectedAcl,
                cancellationToken);
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
            throw new InvalidDataException("The development package directory is redirected.");
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
                "The development package file is missing or redirected.");
    }
}
