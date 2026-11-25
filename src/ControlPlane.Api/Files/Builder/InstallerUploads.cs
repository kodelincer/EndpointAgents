using EndpointAgents.Shared.Office;
using EndpointAgents.Shared.Packages;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ControlPlane.Api.Files.Builder;

/// <summary>
/// What the builder page learns from an uploaded installer. Every value is a suggestion the
/// person can change, except the hash and the signer, which come from the file itself.
/// </summary>
/// <param name="FileType"><c>msi</c> or <c>exe</c>.</param>
/// <param name="Technology">The installer kind to run it with: msi, inno, nsis, wix-burn, installshield, squirrel, or exe (unknown: the vendor's switches are needed).</param>
/// <param name="ProductName">The product name read from the EXE, cleaned of version numbers, as a detection suggestion.</param>
/// <param name="Signer">The Authenticode signer subject, or null when the file is not signed.</param>
public sealed record InstallerInfo(
    string UploadId,
    string FileName,
    long SizeBytes,
    string Sha256,
    string FileType,
    string Technology,
    IReadOnlyList<string> SilentSwitches,
    string? ProductName,
    string? Signer,
    IReadOnlyList<string> Notes);

/// <summary>A supporting file in a package's files\ folder, or one just uploaded for it.</summary>
/// <param name="UploadId">Set while the file is an upload that is not saved into a package yet.</param>
/// <param name="Screens">Menus found in it, for a BAT, CMD or text file the BAT shows while it runs.</param>
public sealed record SupportFileInfo(
    string? UploadId,
    string FileName,
    long SizeBytes,
    string Sha256,
    string? Signer,
    IReadOnlyList<BatScreenSuggestion> Screens,
    IReadOnlyList<string> Notes);

