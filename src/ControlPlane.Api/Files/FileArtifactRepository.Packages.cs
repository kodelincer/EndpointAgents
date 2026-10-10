using EndpointAgents.Shared.Office;
using EndpointAgents.Shared.Packages;
using EndpointAgents.Shared.Windows;
using System.Text.Json;

namespace ControlPlane.Api.Files;

/// <param name="Problem">Groups packages that fix the same problem.</param>
/// <param name="Order">Try order within <paramref name="Problem"/>, lower first.</param>
public sealed record RepairPackageArtifact(
    FileArtifact File,
    PackageDescriptor Descriptor,
    string SupportDirectory,
    string Problem,
    int Order = BatPackageOrdering.Default);

/// <summary>
/// Loads packages from <c>FileRepository/packages/&lt;id&gt;/package.json</c>. Schema 1
/// BAT packages join the existing Office and Windows bugfix catalogs, so those endpoint
/// routes and descriptors stay unchanged; schema 2 packages (BAT or EXE) form the
/// general repair catalog. Legacy manifests keep working.
/// </summary>
public sealed partial class FileArtifactRepository
{
    public const string PackagesDirectoryName = "packages";
    public const string PackageManifestFileName = "package.json";
    public const string PackageSupportDirectoryName = "files";
    public const string GeneralPackageKind = "general";
    private const int MaxDisplayNameCharacters = 120;
    private const int MaxDescriptionCharacters = 500;

    public string PackagesRootPath => Path.Combine(_rootPath, PackagesDirectoryName);

    private sealed record LoadedPackage(BatPackageStatus Status,
        OfficeBugfixFileArtifact? Office, WindowsBugfixFileArtifact? Windows,
        RepairPackageArtifact? General = null);

    /// <summary>Published schema 2 packages, by problem, then try order, then id.</summary>
    public async Task<IReadOnlyList<RepairPackageArtifact>> GetRepairPackagesAsync(
        CancellationToken cancellationToken) =>
        (await LoadPackagesAsync(cancellationToken))
            .Select(package => package.General)
            .OfType<RepairPackageArtifact>()
            .OrderBy(item => item.Problem, StringComparer.Ordinal)
            .ThenBy(item => item.Order)
            .ThenBy(item => item.Descriptor.PackageId, StringComparer.Ordinal)
            .ToArray();

    public async Task<RepairPackageArtifact?> GetRepairPackageAsync(string packageId,
        CancellationToken cancellationToken)
    {
        if (!PackageDescriptorPolicy.IsValidId(packageId)) return null;
        return (await GetRepairPackagesAsync(cancellationToken)).SingleOrDefault(item =>
            item.Descriptor.PackageId == packageId);
    }

    public async Task<FileArtifact?> GetRepairPackageSupportFileAsync(string packageId,
        string fileName, CancellationToken cancellationToken)
    {
        var package = await GetRepairPackageAsync(packageId, cancellationToken);
        var entry = package?.Descriptor.SupportFiles.SingleOrDefault(item =>
            item.FileName == fileName);
        return entry is null ? null : await InspectFileAsync(
            Path.Combine(package!.SupportDirectory, fileName), entry.Sha256,
            cancellationToken);
    }

    public async Task<IReadOnlyList<BatPackageStatus>> GetPackageStatusesAsync(
        CancellationToken cancellationToken)
    {
        var packages = await LoadPackagesAsync(cancellationToken);
        var legacyOffice = (await GetLegacyOfficeBugfixCatalogAsync(cancellationToken))
            .Artifacts.Select(item => item.Descriptor.ArtifactId);
        var legacyWindows = (await GetLegacyWindowsBugfixCatalogAsync(cancellationToken))
            .Artifacts.Select(item => item.Descriptor.ArtifactId);
        var taken = legacyOffice.Concat(legacyWindows)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return packages.Select(package => taken.Contains(package.Status.Id)
                && package.Status.Kind != GeneralPackageKind
                && BatPackageStatuses.IsPublished(package.Status.Status)
                ? package.Status with
                {
                    Status = BatPackageStatuses.Invalid,
                    Reasons = [ConflictReason(package.Status.Id)]
                }
                : package.Status)
            .ToArray();
    }

