using EndpointAgents.Shared.Office;
using EndpointAgents.Shared.Windows;
using System.Text.Json;

namespace ControlPlane.Api.Files;

/// <summary>
/// Loads BAT packages from <c>FileRepository/packages/&lt;id&gt;/package.json</c> and adds
/// the publishable ones to the existing Office and Windows bugfix catalogs, so the
/// endpoint routes and descriptors stay unchanged. Legacy manifests keep working.
/// </summary>
public sealed partial class FileArtifactRepository
{
    public const string PackagesDirectoryName = "packages";
    public const string PackageManifestFileName = "package.json";
    public const string PackageSupportDirectoryName = "files";

    public string PackagesRootPath => Path.Combine(_rootPath, PackagesDirectoryName);

    private sealed record LoadedPackage(BatPackageStatus Status,
        OfficeBugfixFileArtifact? Office, WindowsBugfixFileArtifact? Windows);

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
        return new OfficeBugfixCatalogSnapshot(artifacts
            .OrderBy(item => new[] { item.Descriptor.MinOfficeVersion }
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
            .OrderBy(item => item.Descriptor.MinWindowsVersion is null
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

        if (manifest.SchemaVersion != 1)
            return Rejected("schemaVersion must be 1.");
        if (manifest.Id != folder)
            return Rejected($"id '{manifest.Id}' must equal the folder name '{folder}'.");
        if (!ArtifactIdPattern().IsMatch(folder) || !char.IsAsciiLetterOrDigit(folder[^1]))
            return Rejected("The package id must use lowercase letters, numbers, dots, underscores, or hyphens and end with a letter or number.");
        if ((manifest.Office is null) == (manifest.Windows is null))
            return Rejected("package.json must contain exactly one of \"office\" or \"windows\".");

        var reasons = new List<(string Status, string Reason)>();
        var warnings = new List<string>();
        var kind = manifest.Office is not null ? "office" : "windows";

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

        string[] severity =
        [
            BatPackageStatuses.Invalid, BatPackageStatuses.HashMismatch,
            BatPackageStatuses.InvalidPlaybook, BatPackageStatuses.LintRejected
        ];
        var status = reasons.Count > 0
            ? severity.First(level => reasons.Any(reason => reason.Status == level))
            : warnings.Count > 0 ? BatPackageStatuses.ReadyWithWarnings
            : BatPackageStatuses.Ready;
        var packageStatus = new BatPackageStatus(folder, directory, status,
            reasons.Select(reason => reason.Reason).ToArray(), warnings, kind,
            manifest.DisplayName, manifest.Description, batName.Length == 0 ? null : batName,
            pinnedBat, bat?.Sha256, bat?.SizeBytes,
            NormalizeSha256(manifest.PlaybookSha256), computedPlaybook,
            playbook ?? manifest.Playbook, manifest.SupportFiles ?? [], manifest.Office,
            manifest.Windows, manifest.Review);
        if (!BatPackageStatuses.IsPublished(status))
            return new LoadedPackage(packageStatus, null, null);

        return officeEntry is not null
            ? new LoadedPackage(packageStatus, new OfficeBugfixFileArtifact(bat!,
                CreateOfficeBugfixDescriptor(officeEntry, playbook!, bat!.Sha256,
                    supportFiles!), supportDirectory), null)
            : new LoadedPackage(packageStatus, null, new WindowsBugfixFileArtifact(bat!,
                CreateWindowsBugfixDescriptor(windowsEntry!, playbook!, bat!.Sha256,
                    supportFiles!), supportDirectory));
    }
}