/// <summary>
/// Installers uploaded from the builder page, kept beside the packages folder until the
/// package is saved (or for a day). The body is streamed to disk, so large installers do
/// not pass through memory. Nothing here is ever run.
/// </summary>
public sealed partial class InstallerUploads(FileArtifactRepository repository,
    IOptions<FileRepositoryOptions> options, TimeProvider clock)
{
    private const string InfoFileName = "upload.json";
    private const string SupportInfoFileName = "support-file.json";
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(1);

    public long MaximumBytes => options.Value.MaxFileSizeBytes;

    private string Root => Path.Combine(PackageBuilder.StagingRoot(repository), "uploads");

    public async Task<(InstallerInfo? Info, string? Error)> SaveAsync(Stream body, string? requestedName,
        CancellationToken cancellationToken)
    {
        var fileName = SafeFileName(requestedName);
        if (fileName is null)
            return (null, "Choose an .msi or .exe installer.");
        var (stored, error) = await StoreAsync(body, fileName, "installer", cancellationToken);
        if (stored is not { } file) return (null, error);
        return await WriteInfoAsync(file.Folder, InfoFileName,
            Inspect(file.Id, file.Path, fileName, file.Size, file.Sha256), cancellationToken);
    }

    /// <summary>A supporting file for a BAT package (files\), such as a tool, a CAB or a menu text file.</summary>
    public async Task<(SupportFileInfo? Info, string? Error)> SaveSupportAsync(Stream body, string? requestedName,
        CancellationToken cancellationToken)
    {
        var fileName = SafeSupportFileName(requestedName);
        if (fileName is null)
            return (null, "The file name must use letters, numbers, dots, underscores or hyphens (up to 128 characters).");
        var (stored, error) = await StoreAsync(body, fileName, "file", cancellationToken);
        if (stored is not { } file) return (null, error);
        return await WriteInfoAsync(file.Folder, SupportInfoFileName,
            DescribeSupportFile(file.Path, fileName, file.Size, file.Sha256, file.Id), cancellationToken);
    }

    private static async Task<(T? Info, string? Error)> WriteInfoAsync<T>(string folder, string name, T info,
        CancellationToken cancellationToken) where T : class
    {
        await global::System.IO.File.WriteAllTextAsync(Path.Combine(folder, name),
            JsonSerializer.Serialize(info, PackageBuilder.Json), cancellationToken);
        return (info, null);
    }

    /// <summary>Streams the body to a new upload folder while hashing it; never keeps a partial file.</summary>
    private async Task<((string Id, string Folder, string Path, long Size, string Sha256)? File, string? Error)>
        StoreAsync(Stream body, string fileName, string what, CancellationToken cancellationToken)
    {
        RemoveOld();
        var id = Guid.NewGuid().ToString("N");
        var folder = Path.Combine(Root, id);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        try
        {
            long size;
            string sha256;
            var tooLarge = false;
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 81920, useAsync: true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                size = 0;
                int read;
                while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    size += read;
                    if (size > MaximumBytes)
                    {
                        tooLarge = true; // delete only after the file is closed
                        break;
                    }
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                sha256 = Convert.ToHexString(hash.GetHashAndReset());
            }
            if (tooLarge)
            {
                Directory.Delete(folder, recursive: true);
                return (null, $"The {what} is larger than {MaximumBytes / 1024 / 1024} MB.");
            }
            if (size == 0)
            {
                Directory.Delete(folder, recursive: true);
                return (null, "The file is empty.");
            }
            return ((id, folder, path, size, sha256), null);
        }
        catch
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            throw;
        }
    }

    /// <summary>An uploaded supporting file, or null when unknown or expired.</summary>
    public (string Path, SupportFileInfo Info)? GetSupport(string? uploadId)
    {
        if (uploadId is null || !UploadIdPattern().IsMatch(uploadId)) return null;
        var folder = Path.Combine(Root, uploadId);
        var infoPath = Path.Combine(folder, SupportInfoFileName);
        if (!global::System.IO.File.Exists(infoPath)) return null;
        var info = JsonSerializer.Deserialize<SupportFileInfo>(global::System.IO.File.ReadAllText(infoPath), PackageBuilder.Json);
        if (info is null || SafeSupportFileName(info.FileName) != info.FileName) return null;
        var path = Path.Combine(folder, info.FileName);
        return global::System.IO.File.Exists(path) ? (path, info) : null;
    }

    /// <summary>The hash, signer and (for text files) the menus of a supporting file.</summary>
    public static SupportFileInfo DescribeSupportFile(string path, string fileName, long size, string sha256,
        string? uploadId)
    {
        var notes = new List<string>();
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        string? signer = null;
        if (extension is ".exe" or ".dll" or ".msi" or ".cab" or ".sys")
        {
            signer = ExecutableInspector.ReadSignerSubject(path);
            if (signer is null)
                notes.Add("This file has no vendor (Authenticode) signature. It is still pinned by its hash, so it cannot change after review.");
        }
        IReadOnlyList<BatScreenSuggestion> screens = [];
        if (extension is ".bat" or ".cmd" or ".txt" or ".ini" or ".cfg" or ".nfo" or ".lst"
            && size <= PackageBuilder.MaximumFileBytes)
        {
            var text = PackageBuilder.DecodeText(global::System.IO.File.ReadAllBytes(path), out _);
            screens = extension is ".bat" or ".cmd"
                ? BatMenuScanner.Scan(text).Select(screen => screen with
                    { Notes = [$"Read from {fileName}, which the BAT runs.", .. screen.Notes] }).ToArray()
                : BatMenuScanner.ScanPlainText(text, fileName);
        }
        return new SupportFileInfo(uploadId, fileName, size, sha256, signer, screens, notes);
    }

    /// <summary>The uploaded file and what was read from it, or null when unknown or expired.</summary>
    public (string Path, InstallerInfo Info)? Get(string? uploadId)
    {
        if (uploadId is null || !UploadIdPattern().IsMatch(uploadId)) return null;
        var folder = Path.Combine(Root, uploadId);
        var infoPath = Path.Combine(folder, InfoFileName);
        if (!global::System.IO.File.Exists(infoPath)) return null;
        var info = JsonSerializer.Deserialize<InstallerInfo>(global::System.IO.File.ReadAllText(infoPath), PackageBuilder.Json);
        if (info is null) return null;
        var path = Path.Combine(folder, info.FileName);
        return global::System.IO.File.Exists(path) ? (path, info) : null;
    }

    public void Remove(string uploadId)
    {
        if (!UploadIdPattern().IsMatch(uploadId)) return;
        var folder = Path.Combine(Root, uploadId);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    private void RemoveOld()
    {
        if (!Directory.Exists(Root)) return;
        var cutoff = clock.GetUtcNow().UtcDateTime - KeepFor;
        foreach (var folder in Directory.EnumerateDirectories(Root))
            try
            {
                if (Directory.GetCreationTimeUtc(folder) < cutoff) Directory.Delete(folder, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
    }

    /// <summary>Letters, digits, '.', '_' and '-' only, as package supporting files must be named.</summary>
    public static string? SafeSupportFileName(string? requested)
    {
        var name = UnsafeCharacters().Replace(Path.GetFileName(requested?.Trim() ?? string.Empty), "-");
        return name.StartsWith('.') || name.EndsWith('.') || !BatchSupportFilePolicy.IsValidName(name) ? null : name;
    }

    /// <summary>Letters, digits, '.', '_' and '-' only, like new-install-package.ps1; .msi or .exe.</summary>
    public static string? SafeFileName(string? requested)
    {
        var name = Path.GetFileName(requested?.Trim() ?? string.Empty);
        name = UnsafeCharacters().Replace(name, "-");
        if (name.Length is 0 or > 128 || name.StartsWith('.')) return null;
        var extension = Path.GetExtension(name).ToLowerInvariant();
        return extension is ".msi" or ".exe" ? name : null;
    }

    public static InstallerInfo Inspect(string uploadId, string path, string fileName, long size, string sha256)
    {
        var notes = new List<string>();
        var fileType = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        var signer = ExecutableInspector.ReadSignerSubject(path);
        if (signer is null)
            notes.Add("The installer has no vendor (Authenticode) signature, or it could not be read. PCs refuse unsigned installers, so ask the vendor for a signed one.");
        string technology;
        string? productName = null;
        if (fileType == PackageFileTypes.Msi)
        {
            technology = InstallerKinds.Msi;
            notes.Add("Type the name the app shows in Apps & features (Settings > Apps); the PC uses it to check the install worked.");
        }
        else
        {
            string? versionText = null;
            if (OperatingSystem.IsWindows())
            {
                var version = FileVersionInfo.GetVersionInfo(path);
                productName = CleanProductName(version.ProductName);
                versionText = $"{version.Comments} {version.ProductName} {version.LegalCopyright} {version.FileDescription}";
            }
            technology = DetectTechnology(path, versionText);
            if (technology == InstallerKinds.Exe)
                notes.Add("The installer type was not recognised. Type the vendor's silent-install switches (from its documentation) below.");
        }
        return new InstallerInfo(uploadId, fileName, size, sha256, fileType, technology,
            InstallerKinds.SilentSwitches(technology), productName, signer, notes);
    }

    /// <summary>The same markers new-install-package.ps1 looks for, in the first 16 MB.</summary>
    public static string DetectTechnology(string path, string? versionText)
    {
        string text;
        using (var stream = global::System.IO.File.OpenRead(path))
        {
            var buffer = new byte[(int)Math.Min(stream.Length, 16 * 1024 * 1024)];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            text = Encoding.Latin1.GetString(buffer, 0, read);
        }
        versionText ??= string.Empty;
        if (versionText.Contains("Inno Setup", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Inno Setup Setup Data", StringComparison.Ordinal)) return InstallerKinds.Inno;
        if (text.Contains("Nullsoft.NSIS", StringComparison.Ordinal)
            || text.Contains("NullsoftInst", StringComparison.Ordinal)) return InstallerKinds.Nsis;
        if (text.Contains(".wixburn", StringComparison.Ordinal)) return InstallerKinds.WixBurn;
        if (versionText.Contains("InstallShield", StringComparison.OrdinalIgnoreCase)
            || text.Contains("InstallShield", StringComparison.Ordinal)) return InstallerKinds.InstallShield;
        if (text.Contains("SquirrelSetup", StringComparison.Ordinal)
            || text.Contains("Squirrel.Windows", StringComparison.Ordinal)) return InstallerKinds.Squirrel;
        return InstallerKinds.Exe;
    }

    /// <summary>"7-Zip 24.08 (x64)" becomes "7-Zip": version numbers change with every release.</summary>
    public static string? CleanProductName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var cleaned = ArchitecturePattern().Replace(name, "");
        cleaned = VersionTailPattern().Replace(cleaned, "").Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex UploadIdPattern();

    [GeneratedRegex("[^A-Za-z0-9._-]", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeCharacters();

    [GeneratedRegex(@"\s*\(?(x64|x86|64-bit|32-bit)\)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArchitecturePattern();

    [GeneratedRegex(@"\s+v?\d+(\.\d+)+.*$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionTailPattern();
}
