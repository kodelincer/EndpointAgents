using EndpointAgents.Shared.Office;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ControlPlane.Api.Files.Builder;

/// <summary>An uploaded BAT, sent by the dashboard as base64 inside JSON.</summary>
public sealed record PackageFileUpload(string FileName, string FileBase64);

/// <summary>
/// Everything the "Package builder" page sends to save a package. The person fills in
/// every answer; the control plane only adds the hashes and writes the folder.
/// </summary>
public sealed record PackageBuildRequest(
    string Id,
    string Kind,
    string DisplayName,
    string Description,
    BatchPlaybook Playbook,
    PackageFileUpload? File = null,
    bool Replace = false,
    IReadOnlyList<string>? Symptoms = null,
    string? Problem = null,
    int? Order = null,
    bool RequiresApproval = false,
    BatPackageOffice? Office = null,
    BatPackageWindows? Windows = null,
    PackageCompatibilityManifest? Compatibility = null,
    BatPackageReview? Review = null);

/// <summary>An existing package loaded back into the builder form.</summary>
public sealed record PackageBuilderDraft(string Kind, BatPackageManifest Manifest,
    BatScanResult? Scan, bool Signed);

/// <summary>The builder's answer: either the saved package's status or why nothing was saved.</summary>
public sealed record PackageBuildResult(bool Saved, string? Error, BatPackageStatus? Status)
{
    public static PackageBuildResult Fail(string error) => new(false, error, null);
}

/// <summary>
/// Builds package folders from the dashboard form, in the same format as
/// <c>scripts\new-package.ps1</c> followed by <c>test-bat-playbook.ps1 -Fix</c>: the BAT
/// unchanged, and <c>package.json</c> with the file and playbook hashes pinned. The
/// package is saved unsigned, so endpoints cannot get it until it is signed.
/// </summary>
public sealed partial class PackageBuilder(FileArtifactRepository repository, TimeProvider clock)
{
    public const string General = "general";
    public const string Office = "office";
    public const string Windows = "windows";
    public const int MaximumFileBytes = 8 * 1024 * 1024;
    private const int MaximumSymptoms = 32;
    private const int MaximumSymptomCharacters = 200;
    private const string StagingDirectoryName = ".package-builder";

    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // Keeps Persian symptoms readable in package.json, like PlaybookLab --fix.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Reads an uploaded BAT and suggests its screens. Nothing is saved.</summary>
    public static (BatScanResult? Result, string? Error) Scan(PackageFileUpload? upload)
    {
        if (!TryReadUpload(upload, out var fileName, out var bytes, out var error))
            return (null, error);
        return (ScanBytes(fileName!, bytes!), null);
    }

    public static BatScanResult ScanBytes(string fileName, byte[] bytes)
    {
        var text = DecodeText(bytes, out var decodeNote);
        var lint = BatScriptLint.Inspect(text);
        var screens = BatMenuScanner.Scan(text);
        var notes = new List<string>();
        if (decodeNote is not null) notes.Add(decodeNote);
        if (screens.Count == 0)
            notes.Add("No menus or questions were found in the text. If the BAT needs no answers, leave the steps empty; otherwise run it on a test PC to see its screens.");
        if (screens.Count >= BatMenuScanner.MaximumScreens)
            notes.Add($"Only the first {BatMenuScanner.MaximumScreens} screens are listed.");
        var lineCount = text.Count(character => character == '\n') + 1;
        return new BatScanResult(fileName, Sha256(bytes), bytes.LongLength, lineCount, screens,
            lint.Errors, lint.Warnings, notes);
    }