    public async Task<OfficeBugfixCatalogSnapshot> GetOfficeBugfixCatalogAsync(
        CancellationToken cancellationToken)
    {
        var legacy = await GetLegacyOfficeBugfixCatalogAsync(cancellationToken);
        var artifacts = legacy.Artifacts.ToList();
        var issues = legacy.Issues.ToList();
        foreach (var package in await LoadPackagesAsync(cancellationToken))
        {
            if (package.Office is null) continue;
            if (artifacts.Any(item => item.Descriptor.ArtifactId.Equals(
                    package.Status.Id, StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add(new FileRepositoryIssue($"package.{package.Status.Id}",
                    ConflictReason(package.Status.Id)));
                continue;
            }
            artifacts.Add(package.Office);
        }
        // Endpoints list compatible repairs in catalog order, and the agent tries them
        // in that order, so the administrator's package order comes first.
        return new OfficeBugfixCatalogSnapshot(artifacts
            .OrderBy(item => item.Order)
            .ThenBy(item => new[] { item.Descriptor.MinOfficeVersion }
                .Concat(item.Descriptor.AdditionalCompatibility?.Select(rule =>
                    rule.MinOfficeVersion) ?? [])
                .Select(Version.Parse).Min())
            .ThenBy(item => item.Descriptor.ArtifactId, StringComparer.OrdinalIgnoreCase)
            .ToArray(), issues);
    }

    public async Task<WindowsBugfixCatalogSnapshot> GetWindowsBugfixCatalogAsync(
        CancellationToken cancellationToken)
    {
        var legacy = await GetLegacyWindowsBugfixCatalogAsync(cancellationToken);
        var artifacts = legacy.Artifacts.ToList();
        var issues = legacy.Issues.ToList();
        foreach (var package in await LoadPackagesAsync(cancellationToken))
        {
            if (package.Windows is null) continue;
            if (artifacts.Any(item => item.Descriptor.ArtifactId.Equals(
                    package.Status.Id, StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add(new FileRepositoryIssue($"package.{package.Status.Id}",
                    ConflictReason(package.Status.Id)));
                continue;
            }
            artifacts.Add(package.Windows);
        }
        return new WindowsBugfixCatalogSnapshot(artifacts
            .OrderBy(item => item.Order)
            .ThenBy(item => item.Descriptor.MinWindowsVersion is null
                ? new Version(0, 0)
                : new[] { item.Descriptor.MinWindowsVersion }
                    .Concat(item.Descriptor.AdditionalCompatibility?.Select(rule =>
                        rule.MinWindowsVersion) ?? [])
                    .Select(Version.Parse).Min())
            .ThenBy(item => item.Descriptor.ArtifactId, StringComparer.OrdinalIgnoreCase)
            .ToArray(), issues);
    }

    private static string ConflictReason(string id) =>
        $"Another catalog entry already uses the id '{id}'; rename the package folder and id.";

    private async Task<IReadOnlyList<LoadedPackage>> LoadPackagesAsync(
        CancellationToken cancellationToken)
    {
        var root = PackagesRootPath;
        if (!PrepareTrustedRoot() || !Directory.Exists(root)
            || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            return [];
        var packages = new List<LoadedPackage>();
        foreach (var directory in Directory.EnumerateDirectories(root)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            packages.Add(await LoadPackageAsync(directory, cancellationToken));
        }
        return packages;
    }

    private async Task<LoadedPackage> LoadPackageAsync(string directory,
        CancellationToken cancellationToken)
    {
        var folder = Path.GetFileName(directory);
        LoadedPackage Rejected(string reason) => new(new BatPackageStatus(folder,
            directory, BatPackageStatuses.Invalid, [reason], [], null, null, null, null,
            null, null, null, null, null, null, [], null, null, null), null, null);

        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
            || !PrepareTrustedDirectory(directory))
            return Rejected("The package folder is redirected or writable by an untrusted account.");
        var manifestPath = Path.Combine(directory, PackageManifestFileName);
        var manifestInfo = new FileInfo(manifestPath);
        if (!manifestInfo.Exists)
            return Rejected($"{PackageManifestFileName} is missing. Run scripts\\new-bat-package.ps1 to create it.");
        if (manifestInfo.Length is <= 0 or > MaxManifestBytes
            || (manifestInfo.Attributes & FileAttributes.ReparsePoint) != 0
            || !IsTrustedFile(manifestPath))
            return Rejected($"{PackageManifestFileName} is empty, too large, redirected, or untrusted.");

        BatPackageManifest manifest;
        var manifestText = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        try
        {
            manifest = JsonSerializer.Deserialize<BatPackageManifest>(manifestText,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new JsonException("The file is empty.");
        }
        catch (JsonException ex)
        {
            return Rejected($"{PackageManifestFileName} is not valid JSON: {ex.Message}");
        }
        // new-bat-package.ps1 writes TODO markers for the parts a person must fill in.
        if (manifestText.Contains("TODO", StringComparison.Ordinal))
            return Rejected($"{PackageManifestFileName} still contains TODO markers; replace each one.");

        if (manifest.SchemaVersion is not 1 and not 2)
            return Rejected("schemaVersion must be 1 or 2.");
        if (manifest.Id != folder)
            return Rejected($"id '{manifest.Id}' must equal the folder name '{folder}'.");
        if (!ArtifactIdPattern().IsMatch(folder) || !char.IsAsciiLetterOrDigit(folder[^1]))
            return Rejected("The package id must use lowercase letters, numbers, dots, underscores, or hyphens and end with a letter or number.");
        if (manifest.SchemaVersion == 2)
            return await LoadGeneralPackageAsync(directory, folder, manifest,
                cancellationToken);
        if ((manifest.Office is null) == (manifest.Windows is null))
            return Rejected("package.json must contain exactly one of \"office\" or \"windows\".");

        var reasons = new List<(string Status, string Reason)>();
        var warnings = new List<string>();
        var kind = manifest.Office is not null ? "office" : "windows";
        if (!BatPackageOrdering.IsValid(manifest.Order))
            reasons.Add((BatPackageStatuses.Invalid,
                $"order must be between {BatPackageOrdering.Minimum} and {BatPackageOrdering.Maximum}."));
        var order = manifest.Order ?? BatPackageOrdering.Default;

        // BAT file and its pinned hash.
        var batName = manifest.Bat?.FileName ?? string.Empty;
        var pinnedBat = NormalizeSha256(manifest.Bat?.Sha256);
        FileArtifact? bat = null;
        if (batName.Length == 0 || Path.GetFileName(batName) != batName
            || !batName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            reasons.Add((BatPackageStatuses.Invalid,
                "bat.fileName must be the name of a .bat file inside the package folder."));
        else
        {
            bat = await InspectFileAsync(Path.Combine(directory, batName), null,
                cancellationToken);
            if (bat is null)
                reasons.Add((BatPackageStatuses.Invalid,
                    $"{batName} is missing, empty, too large, redirected, or untrusted."));
            else if (pinnedBat is null)
                reasons.Add((BatPackageStatuses.HashMismatch,
                    $"bat.sha256 is not set; the file's SHA-256 is {bat.Sha256}."));
            else if (pinnedBat != bat.Sha256)
                reasons.Add((BatPackageStatuses.HashMismatch,
                    $"{batName} has SHA-256 {bat.Sha256} but package.json pins {pinnedBat}. The file changed after review."));
        }

        // Playbook and its pinned hash.
        string? computedPlaybook = null;
        BatchPlaybook? playbook = null;
        if (!BatchPlaybookPolicy.TryNormalize(manifest.Playbook, out playbook,
                out var playbookError))
            reasons.Add((BatPackageStatuses.InvalidPlaybook, $"Playbook: {playbookError}"));
        else
        {
            computedPlaybook = BatchPlaybookPolicy.ComputeSha256(playbook!);
            var pinnedPlaybook = NormalizeSha256(manifest.PlaybookSha256);
            if (pinnedPlaybook is null)
                reasons.Add((BatPackageStatuses.InvalidPlaybook,
                    $"playbookSha256 is not set; the playbook hashes to {computedPlaybook}."));
            else if (pinnedPlaybook != computedPlaybook)
                reasons.Add((BatPackageStatuses.InvalidPlaybook,
                    $"The playbook hashes to {computedPlaybook} but package.json pins {pinnedPlaybook}. Review the change, then update playbookSha256."));
        }

        // Display text and compatibility, using the same rules as the legacy manifests.
        var officeEntry = manifest.Office is { } office ? new OfficeBugfixManifestEntry
        {
            ArtifactId = folder, DisplayName = manifest.DisplayName,
            Description = manifest.Description, FileName = $"{folder}.bat",
            Sha256 = pinnedBat ?? bat?.Sha256 ?? string.Empty,
            MinOfficeVersion = office.MinOfficeVersion,
            MaxOfficeVersionExclusive = office.MaxOfficeVersionExclusive,
            OfficeProductReleaseIds = office.OfficeProductReleaseIds ?? [],
            MinWindowsVersion = office.MinWindowsVersion,
            MaxWindowsVersionExclusive = office.MaxWindowsVersionExclusive,
            AdditionalCompatibility = office.AdditionalCompatibility ?? [],
            SupportFiles = manifest.SupportFiles ?? [], Playbook = playbook
        } : null;
        var windowsEntry = manifest.Windows is { } windows ? new WindowsBugfixManifestEntry
        {
            ArtifactId = folder, DisplayName = manifest.DisplayName,
            Description = manifest.Description, FileName = $"{folder}.bat",
            Sha256 = pinnedBat ?? bat?.Sha256 ?? string.Empty,
            MinWindowsVersion = windows.MinWindowsVersion,
            MaxWindowsVersionExclusive = windows.MaxWindowsVersionExclusive,
            EditionIds = windows.EditionIds ?? [],
            AdditionalCompatibility = windows.AdditionalCompatibility ?? [],
            SupportFiles = manifest.SupportFiles ?? [], Playbook = playbook
        } : null;
        if (playbook is not null)
        {
            string? entryError = null;
            var valid = officeEntry is not null
                ? TryValidateBugfixEntry(officeEntry, out _, out entryError)
                : TryValidateWindowsBugfixEntry(windowsEntry!, out _, out entryError);
            if (!valid)
                reasons.Add((BatPackageStatuses.Invalid, entryError!));
        }

        // Supporting files in <package>/files/.
        IReadOnlyList<BatchSupportFile>? supportFiles = null;
        var supportDirectory = Path.Combine(directory, PackageSupportDirectoryName);
        var (loaded, supportError) = await LoadSupportFilesAsync(supportDirectory, folder,
            manifest.SupportFiles ?? [],
            $"/api/files/{kind}/bugfix/{folder}/file", cancellationToken);
        if (supportError is not null)
            reasons.Add((supportError.Contains("hash", StringComparison.OrdinalIgnoreCase)
                ? BatPackageStatuses.HashMismatch : BatPackageStatuses.Invalid,
                supportError));
        else supportFiles = loaded;

        // Lint.
        if (bat is not null)
        {
            var lint = BatScriptLint.Inspect(
                await File.ReadAllTextAsync(bat.FullPath, cancellationToken));
            reasons.AddRange(lint.Errors.Select(error =>
                (BatPackageStatuses.LintRejected, $"Lint: {error}")));
            warnings.AddRange(lint.Warnings);
        }

        var status = OverallStatus(reasons, warnings);
        var packageStatus = new BatPackageStatus(folder, directory, status,
            reasons.Select(reason => reason.Reason).ToArray(), warnings, kind,
            manifest.DisplayName, manifest.Description, batName.Length == 0 ? null : batName,
            pinnedBat, bat?.Sha256, bat?.SizeBytes,
            NormalizeSha256(manifest.PlaybookSha256), computedPlaybook,
            playbook ?? manifest.Playbook, manifest.SupportFiles ?? [], manifest.Office,
            manifest.Windows, manifest.Review, Order: order);
        if (!BatPackageStatuses.IsPublished(status))
            return new LoadedPackage(packageStatus, null, null);

        return officeEntry is not null
            ? new LoadedPackage(packageStatus, new OfficeBugfixFileArtifact(bat!,
                CreateOfficeBugfixDescriptor(officeEntry, playbook!, bat!.Sha256,
                    supportFiles!), supportDirectory, order), null)
            : new LoadedPackage(packageStatus, null, new WindowsBugfixFileArtifact(bat!,
                CreateWindowsBugfixDescriptor(windowsEntry!, playbook!, bat!.Sha256,
                    supportFiles!), supportDirectory, order));
    }

    private static string OverallStatus(
        IReadOnlyList<(string Status, string Reason)> reasons,
        IReadOnlyList<string> warnings)
    {
        string[] severity =
        [
            BatPackageStatuses.Invalid, BatPackageStatuses.HashMismatch,
            BatPackageStatuses.InvalidPlaybook, BatPackageStatuses.LintRejected
        ];
        return reasons.Count > 0
            ? severity.First(level => reasons.Any(reason => reason.Status == level))
            : warnings.Count > 0 ? BatPackageStatuses.ReadyWithWarnings
            : BatPackageStatuses.Ready;
    }

    /// <summary>
    /// Validates a schema 2 general repair package: a BAT or EXE, the symptoms the
    /// agent matches requests against, a version 1-3 playbook, and an optional Windows
    /// version range.
    /// </summary>
    private async Task<LoadedPackage> LoadGeneralPackageAsync(string directory,
        string folder, BatPackageManifest manifest, CancellationToken cancellationToken)
    {
        var reasons = new List<(string Status, string Reason)>();
        var warnings = new List<string>();
        void Invalid(string reason) => reasons.Add((BatPackageStatuses.Invalid, reason));

        if (manifest.Bat is not null || manifest.Office is not null
            || manifest.Windows is not null)
            Invalid("Schema 2 packages describe their file in \"file\"; remove \"bat\", \"office\" and \"windows\".");
        var displayName = manifest.DisplayName?.Trim() ?? string.Empty;
        var description = manifest.Description?.Trim() ?? string.Empty;
        if (displayName.Length is 0 or > MaxDisplayNameCharacters)
            Invalid($"displayName must contain 1 to {MaxDisplayNameCharacters} characters.");
        if (description.Length is 0 or > MaxDescriptionCharacters)
            Invalid($"description must contain 1 to {MaxDescriptionCharacters} characters.");

        var symptoms = (manifest.Symptoms ?? [])
            .Select(symptom => symptom?.Trim() ?? string.Empty).ToArray();
        if (symptoms.Length is 0 or > PackageDescriptorPolicy.MaximumSymptoms
            || symptoms.Any(symptom => symptom.Length is 0
                or > PackageDescriptorPolicy.MaximumSymptomCharacters))
            Invalid($"symptoms must list 1 to {PackageDescriptorPolicy.MaximumSymptoms} phrases of up to {PackageDescriptorPolicy.MaximumSymptomCharacters} characters that describe the problem in the user's words.");

        // Packages with the same problem are alternatives, tried in order until one works.
        var problem = string.IsNullOrWhiteSpace(manifest.Problem) ? folder
            : manifest.Problem.Trim();
        if (!ArtifactIdPattern().IsMatch(problem) || !char.IsAsciiLetterOrDigit(problem[^1]))
            Invalid("problem must use lowercase letters, numbers, dots, underscores or hyphens, such as \"printing-stuck\".");
        if (!BatPackageOrdering.IsValid(manifest.Order))
            Invalid($"order must be between {BatPackageOrdering.Minimum} and {BatPackageOrdering.Maximum}.");
        var order = manifest.Order ?? BatPackageOrdering.Default;

        var minWindows = manifest.Compatibility?.MinWindowsVersion?.Trim();
        var maxWindows = manifest.Compatibility?.MaxWindowsVersionExclusive?.Trim();
        if (string.IsNullOrEmpty(minWindows)) minWindows = null;
        if (string.IsNullOrEmpty(maxWindows)) maxWindows = null;
        if (!ValidVersion(minWindows) || !ValidVersion(maxWindows)
            || minWindows is not null && maxWindows is not null
            && Version.Parse(minWindows) >= Version.Parse(maxWindows))
            Invalid("compatibility.minWindowsVersion and maxWindowsVersionExclusive must be versions such as 10.0.19041, with the minimum below the maximum.");

        // The file and its pinned hash.
        var type = manifest.File?.Type?.Trim().ToLowerInvariant() ?? string.Empty;
        var fileName = manifest.File?.FileName ?? string.Empty;
        var pinnedFile = NormalizeSha256(manifest.File?.Sha256);
        var publisher = manifest.File?.Publisher?.Trim();
        if (string.IsNullOrEmpty(publisher)) publisher = null;
        FileArtifact? file = null;
        if (!PackageDescriptorPolicy.IsValidSourceFileName(type, fileName))
            Invalid("file.type must be \"bat\" or \"exe\", and file.fileName the name of a matching .bat or .exe inside the package folder.");
        else
        {
            file = await InspectFileAsync(Path.Combine(directory, fileName), null,
                cancellationToken);
            if (file is null)
                Invalid($"{fileName} is missing, empty, too large, redirected, or untrusted.");
            else if (pinnedFile is null)
                reasons.Add((BatPackageStatuses.HashMismatch,
                    $"file.sha256 is not set; the file's SHA-256 is {file.Sha256}."));
            else if (pinnedFile != file.Sha256)
                reasons.Add((BatPackageStatuses.HashMismatch,
                    $"{fileName} has SHA-256 {file.Sha256} but package.json pins {pinnedFile}. The file changed after review."));
        }

        // Playbook and its pinned hash.
        string? computedPlaybook = null;
        if (!BatchPlaybookPolicy.TryNormalize(manifest.Playbook, out var playbook,
                out var playbookError))
            reasons.Add((BatPackageStatuses.InvalidPlaybook, $"Playbook: {playbookError}"));
        else
        {
            computedPlaybook = BatchPlaybookPolicy.ComputeSha256(playbook!);
            var pinnedPlaybook = NormalizeSha256(manifest.PlaybookSha256);
            if (pinnedPlaybook is null)
                reasons.Add((BatPackageStatuses.InvalidPlaybook,
                    $"playbookSha256 is not set; the playbook hashes to {computedPlaybook}."));
            else if (pinnedPlaybook != computedPlaybook)
                reasons.Add((BatPackageStatuses.InvalidPlaybook,
                    $"The playbook hashes to {computedPlaybook} but package.json pins {pinnedPlaybook}. Review the change, then update playbookSha256."));
        }

        // BAT lint, or EXE subsystem and signer.
        string? subsystem = null;
        string? signer = null;
        if (file is not null && type == PackageFileTypes.Bat)
        {
            if (publisher is not null)
                Invalid("file.publisher applies only to EXE files.");
            var lint = BatScriptLint.Inspect(
                await File.ReadAllTextAsync(file.FullPath, cancellationToken));
            reasons.AddRange(lint.Errors.Select(error =>
                (BatPackageStatuses.LintRejected, $"Lint: {error}")));
            warnings.AddRange(lint.Warnings);
        }
        else if (file is not null)
        {
            subsystem = ExecutableInspector.ReadSubsystem(file.FullPath);
            signer = ExecutableInspector.ReadSignerSubject(file.FullPath);
            if (subsystem is null)
                Invalid($"{fileName} is not a Windows console or GUI executable.");
            else if (subsystem == ExecutableInspector.Gui && playbook?.Steps.Count > 0)
                reasons.Add((BatPackageStatuses.InvalidPlaybook,
                    $"{fileName} is a windowed (GUI) program, so it cannot answer console prompts. Use playbook version 3 with no steps and the program's silent-mode arguments."));
            if (publisher is not null && !publisher.Equals(signer,
                    StringComparison.OrdinalIgnoreCase))
                Invalid(signer is null
                    ? $"file.publisher pins '{publisher}' but {fileName} is not signed."
                    : $"file.publisher pins '{publisher}' but {fileName} is signed by '{signer}'.");
            if (signer is null)
                warnings.Add($"{fileName} has no embedded Authenticode signature. Its SHA-256 pin still protects it, but prefer files signed by their vendor.");
        }

        // Supporting files in <package>/files/.
        IReadOnlyList<BatchSupportFile>? supportFiles = null;
        var supportDirectory = Path.Combine(directory, PackageSupportDirectoryName);
        var stagedName = PackageFileTypes.IsKnown(type)
            ? PackageDescriptorPolicy.StagedFileName(folder, type) : fileName;
        var supportEntries = manifest.SupportFiles ?? [];
        if (supportEntries.Any(entry => entry?.FileName?.Equals(stagedName,
                StringComparison.OrdinalIgnoreCase) == true))
            Invalid($"A supporting file cannot be named like the staged package file ({stagedName}).");
        else
        {
            var (loaded, supportError) = await LoadSupportFilesAsync(supportDirectory,
                folder, supportEntries, PackageRoutes.SupportFilePrefix(folder),
                cancellationToken);
            if (supportError is not null)
                reasons.Add((supportError.Contains("hash",
                        StringComparison.OrdinalIgnoreCase)
                    ? BatPackageStatuses.HashMismatch : BatPackageStatuses.Invalid,
                    supportError));
            else supportFiles = loaded;
        }

        var status = OverallStatus(reasons, warnings);
        var compatibility = minWindows is null && maxWindows is null ? null
            : new PackageCompatibilityManifest
            {
                MinWindowsVersion = minWindows,
                MaxWindowsVersionExclusive = maxWindows
            };
        var packageStatus = new BatPackageStatus(folder, directory, status,
            reasons.Select(reason => reason.Reason).ToArray(), warnings,
            GeneralPackageKind, manifest.DisplayName, manifest.Description,
            fileName.Length == 0 ? null : fileName, pinnedFile, file?.Sha256,
            file?.SizeBytes, NormalizeSha256(manifest.PlaybookSha256), computedPlaybook,
            playbook ?? manifest.Playbook, supportEntries, null, null, manifest.Review,
            PackageFileTypes.IsKnown(type) ? type : manifest.File?.Type ?? string.Empty,
            symptoms, compatibility, signer, subsystem, problem, order);
        if (!BatPackageStatuses.IsPublished(status))
            return new LoadedPackage(packageStatus, null, null);

        var descriptor = new PackageDescriptor(folder, displayName, description, type,
            stagedName, file!.Sha256, PackageRoutes.Download(folder), playbook!,
            computedPlaybook!, supportFiles!,
            supportFiles!.Count == 0 ? null
                : BatchSupportFilePolicy.ComputeBundleSha256(file.Sha256, supportFiles),
            NormalizeVersion(minWindows), NormalizeVersion(maxWindows), publisher,
            symptoms);
        return new LoadedPackage(packageStatus, null, null,
            new RepairPackageArtifact(file, descriptor, supportDirectory, problem, order));
    }
}
