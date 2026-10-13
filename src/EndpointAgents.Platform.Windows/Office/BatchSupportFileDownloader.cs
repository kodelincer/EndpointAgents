using EndpointAgents.Shared.Office;
using EndpointAgents.Shared.Security;
using System.Net;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Office;

[SupportedOSPlatform("windows")]
public static class BatchSupportFileDownloader
{
    // Large enough for a vendor MSI such as Chrome Enterprise (about 170 MB).
    private const long MaxFileBytes = 500 * 1024 * 1024;

    public static async Task EnsureAsync(string batchPath,
        IReadOnlyList<BatchSupportFile>? files,
        EndpointArtifactTransport transport, bool requireProtectedAcl,
        CancellationToken cancellationToken)
    {
        if (files is not { Count: > 0 }) return;
        var directory = Path.GetDirectoryName(Path.GetFullPath(batchPath))!;
        if (requireProtectedAcl)
            ProtectedWindowsPath.EnsureDirectory(directory,
                requireProtectedAcl: false);
        else if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The batch support directory is redirected.");

        using var client = transport.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(10);
        foreach (var file in files)
        {
            if (!BatchSupportFilePolicy.IsValidName(file.FileName)
                || !BatchSupportFilePolicy.IsSha256(file.Sha256))
                throw new InvalidDataException("A batch support file is invalid.");
            var destination = Path.Combine(directory, file.FileName);
            if (Path.Exists(destination) && !File.Exists(destination))
                throw new InvalidDataException("A batch support file is redirected or invalid.");
            if (File.Exists(destination)
                && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A batch support file is redirected.");
            if (File.Exists(destination)
                && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) == 0
                && await OfficeArtifactDownloader.IsApprovedLocalFileAsync(
                    destination, file.Sha256, cancellationToken,
                    requirePortableExecutable: false))
                continue;

            using var request = await transport.CreateGetRequestAsync(
                file.DownloadPath, cancellationToken);
            using var response = await client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK
                || response.Content.Headers.ContentLength is > MaxFileBytes)
                throw new InvalidDataException(
                    "A batch support file could not be downloaded.");
            var partial = destination + $".{Guid.NewGuid():N}.download";
            try
            {
                await using (var input = await response.Content.ReadAsStreamAsync(
                                 cancellationToken))
                await using (var output = new FileStream(partial,
                                 FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 64 * 1024, FileOptions.Asynchronous))
                {
                    var buffer = new byte[64 * 1024];
                    long total = 0;
                    while (true)
                    {
                        var count = await input.ReadAsync(buffer, cancellationToken);
                        if (count == 0) break;
                        total += count;
                        if (total > MaxFileBytes)
                            throw new InvalidDataException(
                                "A batch support file exceeds the size limit.");
                        await output.WriteAsync(buffer.AsMemory(0, count),
                            cancellationToken);
                    }
                }
                if (!await OfficeArtifactDownloader.IsApprovedLocalFileAsync(
                        partial, file.Sha256, cancellationToken,
                        requirePortableExecutable: false))
                    throw new InvalidDataException(
                        "A batch support file has a hash mismatch.");
                if (requireProtectedAcl) ProtectedWindowsPath.EnsureFile(partial);
                File.Move(partial, destination, overwrite: true);
                if (requireProtectedAcl) ProtectedWindowsPath.EnsureFile(destination);
            }
            finally { try { File.Delete(partial); } catch { } }
        }
    }

    public static async Task<bool> AreApprovedAsync(string batchPath,
        IReadOnlyList<BatchSupportFile>? files, bool requireProtectedAcl,
        CancellationToken cancellationToken)
    {
        if (files is not { Count: > 0 }) return true;
        var directory = Path.GetDirectoryName(Path.GetFullPath(batchPath))!;
        foreach (var file in files)
        {
            var path = Path.Combine(directory, file.FileName);
            if (!File.Exists(path)
                || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                || requireProtectedAcl && !ProtectedWindowsPath.IsFileSecure(path)
                || !await OfficeArtifactDownloader.IsApprovedLocalFileAsync(
                    path, file.Sha256, cancellationToken,
                    requirePortableExecutable: false))
                return false;
        }
        return true;
    }
}