    /// <summary>Loads an existing package's package.json (and a scan of its BAT) into the form.</summary>
    public async Task<PackageBuilderDraft?> LoadAsync(string id, CancellationToken cancellationToken)
    {
        if (!IsValidId(id)) return null;
        var folder = Path.Combine(repository.PackagesRootPath, id);
        var manifestPath = Path.Combine(folder, FileArtifactRepository.PackageManifestFileName);
        if (!global::System.IO.File.Exists(manifestPath) || IsRedirected(folder)) return null;
        BatPackageManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<BatPackageManifest>(
                await global::System.IO.File.ReadAllTextAsync(manifestPath, cancellationToken), Json);
        }
        catch (JsonException) { return null; }
        if (manifest is null) return null;
        var kind = manifest.SchemaVersion == 2 ? General
            : manifest.Office is not null ? Office : Windows;
        var fileName = manifest.SchemaVersion == 2 ? manifest.File?.FileName : manifest.Bat?.FileName;
        BatScanResult? scan = null;
        if (fileName is { Length: > 0 } && Path.GetFileName(fileName) == fileName
            && fileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            var path = Path.Combine(folder, fileName);
            var info = new FileInfo(path);
            if (info.Exists && info.Length is > 0 and <= MaximumFileBytes
                && (info.Attributes & FileAttributes.ReparsePoint) == 0)
                scan = ScanBytes(fileName, await global::System.IO.File.ReadAllBytesAsync(path, cancellationToken));
        }
        var signed = manifest.Signature is not null;
        manifest.Signature = null;
        return new PackageBuilderDraft(kind, manifest, scan, signed);
    }

    /// <summary>Validates the form, writes the package folder, and returns its status.</summary>
    /// <param name="author">The signed-in operator, recorded as the reviewer when none is given.</param>
    /// <param name="mayReplaceSigned">Admins may replace a signed (published) package.</param>
    public async Task<PackageBuildResult> SaveAsync(PackageBuildRequest request, string author,
        bool mayReplaceSigned, CancellationToken cancellationToken)
    {
        var id = request.Id?.Trim() ?? string.Empty;
        if (!IsValidId(id))
            return PackageBuildResult.Fail("The id must be 1 to 64 lowercase letters, numbers, dots, underscores or hyphens, start and end with a letter or number, such as \"office-reseller-fix\".");
        var kind = request.Kind?.Trim().ToLowerInvariant() ?? string.Empty;
        if (kind is not (General or Office or Windows))
            return PackageBuildResult.Fail("Choose what the package is for: general repair, Office activation or Windows activation.");
        var displayName = request.DisplayName?.Trim() ?? string.Empty;
        var description = request.Description?.Trim() ?? string.Empty;
        if (displayName.Length is 0 or > 120)
            return PackageBuildResult.Fail("Give the package a name of 1 to 120 characters.");
        if (description.Length is 0 or > 500)
            return PackageBuildResult.Fail("Describe what the package does in 1 to 500 characters.");
        if (ContainsTodo(request))
            return PackageBuildResult.Fail("Replace every TODO in the form first.");

        // The playbook version follows from the kind, like new-package.ps1 does.
        var source = request.Playbook ?? new BatchPlaybook(3, []);
        var steps = source.Steps ?? [];
        var playbook = kind == General || steps.Count == 0
            ? source with { Version = 3, Steps = steps }
            : source with
            {
                Version = 2, Steps = steps, Arguments = null, TimeoutSeconds = null,
                RestartExitCodes = null
            };
        if (!BatchPlaybookPolicy.TryNormalize(playbook, out var normalized, out var playbookError))
            return PackageBuildResult.Fail($"Playbook: {playbookError}");

        var symptoms = (request.Symptoms ?? []).Select(symptom => symptom?.Trim() ?? string.Empty)
            .Where(symptom => symptom.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (kind == General && symptoms.Count == 0)
            return PackageBuildResult.Fail("Add at least one way a user would describe the problem.");
        if (symptoms.Count > MaximumSymptoms || symptoms.Any(symptom => symptom.Length > MaximumSymptomCharacters))
            return PackageBuildResult.Fail($"Use at most {MaximumSymptoms} problem descriptions of up to {MaximumSymptomCharacters} characters each.");
        if (kind == Office && request.Office is null)
            return PackageBuildResult.Fail("Fill in which Office versions the package fits.");
        if (kind == Windows && request.Windows is null)
            return PackageBuildResult.Fail("Fill in which Windows versions the package fits.");

        PackageFileUpload? upload = request.File;
        string? fileName = null;
        byte[]? bytes = null;
        if (upload is not null && !TryReadUpload(upload, out fileName, out bytes, out var uploadError))
            return PackageBuildResult.Fail(uploadError!);

        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            var root = repository.PackagesRootPath;
            var folder = Path.Combine(root, id);
            BatPackageManifest? existing = null;
            if (Directory.Exists(folder))
            {
                if (!request.Replace)
                    return PackageBuildResult.Fail($"A package with the id \"{id}\" already exists. Open it in the builder to change it, or pick another id.");
                if (IsRedirected(folder))
                    return PackageBuildResult.Fail("The existing package folder is redirected; fix it on the server first.");
                existing = await ReadManifestAsync(folder, cancellationToken);
                if (existing?.Signature is not null && !mayReplaceSigned)
                    return PackageBuildResult.Fail("This package is signed and may be in use. Only an admin can change it.");
            }

            if (bytes is null)
            {
                // Keep the BAT that is already in the package.
                var keptName = existing?.SchemaVersion == 2 ? existing.File?.FileName : existing?.Bat?.FileName;
                if (existing is null || keptName is not { Length: > 0 } || Path.GetFileName(keptName) != keptName
                    || !keptName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                    || !global::System.IO.File.Exists(Path.Combine(folder, keptName)))
                    return PackageBuildResult.Fail("Upload the BAT file.");
                fileName = keptName;
                bytes = await global::System.IO.File.ReadAllBytesAsync(Path.Combine(folder, keptName), cancellationToken);
            }

            var review = request.Review ?? new BatPackageReview();
            var manifest = new BatPackageManifest
            {
                SchemaVersion = kind == General ? 2 : 1,
                Id = id,
                DisplayName = displayName,
                Description = description,
                Problem = string.IsNullOrWhiteSpace(request.Problem) ? null : request.Problem.Trim(),
                Order = request.Order,
                RequiresApproval = request.RequiresApproval,
                // Supporting files are not managed by the builder; keep the existing ones.
                SupportFiles = existing?.SupportFiles ?? [],
                Playbook = normalized,
                PlaybookSha256 = BatchPlaybookPolicy.ComputeSha256(normalized!),
                Review = new BatPackageReview
                {
                    ReviewedBy = string.IsNullOrWhiteSpace(review.ReviewedBy) ? author : review.ReviewedBy.Trim(),
                    ReviewedOn = string.IsNullOrWhiteSpace(review.ReviewedOn)
                        ? clock.GetUtcNow().ToString("yyyy-MM-dd", global::System.Globalization.CultureInfo.InvariantCulture)
                        : review.ReviewedOn.Trim(),
                    Source = review.Source?.Trim(),
                    Notes = string.IsNullOrWhiteSpace(review.Notes)
                        ? $"Made with the dashboard package builder by {author}."
                        : review.Notes.Trim(),
                    AcceptedRisks = review.AcceptedRisks ?? []
                }
            };
            var fileSha256 = Sha256(bytes);
            if (kind == General)
            {
                manifest.Symptoms = symptoms;
                manifest.File = new PackageFileManifest { Type = "bat", FileName = fileName!, Sha256 = fileSha256 };
                manifest.Compatibility = request.Compatibility;
            }
            else
            {
                manifest.Bat = new BatPackageFile { FileName = fileName!, Sha256 = fileSha256 };
                manifest.Office = kind == Office ? request.Office : null;
                manifest.Windows = kind == Windows ? request.Windows : null;
            }

            await WriteFolderAsync(root, folder, fileName!, bytes, manifest,
                existing is not null, cancellationToken);
        }
        finally
        {
            WriteLock.Release();
        }

        var status = (await repository.GetPackageStatusesAsync(cancellationToken))
            .FirstOrDefault(item => item.Id == id);
        return new PackageBuildResult(true, null, status);
    }

    /// <summary>
    /// Builds the new folder next to the packages folder, then swaps it in, so the
    /// catalog never sees a half-written package.
    /// </summary>
    private static async Task WriteFolderAsync(string root, string folder, string fileName,
        byte[] bytes, BatPackageManifest manifest, bool replace, CancellationToken cancellationToken)
    {
        var staging = Path.Combine(Path.GetDirectoryName(root)!, StagingDirectoryName);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(staging);
        var building = Path.Combine(staging, $"{manifest.Id}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(building);
        try
        {
            await global::System.IO.File.WriteAllBytesAsync(Path.Combine(building, fileName), bytes, cancellationToken);
            await global::System.IO.File.WriteAllTextAsync(
                Path.Combine(building, FileArtifactRepository.PackageManifestFileName),
                JsonSerializer.Serialize(manifest, Json) + Environment.NewLine,
                new UTF8Encoding(false), cancellationToken);
            var support = Path.Combine(folder, FileArtifactRepository.PackageSupportDirectoryName);
            if (replace && Directory.Exists(support) && !IsRedirected(support))
                CopyDirectory(support, Path.Combine(building, FileArtifactRepository.PackageSupportDirectoryName));

            if (replace)
            {
                var old = Path.Combine(staging, $"{manifest.Id}-old-{Guid.NewGuid():N}");
                Directory.Move(folder, old);
                try { Directory.Move(building, folder); }
                catch
                {
                    Directory.Move(old, folder);
                    throw;
                }
                Directory.Delete(old, recursive: true);
            }
            else Directory.Move(building, folder);
        }
        finally
        {
            if (Directory.Exists(building)) Directory.Delete(building, recursive: true);
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
            global::System.IO.File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
    }

    private static async Task<BatPackageManifest?> ReadManifestAsync(string folder,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(folder, FileArtifactRepository.PackageManifestFileName);
        if (!global::System.IO.File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<BatPackageManifest>(
                await global::System.IO.File.ReadAllTextAsync(path, cancellationToken), Json);
        }
        catch (JsonException) { return null; }
    }

    private static bool TryReadUpload(PackageFileUpload? upload, out string? fileName,
        out byte[]? bytes, out string? error)
    {
        fileName = null;
        bytes = null;
        error = null;
        var name = Path.GetFileName(upload?.FileName?.Trim() ?? string.Empty);
        if (upload is null || string.IsNullOrWhiteSpace(upload.FileBase64))
            return Fail("Choose a .bat file to upload.", out error);
        if (name.Length is 0 or > 100 || !name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
            || name != upload.FileName.Trim() || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Any(character => character is '\\' or '/' or ':' or '"' or '<' or '>' or '|' or '?' or '*')
            || name.StartsWith('.'))
            return Fail("The file must be a .bat file with a plain name of up to 100 characters.", out error);
        try { bytes = Convert.FromBase64String(upload.FileBase64); }
        catch (FormatException) { return Fail("The file could not be read.", out error); }
        if (bytes.Length == 0) return Fail("The file is empty.", out error);
        if (bytes.Length > MaximumFileBytes)
            return Fail($"The file is larger than {MaximumFileBytes / 1024 / 1024} MB.", out error);
        fileName = name;
        return true;
    }

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }

    /// <summary>UTF-8 when the bytes are valid UTF-8; otherwise each byte as one character.</summary>
    private static string DecodeText(byte[] bytes, out string? note)
    {
        note = null;
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            note = "The file is not UTF-8, so letters outside English may show wrongly here. Copy such prompt text from the screen of a test run instead.";
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static bool ContainsTodo(PackageBuildRequest request) =>
        JsonSerializer.Serialize(request with { File = null }, Json)
            .Contains("TODO", StringComparison.Ordinal);

    private static bool IsRedirected(string path) =>
        (global::System.IO.File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    public static bool IsValidId(string? id) =>
        id is { Length: > 0 } && IdPattern().IsMatch(id) && char.IsAsciiLetterOrDigit(id[^1]);

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}
