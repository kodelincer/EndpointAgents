using EndpointAgents.Shared.Office;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EndpointAgents.Shared.Packages;
using Microsoft.Extensions.Options;

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
    BatPackageReview? Review = null,
    string? UploadId = null,
    SoftwareSpec? Software = null,
    IReadOnlyList<string>? KeepSupportFiles = null,
    IReadOnlyList<string>? SupportUploadIds = null);

/// <summary>The revision the admin reviewed: both hashes must still match when they approve.</summary>
public sealed record PackageApproveRequest(string? FileSha256, string? PlaybookSha256);

/// <summary>Whether dashboard approvals work, and the key endpoints must trust for them.</summary>
public sealed record PackageApprovalInfo(bool Available, string? KeyId, string? PublicKey,
    string? Problem, bool RequireSecondPerson);

/// <summary>An existing package loaded back into the builder form.</summary>
public sealed record PackageBuilderDraft(string Kind, BatPackageManifest Manifest,
    BatScanResult? Scan, bool Signed, IReadOnlyList<SupportFileInfo>? SupportFiles = null);

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
public sealed partial class PackageBuilder(FileArtifactRepository repository, TimeProvider clock,
    IPackageApprovalKey? approvalKey = null, IOptions<PackageApprovalOptions>? approvalOptions = null,
    InstallerUploads? uploads = null)
{
    public const string General = "general";
    public const string Office = "office";
    public const string Windows = "windows";
    /// <summary>A vendor MSI or EXE the PC installs itself (software.install), like new-install-package.ps1 -Typed.</summary>
    public const string Install = "install";
    private const int DefaultInstallTimeoutSeconds = 900;
    public const int MaximumFileBytes = 8 * 1024 * 1024;
    private const int MaximumSymptoms = 32;
    private const int MaximumSymptomCharacters = 200;
    private const string StagingDirectoryName = ".package-builder";

    /// <summary>Beside the packages folder (same disk), so finished folders can be moved in.</summary>
    public static string StagingRoot(FileArtifactRepository repository) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(repository.PackagesRootPath))!, StagingDirectoryName);

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
        var kind = manifest.SchemaVersion == 2
            ? IsInstallManifest(manifest) ? Install : General
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
        var support = new List<SupportFileInfo>();
        foreach (var entry in manifest.SupportFiles ?? [])
        {
            if (InstallerUploads.SafeSupportFileName(entry.FileName) != entry.FileName) continue;
            var path = Path.Combine(folder, FileArtifactRepository.PackageSupportDirectoryName, entry.FileName);
            var info = new FileInfo(path);
            support.Add(info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0
                ? InstallerUploads.DescribeSupportFile(path, entry.FileName, info.Length,
                    await Sha256FileAsync(path, cancellationToken), null)
                : new SupportFileInfo(null, entry.FileName, 0, entry.Sha256, null, [],
                    ["This file is missing from the package. Upload it again, or remove it."]));
        }
        var signed = manifest.Signature is not null;
        manifest.Signature = null;
        return new PackageBuilderDraft(kind, manifest, scan, signed, support);
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
        if (kind is not (General or Office or Windows or Install))
            return PackageBuildResult.Fail("Choose what the package is for: general repair, Office activation, Windows activation or installing software.");
        if (kind == Install && !id.EndsWith("-install", StringComparison.Ordinal))
            return PackageBuildResult.Fail("The id of an install package ends with \"-install\", such as \"7zip-install\".");
        var displayName = request.DisplayName?.Trim() ?? string.Empty;
        var description = request.Description?.Trim() ?? string.Empty;
        if (displayName.Length is 0 or > 120)
            return PackageBuildResult.Fail("Give the package a name of 1 to 120 characters.");
        if (description.Length is 0 or > 500)
            return PackageBuildResult.Fail("Describe what the package does in 1 to 500 characters.");
        if (ContainsTodo(request))
            return PackageBuildResult.Fail("Replace every TODO in the form first.");
        if (kind == Install)
            return await SaveInstallAsync(request, id, displayName, description, author,
                mayReplaceSigned, cancellationToken);

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

        var symptoms = CleanSymptoms(request.Symptoms);
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

            var manifest = new BatPackageManifest
            {
                SchemaVersion = kind == General ? 2 : 1,
                Id = id,
                DisplayName = displayName,
                Description = description,
                Problem = string.IsNullOrWhiteSpace(request.Problem) ? null : request.Problem.Trim(),
                Order = request.Order,
                RequiresApproval = request.RequiresApproval,
                Playbook = normalized,
                PlaybookSha256 = BatchPlaybookPolicy.ComputeSha256(normalized!),
                Review = BuildReview(request.Review, author, $"Made with the dashboard package builder by {author}.")
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

            var stagedName = kind == General
                ? PackageDescriptorPolicy.StagedFileName(id, PackageFileTypes.Bat) : $"{id}.bat";
            var (support, supportError) = await PlanSupportFilesAsync(request, existing, folder,
                [stagedName, fileName!], cancellationToken);
            if (supportError is not null)
                return PackageBuildResult.Fail(supportError);
            manifest.SupportFiles = support!
                .Select(item => new BatchSupportFileManifestEntry { FileName = item.FileName, Sha256 = item.Sha256 })
                .ToList();

            var batBytes = bytes;
            await WriteFolderAsync(root, folder, fileName!,
                (path, token) => global::System.IO.File.WriteAllBytesAsync(path, batBytes, token),
                manifest, existing is not null, support!, cancellationToken);
            foreach (var uploadId in request.SupportUploadIds ?? [])
                uploads?.Remove(uploadId);
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
    /// Saves an install package: the vendor installer is the package file and the PC runs it
    /// itself with the switches of its installer kind, then checks the detection rule. The
    /// same package new-install-package.ps1 -Typed writes.
    /// </summary>
    private async Task<PackageBuildResult> SaveInstallAsync(PackageBuildRequest request, string id,
        string displayName, string description, string author, bool mayReplaceSigned,
        CancellationToken cancellationToken)
    {
        var symptoms = CleanSymptoms(request.Symptoms);
        if (symptoms.Count == 0)
            return PackageBuildResult.Fail("Add at least one way a user would ask for the application.");
        if (symptoms.Count > MaximumSymptoms || symptoms.Any(symptom => symptom.Length > MaximumSymptomCharacters))
            return PackageBuildResult.Fail($"Use at most {MaximumSymptoms} request phrases of up to {MaximumSymptomCharacters} characters each.");
        if (request.Software?.Install is not { } requestedInstall)
            return PackageBuildResult.Fail("Choose how the installer runs.");
        var installKind = requestedInstall.Kind?.Trim().ToLowerInvariant();
        if (installKind is not (InstallerKinds.Msi or InstallerKinds.Inno or InstallerKinds.Nsis
            or InstallerKinds.WixBurn or InstallerKinds.InstallShield or InstallerKinds.Squirrel
            or InstallerKinds.Exe))
            return PackageBuildResult.Fail("The builder makes MSI and EXE install packages. Use new-install-package.ps1 for recorded answers or a manual install.");
        if (request.Software.Detection is not { Count: > 0 })
            return PackageBuildResult.Fail("Type the name the app shows in Apps & features, so the PC can check the install worked.");

        (string Path, InstallerInfo Info)? upload = null;
        if (request.UploadId is not null)
        {
            upload = uploads?.Get(request.UploadId);
            if (upload is null)
                return PackageBuildResult.Fail("The uploaded installer has expired. Upload it again.");
        }

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

            string fileName, sourcePath;
            if (upload is { } uploaded)
            {
                fileName = uploaded.Info.FileName;
                sourcePath = uploaded.Path;
            }
            else
            {
                // Keep the installer that is already in the package.
                var keptName = existing?.File?.FileName;
                if (existing is null || !IsInstallManifest(existing) || keptName is not { Length: > 0 }
                    || Path.GetFileName(keptName) != keptName
                    || !global::System.IO.File.Exists(Path.Combine(folder, keptName)))
                    return PackageBuildResult.Fail("Upload the installer (.msi or .exe).");
                fileName = keptName;
                sourcePath = Path.Combine(folder, keptName);
            }
            var fileType = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
            if (fileType is not (PackageFileTypes.Msi or PackageFileTypes.Exe))
                return PackageBuildResult.Fail("The installer must be an .msi or .exe file.");
            if ((fileType == PackageFileTypes.Msi) != (installKind == InstallerKinds.Msi))
                return PackageBuildResult.Fail(fileType == PackageFileTypes.Msi
                    ? "An .msi installer runs as kind msi."
                    : "An .exe installer cannot run as kind msi; pick its installer type.");

            // Endpoints check this signer again before they run the installer.
            var publisher = upload?.Info.Signer ?? ExecutableInspector.ReadSignerSubject(sourcePath);
            if (publisher is null)
                return PackageBuildResult.Fail("The installer has no vendor (Authenticode) signature. PCs refuse unsigned installers, so ask the vendor for a signed one.");

            var timeout = requestedInstall.TimeoutSeconds ?? DefaultInstallTimeoutSeconds;
            var software = request.Software with
            {
                Install = requestedInstall with { Kind = installKind, TimeoutSeconds = timeout },
                Name = string.IsNullOrWhiteSpace(request.Software.Name) ? null : request.Software.Name
            };
            if (!SoftwareSpecPolicy.TryNormalize(software, fileType, out var normalizedSoftware, out var softwareError))
                return PackageBuildResult.Fail($"Install: {softwareError}");
            var playbook = new BatchPlaybook(3, [], TimeoutSeconds: timeout);
            if (!BatchPlaybookPolicy.TryNormalize(playbook, out var normalizedPlaybook, out var playbookError))
                return PackageBuildResult.Fail($"Playbook: {playbookError}");

            var fileSha256 = upload?.Info.Sha256 ?? await Sha256FileAsync(sourcePath, cancellationToken);
            var appName = normalizedSoftware!.Name ?? id[..^"-install".Length];
            var manifest = new BatPackageManifest
            {
                SchemaVersion = 2,
                Id = id,
                DisplayName = displayName,
                Description = description,
                Problem = string.IsNullOrWhiteSpace(request.Problem)
                    ? "install-" + id[..^"-install".Length] : request.Problem.Trim(),
                Order = request.Order ?? BatPackageOrdering.Default,
                RequiresApproval = request.RequiresApproval,
                Symptoms = symptoms,
                File = new PackageFileManifest { Type = fileType, FileName = fileName, Sha256 = fileSha256, Publisher = publisher },
                Software = normalizedSoftware,
                Compatibility = request.Compatibility ?? new PackageCompatibilityManifest { MinWindowsVersion = "10.0.10240" },
                SupportFiles = [],
                Playbook = normalizedPlaybook,
                PlaybookSha256 = BatchPlaybookPolicy.ComputeSha256(normalizedPlaybook!),
                Review = BuildReview(request.Review, author, $"Install package for {appName} made with the dashboard package builder by {author}.")
            };

            var fromUpload = upload is not null;
            await WriteFolderAsync(root, folder, fileName, async (path, token) =>
            {
                // An upload sits on the same disk, so moving it is quick even for large installers.
                if (fromUpload) global::System.IO.File.Move(sourcePath, path);
                else
                {
                    await using var from = global::System.IO.File.OpenRead(sourcePath);
                    await using var to = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                    await from.CopyToAsync(to, token);
                }
            }, manifest, existing is not null, [], cancellationToken);
            if (request.UploadId is not null) uploads?.Remove(request.UploadId);
        }
        finally
        {
            WriteLock.Release();
        }

        var status = (await repository.GetPackageStatusesAsync(cancellationToken))
            .FirstOrDefault(item => item.Id == id);
        return new PackageBuildResult(true, null, status);
    }

    private BatPackageReview BuildReview(BatPackageReview? review, string author, string defaultNotes)
    {
        review ??= new BatPackageReview();
        return new BatPackageReview
        {
            ReviewedBy = string.IsNullOrWhiteSpace(review.ReviewedBy) ? author : review.ReviewedBy.Trim(),
            ReviewedOn = string.IsNullOrWhiteSpace(review.ReviewedOn)
                ? clock.GetUtcNow().ToString("yyyy-MM-dd", global::System.Globalization.CultureInfo.InvariantCulture)
                : review.ReviewedOn.Trim(),
            Source = review.Source?.Trim(),
            Notes = string.IsNullOrWhiteSpace(review.Notes) ? defaultNotes : review.Notes.Trim(),
            AcceptedRisks = review.AcceptedRisks ?? [],
            // A saved revision is a draft again until an admin approves it.
            BuiltBy = author
        };
    }

    private static List<string> CleanSymptoms(IReadOnlyList<string>? symptoms) =>
        (symptoms ?? []).Select(symptom => symptom?.Trim() ?? string.Empty)
            .Where(symptom => symptom.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static bool IsInstallManifest(BatPackageManifest manifest) =>
        manifest.SchemaVersion == 2 && manifest.Software?.Install is not null
        && manifest.File?.Type?.ToLowerInvariant() is PackageFileTypes.Msi or PackageFileTypes.Exe;

    private static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = global::System.IO.File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    /// <summary>
    /// Builds the new folder next to the packages folder, then swaps it in, so the
    /// catalog never sees a half-written package.
    /// </summary>
    private async Task WriteFolderAsync(string root, string folder, string fileName,
        Func<string, CancellationToken, Task> writeFile, BatPackageManifest manifest, bool replace,
        IReadOnlyList<SupportFilePlan> support, CancellationToken cancellationToken)
    {
        var staging = StagingRoot(repository);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(staging);
        var building = Path.Combine(staging, $"{manifest.Id}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(building);
        try
        {
            await writeFile(Path.Combine(building, fileName), cancellationToken);
            await global::System.IO.File.WriteAllTextAsync(
                Path.Combine(building, FileArtifactRepository.PackageManifestFileName),
                JsonSerializer.Serialize(manifest, Json) + Environment.NewLine,
                new UTF8Encoding(false), cancellationToken);
            if (support.Count > 0)
            {
                var files = Path.Combine(building, FileArtifactRepository.PackageSupportDirectoryName);
                Directory.CreateDirectory(files);
                foreach (var item in support)
                {
                    var target = Path.Combine(files, item.FileName);
                    // Uploads sit on the same disk; kept files are copied so the old folder stays whole until the swap.
                    if (item.Move) global::System.IO.File.Move(item.SourcePath, target);
                    else global::System.IO.File.Copy(item.SourcePath, target);
                }
            }

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

    /// <summary>One file for the package's files\ folder, with its hash worked out by the control plane.</summary>
    private sealed record SupportFilePlan(string FileName, string Sha256, string SourcePath, bool Move);

    private const int MaximumSupportFiles = 32;

    /// <summary>
    /// The supporting files the saved package will have: the existing ones the person kept
    /// (all of them when the form does not say) and the new uploads, which replace a kept
    /// file of the same name. Every hash is computed from the file itself.
    /// </summary>
    private async Task<(IReadOnlyList<SupportFilePlan>? Files, string? Error)> PlanSupportFilesAsync(
        PackageBuildRequest request, BatPackageManifest? existing, string folder,
        IReadOnlyList<string> reservedNames, CancellationToken cancellationToken)
    {
        var plan = new List<SupportFilePlan>();
        var existingNames = (existing?.SupportFiles ?? []).Select(entry => entry.FileName).ToList();
        var keep = request.KeepSupportFiles ?? existingNames;
        foreach (var name in keep.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!existingNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                return (null, $"The package has no supporting file named {name}.");
            var path = Path.Combine(folder, FileArtifactRepository.PackageSupportDirectoryName, name);
            if (InstallerUploads.SafeSupportFileName(name) != name || !global::System.IO.File.Exists(path)
                || IsRedirected(path))
                return (null, $"The supporting file {name} is missing. Upload it again, or remove it.");
            plan.Add(new SupportFilePlan(name, await Sha256FileAsync(path, cancellationToken), path, false));
        }
        foreach (var uploadId in (request.SupportUploadIds ?? []).Distinct(StringComparer.Ordinal))
        {
            if (uploads?.GetSupport(uploadId) is not { } upload)
                return (null, "An uploaded supporting file has expired. Upload it again.");
            plan.RemoveAll(item => item.FileName.Equals(upload.Info.FileName, StringComparison.OrdinalIgnoreCase));
            plan.Add(new SupportFilePlan(upload.Info.FileName, upload.Info.Sha256, upload.Path, true));
        }
        if (plan.Count > MaximumSupportFiles)
            return (null, $"A package can have at most {MaximumSupportFiles} supporting files.");
        if (plan.FirstOrDefault(item => reservedNames.Contains(item.FileName, StringComparer.OrdinalIgnoreCase)) is { } clash)
            return (null, $"A supporting file cannot be named {clash.FileName}, like the package's own file. Rename it.");
        return (plan.OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase).ToList(), null);
    }

    /// <summary>What the dashboard shows about dashboard approvals.</summary>
    public PackageApprovalInfo GetApprovalInfo()
    {
        var publicKey = approvalKey?.PublicKey;
        return new PackageApprovalInfo(publicKey is not null,
            publicKey is null ? null : PackageSignaturePolicy.GetKeyId(publicKey), publicKey,
            approvalKey is null ? "Approving in the dashboard is not set up." : approvalKey.Problem,
            approvalOptions?.Value.RequireSecondPerson ?? true);
    }

    /// <summary>
    /// An admin approves the revision they reviewed: the control plane signs it with its own
    /// key, so endpoints that trust that key may run it. The hashes the admin saw must still
    /// match, and the person who saved it in the builder may not approve it.
    /// </summary>
    public async Task<PackageBuildResult> ApproveAsync(string id, PackageApproveRequest request,
        string approver, CancellationToken cancellationToken)
    {
        if (approvalKey?.PublicKey is null)
            return PackageBuildResult.Fail(approvalKey?.Problem ?? "Approving in the dashboard is not set up.");
        if (!IsValidId(id))
            return PackageBuildResult.Fail("Unknown package.");

        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            var folder = Path.Combine(repository.PackagesRootPath, id);
            var manifestPath = Path.Combine(folder, FileArtifactRepository.PackageManifestFileName);
            if (!Directory.Exists(folder) || IsRedirected(folder) || !global::System.IO.File.Exists(manifestPath))
                return PackageBuildResult.Fail("Unknown package.");
            var status = (await repository.GetPackageStatusesAsync(cancellationToken))
                .FirstOrDefault(item => item.Id == id);
            if (status is null)
                return PackageBuildResult.Fail("Unknown package.");
            if (status.Status != BatPackageStatuses.Unsigned)
                return PackageBuildResult.Fail(BatPackageStatuses.IsPublished(status.Status)
                    ? "This package is already signed."
                    : $"Fix the package first: {string.Join(" ", status.Reasons)}");
            if (status.FileType is not (PackageFileTypes.Bat or PackageFileTypes.Exe or PackageFileTypes.Msi))
                return PackageBuildResult.Fail("Only BAT, EXE and MSI packages can be approved here. Sign other packages with test-bat-playbook.ps1 -Sign.");
            if (!string.Equals(request.FileSha256, status.ActualBatSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(request.PlaybookSha256, status.ComputedPlaybookSha256, StringComparison.OrdinalIgnoreCase))
                return PackageBuildResult.Fail("The package changed after you opened it. Refresh the page and review it again.");

            var original = await global::System.IO.File.ReadAllTextAsync(manifestPath, cancellationToken);
            var node = JsonNode.Parse(original)?.AsObject();
            var manifest = node?.Deserialize<BatPackageManifest>(Json);
            if (node is null || manifest is null)
                return PackageBuildResult.Fail("package.json could not be read.");
            var builtBy = manifest.Review?.BuiltBy;
            if ((approvalOptions?.Value.RequireSecondPerson ?? true) && builtBy is not null
                && string.Equals(builtBy, approver, StringComparison.OrdinalIgnoreCase))
                return PackageBuildResult.Fail("You saved this package yourself, so a different admin must approve it.");

            var stagedName = manifest.SchemaVersion == 2
                ? PackageDescriptorPolicy.StagedFileName(id, status.FileType) : $"{id}.bat";
            var support = status.SupportFiles
                .Select(entry => new BatchSupportFile(entry.FileName, entry.Sha256, string.Empty)).ToArray();
            var publisher = manifest.File?.Publisher?.Trim();
            string? softwareSha256 = null;
            if (manifest.Software is not null || status.FileType == PackageFileTypes.Msi)
            {
                if (!SoftwareSpecPolicy.TryNormalize(manifest.Software, status.FileType,
                        out var software, out var softwareError))
                    return PackageBuildResult.Fail($"Fix the package first: software: {softwareError}");
                softwareSha256 = SoftwareSpecPolicy.ComputeSha256(software!);
            }
            var statement = PackageSignaturePolicy.BuildStatement(id, stagedName, status.ActualBatSha256!,
                status.ComputedPlaybookSha256!, support.Length == 0 ? null
                    : BatchSupportFilePolicy.ComputeBundleSha256(status.ActualBatSha256!, support),
                string.IsNullOrEmpty(publisher) ? null : publisher,
                PackageSignaturePolicy.NormalizeRisks(manifest.Review?.AcceptedRisks), softwareSha256);

            node["signature"] = JsonSerializer.SerializeToNode(approvalKey.Sign(statement), Json);
            if (node["review"] is not JsonObject review)
                node["review"] = review = new JsonObject();
            review["approvedBy"] = approver;
            review["approvedOn"] = clock.GetUtcNow().ToString("yyyy-MM-dd",
                global::System.Globalization.CultureInfo.InvariantCulture);
            await WriteManifestAsync(manifestPath, node.ToJsonString(Json) + Environment.NewLine, cancellationToken);

            var signed = (await repository.GetPackageStatusesAsync(cancellationToken))
                .FirstOrDefault(item => item.Id == id);
            if (signed is null || !BatPackageStatuses.IsPublished(signed.Status))
            {
                await WriteManifestAsync(manifestPath, original, cancellationToken);
                return PackageBuildResult.Fail("The new signature did not check out, so nothing was changed. "
                    + string.Join(" ", signed?.Reasons ?? []));
            }
            return new PackageBuildResult(true, null, signed);
        }
        finally
        {
            WriteLock.Release();
        }
    }

    private static async Task WriteManifestAsync(string path, string text, CancellationToken cancellationToken)
    {
        var temporary = path + ".approving";
        await global::System.IO.File.WriteAllTextAsync(temporary, text, new UTF8Encoding(false), cancellationToken);
        global::System.IO.File.Move(temporary, path, overwrite: true);
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
    internal static string DecodeText(byte[] bytes, out string? note)
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
