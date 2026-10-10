using EndpointAgents.Shared.Office;
using EndpointAgents.Shared.Security;
using EndpointAgents.Shared.Windows;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ControlPlane.Api.Files;

public sealed record FileArtifact(
    string FullPath,
    string FileName,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc,
    string Sha256);

public sealed record OfficeFileArtifact(
    FileArtifact File,
    OfficeRemediationArtifactDescriptor Descriptor);

public sealed record OfficeArtifactCatalogSnapshot(
    IReadOnlyList<OfficeFileArtifact> Artifacts,
    IReadOnlyList<FileRepositoryIssue> Issues);

public sealed record OfficeBugfixFileArtifact(
    FileArtifact File,
    OfficeBugfixArtifactDescriptor Descriptor,
    string? SupportDirectory = null,
    int Order = BatPackageOrdering.Default);

public sealed record OfficeBugfixCatalogSnapshot(
    IReadOnlyList<OfficeBugfixFileArtifact> Artifacts,
    IReadOnlyList<FileRepositoryIssue> Issues);

public sealed record WindowsBugfixFileArtifact(
    FileArtifact File,
    WindowsBugfixArtifactDescriptor Descriptor,
    string? SupportDirectory = null,
    int Order = BatPackageOrdering.Default);

public sealed record WindowsBugfixCatalogSnapshot(
    IReadOnlyList<WindowsBugfixFileArtifact> Artifacts,
    IReadOnlyList<FileRepositoryIssue> Issues);

/// <summary>
/// Read-only repository for administrator-provided, hash-verified endpoint files.
/// Remote upload is intentionally unavailable while operator authentication is absent.
/// </summary>
public sealed partial class FileArtifactRepository
{
    private const long MaxManifestBytes = 1024 * 1024;
    private static readonly HashSet<string> AllowedOfficeExtensions = new(
        [".ps1", ".xml", ".zip", ".exe", ".msi", ".diagcab"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AllowedOfficeKinds = new(
        ["MicrosoftScript", "PowerShellScript", "OfficeConfiguration",
         OfficeRemediationArtifactKinds.MicrosoftSignedExecutable],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AllowedFamilies = new(
        [OfficeProductFamilies.Microsoft365Apps, OfficeProductFamilies.VolumeLicensed],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AllowedStates = new(
        [OfficeActivationStates.Unlicensed, OfficeActivationStates.Grace,
         OfficeActivationStates.Notification, OfficeActivationStates.Error,
         OfficeActivationStates.ToolUnavailable, OfficeActivationStates.Unknown],
        StringComparer.OrdinalIgnoreCase);

    private readonly FileRepositoryOptions _options;
    private readonly string _rootPath;
    private readonly string _officeRootPath;
    private readonly string _officeManifestPath;
    private readonly string _officeBugfixManifestPath;
    private readonly string _windowsRootPath;
    private readonly string _windowsBugfixFilePath;
    private readonly string _windowsBugfixManifestPath;
    private readonly string _windowsHelloSampleFilePath;

    public FileArtifactRepository(IOptions<FileRepositoryOptions> options,
        IHostEnvironment environment)
    {
        _options = options.Value;
        _rootPath = Path.IsPathRooted(_options.RootPath)
            ? Path.GetFullPath(_options.RootPath)
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, _options.RootPath));
        _officeRootPath = Path.GetFullPath(Path.Combine(_rootPath,
            _options.OfficeDirectoryName));
        _officeManifestPath = Path.GetFullPath(Path.Combine(_officeRootPath,
            _options.OfficeManifestFileName));
        _officeBugfixManifestPath = Path.GetFullPath(Path.Combine(_rootPath,
            _options.OfficeBugfixManifestFileName));
        _windowsRootPath = Path.GetFullPath(Path.Combine(_rootPath,
            _options.WindowsDirectoryName));
        _windowsBugfixFilePath = Path.GetFullPath(Path.Combine(_windowsRootPath,
            _options.WindowsBugfixFileName));
        _windowsBugfixManifestPath = Path.GetFullPath(Path.Combine(_windowsRootPath,
            _options.WindowsBugfixManifestFileName));
        _windowsHelloSampleFilePath = Path.GetFullPath(Path.Combine(_windowsRootPath,
            _options.WindowsHelloSampleFileName));
    }

    public string RootPath => _rootPath;
    public string OfficeRootPath => _officeRootPath;
    public string OfficeManifestPath => _officeManifestPath;
    public string OfficeBugfixManifestPath => _officeBugfixManifestPath;
    public string WindowsBugfixFilePath => _windowsBugfixFilePath;
    public string WindowsBugfixManifestPath => _windowsBugfixManifestPath;
    public string WindowsHelloSampleFilePath => _windowsHelloSampleFilePath;
    public string IbsngDialerFileName => _options.IbsngDialerFileName;
    public bool ProtectedAclRequired => _options.RequireProtectedAcl;

    public async Task<FileArtifact?> GetIbsngDialerAsync(CancellationToken cancellationToken)
    {
        if (!PrepareTrustedRoot()) return null;
        var path = Path.Combine(_rootPath, _options.IbsngDialerFileName);
        return await InspectFileAsync(path, expectedSha256: null, cancellationToken);
    }

    public async Task<OfficeArtifactCatalogSnapshot> GetOfficeCatalogAsync(
        CancellationToken cancellationToken)
    {
        if (!PrepareTrustedDirectory(_officeRootPath))
            return InvalidManifest(
                "The Office repository is not protected from untrusted writes.");
        if (!File.Exists(_officeManifestPath))
            return new OfficeArtifactCatalogSnapshot([], []);

        OfficeArtifactManifest manifest;
        try
        {
            var manifestInfo = new FileInfo(_officeManifestPath);
            if (manifestInfo.Length is <= 0 or > MaxManifestBytes
                || (manifestInfo.Attributes & FileAttributes.ReparsePoint) != 0
                || !IsTrustedFile(_officeManifestPath))
                return InvalidManifest(
                    "The Office artifact manifest is empty, too large, or a reparse point.");
            await using var stream = new FileStream(_officeManifestPath, FileMode.Open,
                FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous);
            manifest = await JsonSerializer.DeserializeAsync<OfficeArtifactManifest>(stream,
                new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken)
                ?? new OfficeArtifactManifest();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return InvalidManifest($"The Office artifact manifest could not be read: {ex.GetType().Name}.");
        }

        if (manifest.Version != 1)
            return InvalidManifest("The Office artifact manifest version must be 1.");

        var hasNullCollection = manifest.Artifacts is null;
        var rawEntries = manifest.Artifacts ?? [];
        var manifestEntries = rawEntries.OfType<OfficeArtifactManifestEntry>().ToArray();
        var duplicateIds = manifestEntries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.ArtifactId))
            .GroupBy(entry => entry.ArtifactId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var artifacts = new List<OfficeFileArtifact>();
        var issues = duplicateIds.Select(id => new FileRepositoryIssue(
            $"office.{id}", "Duplicate artifact ID was rejected.")).ToList();
        if (hasNullCollection)
            issues.Add(new FileRepositoryIssue("office.invalid",
                "The manifest artifact collection cannot be null."));
        if (manifestEntries.Length != rawEntries.Count)
            issues.Add(new FileRepositoryIssue("office.invalid",
                "A null manifest entry was rejected."));

        foreach (var entry in manifestEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (duplicateIds.Contains(entry.ArtifactId))
                continue;
            if (!TryValidateEntry(entry, out var error))
            {
                issues.Add(new FileRepositoryIssue(
                    $"office.{SafeIssueId(entry.ArtifactId)}", error!));
                continue;
            }

            var path = Path.GetFullPath(Path.Combine(_officeRootPath, entry.FileName));
            if (!IsWithinOfficeRoot(path))
            {
                issues.Add(new FileRepositoryIssue($"office.{entry.ArtifactId}",
                    "Artifact path escaped the Office repository and was rejected."));
                continue;
            }

            var expectedHash = NormalizeSha256(entry.Sha256)!;
            var file = await InspectFileAsync(path, expectedHash, cancellationToken);
            if (file is null)
            {
                issues.Add(new FileRepositoryIssue($"office.{entry.ArtifactId}",
                    "Artifact is missing, empty, oversized, or does not match its SHA-256."));
                continue;
            }

            var descriptor = new OfficeRemediationArtifactDescriptor(entry.ArtifactId,
                entry.DisplayName.Trim(), entry.Description.Trim(), entry.FileName,
                expectedHash, entry.Kind, entry.SupportedProductFamilies,
                NormalizeVersion(entry.MinOfficeVersion), NormalizeVersion(entry.MaxOfficeVersion),
                entry.ActivationStates, entry.ErrorCodes.Select(NormalizeErrorCode).ToArray(),
                entry.RequiresInteractiveUser, entry.RequiresOfficeAppsClosed,
                entry.RequiresRestart, $"/api/files/office/{entry.ArtifactId}");
            artifacts.Add(new OfficeFileArtifact(file, descriptor));
        }

        return new OfficeArtifactCatalogSnapshot(
            artifacts.OrderBy(item => item.Descriptor.ArtifactId,
                StringComparer.OrdinalIgnoreCase).ToArray(), issues);
    }

    public async Task<OfficeFileArtifact?> GetOfficeArtifactAsync(string artifactId,
        CancellationToken cancellationToken)
    {
        if (!ArtifactIdPattern().IsMatch(artifactId ?? string.Empty))
            return null;
        var catalog = await GetOfficeCatalogAsync(cancellationToken);
        return catalog.Artifacts.FirstOrDefault(item => item.Descriptor.ArtifactId.Equals(
            artifactId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<OfficeBugfixCatalogSnapshot> GetLegacyOfficeBugfixCatalogAsync(
        CancellationToken cancellationToken)
    {
        if (!PrepareTrustedRoot())
            return InvalidBugfixManifest(
                "The Office bugfix repository is not protected from untrusted writes.");
        if (!File.Exists(_officeBugfixManifestPath))
            return new OfficeBugfixCatalogSnapshot([], []);

        OfficeBugfixManifest manifest;
        try
        {
            var manifestInfo = new FileInfo(_officeBugfixManifestPath);
            if (manifestInfo.Length is <= 0 or > MaxManifestBytes
                || (manifestInfo.Attributes & FileAttributes.ReparsePoint) != 0
                || !IsTrustedFile(_officeBugfixManifestPath))
                return InvalidBugfixManifest(
                    "The Office bugfix manifest is empty, too large, or a reparse point.");
            await using var stream = new FileStream(_officeBugfixManifestPath,
                FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024,
                FileOptions.Asynchronous);
            manifest = await JsonSerializer.DeserializeAsync<OfficeBugfixManifest>(stream,
                new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken)
                ?? new OfficeBugfixManifest();
        }
        catch (Exception ex) when (ex is JsonException or IOException
            or UnauthorizedAccessException)
        {
            return InvalidBugfixManifest(
                $"The Office bugfix manifest could not be read: {ex.GetType().Name}.");
        }

        if (manifest.Version != 1)
            return InvalidBugfixManifest("The Office bugfix manifest version must be 1.");

        var rawEntries = manifest.Artifacts ?? [];
        var manifestEntries = rawEntries.OfType<OfficeBugfixManifestEntry>().ToArray();
        var duplicateIds = FindDuplicates(manifestEntries.Select(entry => entry.ArtifactId));
        var duplicateFiles = FindDuplicates(manifestEntries.Select(entry => entry.FileName));
        var artifacts = new List<OfficeBugfixFileArtifact>();
        var issues = new List<FileRepositoryIssue>();
        if (manifestEntries.Length != rawEntries.Count)
            issues.Add(new FileRepositoryIssue("office_bugfix.invalid",
                "A null manifest entry was rejected."));
        issues.AddRange(duplicateIds.Select(id => new FileRepositoryIssue(
            $"office_bugfix.{id}", "Duplicate artifact ID was rejected.")));
        issues.AddRange(duplicateFiles.Select(file => new FileRepositoryIssue(
            $"office_bugfix.{SafeIssueId(file)}", "Duplicate artifact filename was rejected.")));

        foreach (var entry in manifestEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (duplicateIds.Contains(entry.ArtifactId)
                || duplicateFiles.Contains(entry.FileName))
                continue;
            if (!TryValidateBugfixEntry(entry, out var playbook, out var error))
            {
                issues.Add(new FileRepositoryIssue(
                    $"office_bugfix.{SafeIssueId(entry.ArtifactId)}", error!));
                continue;
            }

            var path = Path.GetFullPath(Path.Combine(_rootPath, entry.FileName));
            if (!IsWithinRoot(path))
            {
                issues.Add(new FileRepositoryIssue($"office_bugfix.{entry.ArtifactId}",
                    "Artifact path escaped the file repository and was rejected."));
                continue;
            }

            var expectedHash = NormalizeSha256(entry.Sha256)!;
            var file = await InspectFileAsync(path, expectedHash, cancellationToken);
            if (file is null)
            {
                issues.Add(new FileRepositoryIssue($"office_bugfix.{entry.ArtifactId}",
                    "Artifact is missing, empty, oversized, a reparse point, or does not match its SHA-256."));
                continue;
            }

            var (supportFiles, supportError) = await LoadSupportFilesAsync(
                Path.Combine(_rootPath, $"{entry.ArtifactId}.files"),
                entry.ArtifactId, entry.SupportFiles,
                $"/api/files/office/bugfix/{entry.ArtifactId}/file",
                cancellationToken);
            if (supportError is not null)
            {
                issues.Add(new FileRepositoryIssue(
                    $"office_bugfix.{entry.ArtifactId}", supportError));
                continue;
            }
            if (!await PassesLintAsync(file, $"office_bugfix.{entry.ArtifactId}",
                    issues, cancellationToken))
                continue;

            artifacts.Add(new OfficeBugfixFileArtifact(file, CreateOfficeBugfixDescriptor(
                entry, playbook!, expectedHash, supportFiles!)));
        }

        return new OfficeBugfixCatalogSnapshot(
            artifacts.OrderBy(item => new[] { item.Descriptor.MinOfficeVersion }
                    .Concat(item.Descriptor.AdditionalCompatibility?.Select(rule =>
                        rule.MinOfficeVersion) ?? [])
                    .Select(Version.Parse).Min())
                .ThenBy(item => item.Descriptor.ArtifactId,
                    StringComparer.OrdinalIgnoreCase).ToArray(), issues);
    }

    public async Task<OfficeBugfixFileArtifact?> GetOfficeBugfixArtifactAsync(
        string artifactId, CancellationToken cancellationToken)
    {
        if (!ArtifactIdPattern().IsMatch(artifactId ?? string.Empty))
            return null;
        var catalog = await GetOfficeBugfixCatalogAsync(cancellationToken);
        return catalog.Artifacts.FirstOrDefault(item => item.Descriptor.ArtifactId.Equals(
            artifactId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<FileArtifact?> GetOfficeBugfixSupportFileAsync(
        string artifactId, string fileName, CancellationToken cancellationToken)
    {
        var artifact = await GetOfficeBugfixArtifactAsync(artifactId,
            cancellationToken);
        if (artifact?.Descriptor.SupportFiles?.Any(item =>
                item.FileName == fileName) != true)
            return null;
        return await InspectFileAsync(Path.Combine(artifact.SupportDirectory
                ?? Path.Combine(_rootPath, $"{artifactId}.files"), fileName),
            artifact.Descriptor.SupportFiles.Single(item =>
                item.FileName == fileName).Sha256, cancellationToken);
    }

    private async Task<WindowsBugfixCatalogSnapshot> GetLegacyWindowsBugfixCatalogAsync(
        CancellationToken cancellationToken)
    {
        if (!PrepareTrustedDirectory(_windowsRootPath))
            return new WindowsBugfixCatalogSnapshot([], [new FileRepositoryIssue(
                "windows_bugfix.directory",
                "The Windows artifact directory is redirected or writable by an untrusted account.")]);
        if (!IsWithinRoot(_windowsBugfixFilePath)
            || !IsWithinRoot(_windowsHelloSampleFilePath)
            || Path.GetFileName(_windowsBugfixFilePath) != WindowsRemediationPaths.FileName
            || Path.GetFileName(_windowsHelloSampleFilePath) != WindowsHelloSamplePaths.FileName)
            return new WindowsBugfixCatalogSnapshot([], [new FileRepositoryIssue(
                "windows_bugfix.file",
                "The Windows artifact filenames are not approved.")]);

        var artifacts = new List<WindowsBugfixFileArtifact>();
        var issues = new List<FileRepositoryIssue>();
        if (File.Exists(_windowsBugfixManifestPath))
        {
            WindowsBugfixManifest manifest;
            try
            {
                var info = new FileInfo(_windowsBugfixManifestPath);
                if (info.Length is <= 0 or > MaxManifestBytes
                    || (info.Attributes & FileAttributes.ReparsePoint) != 0
                    || !IsTrustedFile(_windowsBugfixManifestPath))
                    throw new InvalidDataException("Manifest file is not trusted.");
                await using var stream = File.OpenRead(_windowsBugfixManifestPath);
                manifest = await JsonSerializer.DeserializeAsync<WindowsBugfixManifest>(
                    stream, new JsonSerializerOptions(JsonSerializerDefaults.Web),
                    cancellationToken) ?? new WindowsBugfixManifest();
                if (manifest.Version != 1)
                    throw new InvalidDataException("Manifest version must be 1.");
            }
            catch (Exception ex) when (ex is JsonException or IOException
                or UnauthorizedAccessException or InvalidDataException)
            {
                manifest = new WindowsBugfixManifest();
                issues.Add(new FileRepositoryIssue("windows_bugfix.manifest",
                    $"The Windows bugfix manifest could not be read: {ex.GetType().Name}."));
            }

            var entries = manifest.Artifacts ?? [];
            var duplicateIds = FindDuplicates(entries.OfType<WindowsBugfixManifestEntry>()
                .Select(item => item.ArtifactId));
            if (entries.Any(item => item is null))
                issues.Add(new FileRepositoryIssue("windows_bugfix.invalid",
                    "A null manifest entry was rejected."));
            foreach (var entry in entries.OfType<WindowsBugfixManifestEntry>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                BatchPlaybook? playbook = null;
                string? error = null;
                if (duplicateIds.Contains(entry.ArtifactId)
                    || !TryValidateWindowsBugfixEntry(entry, out playbook,
                        out error))
                {
                    issues.Add(new FileRepositoryIssue(
                        $"windows_bugfix.{SafeIssueId(entry.ArtifactId)}",
                        error ?? "Duplicate artifact ID was rejected."));
                    continue;
                }
                var path = Path.GetFullPath(Path.Combine(_windowsRootPath,
                    entry.FileName));
                if (!IsWithinRoot(path))
                {
                    issues.Add(new FileRepositoryIssue(
                        $"windows_bugfix.{entry.ArtifactId}",
                        "Artifact path escaped the file repository."));
                    continue;
                }
                var file = await InspectFileAsync(path,
                    NormalizeSha256(entry.Sha256), cancellationToken);
                if (file is null)
                {
                    issues.Add(new FileRepositoryIssue(
                        $"windows_bugfix.{entry.ArtifactId}",
                        "Artifact is missing or does not match its SHA-256."));
                    continue;
                }
                var (supportFiles, supportError) = await LoadSupportFilesAsync(
                    Path.Combine(_windowsRootPath, $"{entry.ArtifactId}.files"),
                    entry.ArtifactId, entry.SupportFiles,
                    $"/api/files/windows/bugfix/{entry.ArtifactId}/file",
                    cancellationToken);
                if (supportError is not null)
                {
                    issues.Add(new FileRepositoryIssue(
                        $"windows_bugfix.{entry.ArtifactId}", supportError));
                    continue;
                }
                if (!await PassesLintAsync(file, $"windows_bugfix.{entry.ArtifactId}",
                        issues, cancellationToken))
                    continue;
                artifacts.Add(new WindowsBugfixFileArtifact(file,
                    CreateWindowsBugfixDescriptor(entry, playbook!, file.Sha256,
                        supportFiles!)));
            }
        }
        var activation = await InspectFileAsync(_windowsBugfixFilePath,
            expectedSha256: null, cancellationToken);
        if (activation is not null)
            artifacts.Add(new WindowsBugfixFileArtifact(activation,
                new WindowsBugfixArtifactDescriptor(
                    WindowsRemediationPaths.ArtifactId,
                    "Windows activation repair",
                    "Administrator-reviewed Windows activation diagnostic and repair batch file.",
                    WindowsRemediationPaths.FileName, activation.Sha256,
                    "/api/files/windows/bugfix/windows-activation-repair")));

        var hello = await InspectFileAsync(_windowsHelloSampleFilePath,
            expectedSha256: null, cancellationToken);
        if (hello is not null)
            artifacts.Add(new WindowsBugfixFileArtifact(hello,
                new WindowsBugfixArtifactDescriptor(
                    WindowsHelloSamplePaths.ArtifactId,
                    "Windows Hello terminal demo",
                    "Copies a harmless Hello batch file to the signed-in user's desktop and opens it visibly.",
                    WindowsHelloSamplePaths.FileName, hello.Sha256,
                    "/api/files/windows/bugfix/windows-hello-sample")));
        return new WindowsBugfixCatalogSnapshot(artifacts
            .OrderBy(item => item.Descriptor.MinWindowsVersion is null
                ? new Version(0, 0)
                : new[] { item.Descriptor.MinWindowsVersion }
                    .Concat(item.Descriptor.AdditionalCompatibility?.Select(rule =>
                        rule.MinWindowsVersion) ?? [])
                    .Select(Version.Parse).Min())
            .ThenBy(item => item.Descriptor.ArtifactId,
                StringComparer.OrdinalIgnoreCase).ToArray(), issues);
    }

    public async Task<WindowsBugfixFileArtifact?> GetWindowsBugfixArtifactAsync(
        string artifactId, CancellationToken cancellationToken)
    {
        if (!ArtifactIdPattern().IsMatch(artifactId ?? string.Empty)) return null;
        var catalog = await GetWindowsBugfixCatalogAsync(cancellationToken);
        return catalog.Artifacts.SingleOrDefault(item =>
            item.Descriptor.ArtifactId == artifactId);
    }

    public async Task<FileArtifact?> GetWindowsBugfixSupportFileAsync(
        string artifactId, string fileName, CancellationToken cancellationToken)
    {
        var artifact = await GetWindowsBugfixArtifactAsync(artifactId,
            cancellationToken);
        if (artifact?.Descriptor.SupportFiles?.Any(item =>
                item.FileName == fileName) != true)
            return null;
        return await InspectFileAsync(Path.Combine(artifact.SupportDirectory
                ?? Path.Combine(_windowsRootPath, $"{artifactId}.files"), fileName),
            artifact.Descriptor.SupportFiles.Single(item =>
                item.FileName == fileName).Sha256, cancellationToken);
    }

    private async Task<(IReadOnlyList<BatchSupportFile>? Files, string? Error)>
        LoadSupportFilesAsync(string supportDirectory, string artifactId,
            IReadOnlyList<BatchSupportFileManifestEntry> entries,
            string downloadPrefix, CancellationToken cancellationToken)
    {
        if (entries is null || entries.Count > 32
            || entries.Any(entry => entry is null
                || !BatchSupportFilePolicy.IsValidName(entry.FileName)
                || entry.FileName.Equals($"{artifactId}.bat",
                    StringComparison.OrdinalIgnoreCase)
                || !BatchSupportFilePolicy.IsSha256(entry.Sha256))
            || entries.Select(entry => entry.FileName)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
            return (null, "Supporting file names or hashes are invalid.");
        if (entries.Count == 0) return ([], null);
        var directory = Path.GetFullPath(supportDirectory);
        if (!IsWithinRoot(directory) || !Directory.Exists(directory)
            || !PrepareTrustedDirectory(directory))
            return (null, "The supporting file directory is missing or untrusted.");
        var files = new List<BatchSupportFile>(entries.Count);
        foreach (var entry in entries)
        {
            var file = await InspectFileAsync(Path.Combine(directory,
                    entry.FileName), NormalizeSha256(entry.Sha256),
                cancellationToken);
            if (file is null)
                return (null, $"Supporting file {entry.FileName} is missing or has a hash mismatch.");
            files.Add(new BatchSupportFile(entry.FileName, file.Sha256,
                $"{downloadPrefix}/{entry.FileName}"));
        }
        return (files, null);
    }

    private static bool TryValidateWindowsBugfixEntry(
        WindowsBugfixManifestEntry entry, out BatchPlaybook? playbook,
        out string? error)
    {
        playbook = null;
        if (entry.ArtifactId is not { } id
            || !ArtifactIdPattern().IsMatch(id)
            || !char.IsAsciiLetterOrDigit(id[^1])
            || id is WindowsRemediationPaths.ArtifactId
                or WindowsHelloSamplePaths.ArtifactId)
            return Fail("A unique Windows repair artifact ID is required.", out error);
        if (string.IsNullOrWhiteSpace(entry.DisplayName)
            || string.IsNullOrWhiteSpace(entry.Description))
            return Fail("Display name and description are required.", out error);
        if (entry.FileName != $"{id}.bat"
            || NormalizeSha256(entry.Sha256) is null)
            return Fail("BAT filename or SHA-256 is invalid.", out error);
        if (!TryValidateWindowsCompatibility(new WindowsBugfixCompatibilityRange(
                entry.MinWindowsVersion, entry.MaxWindowsVersionExclusive,
                entry.EditionIds), out error))
            return false;
        if (entry.AdditionalCompatibility is null
            || entry.AdditionalCompatibility.Count > 31
            || entry.AdditionalCompatibility.Any(rule => rule is null
                || !TryValidateWindowsCompatibility(rule, out _)))
            return Fail("Additional Windows compatibility ranges are invalid.",
                out error);
        if (!BatchPlaybookPolicy.TryNormalize(entry.Playbook, out playbook,
                out var playbookError))
            return Fail($"Invalid playbook: {playbookError}", out error);
        if (BatchPlaybookPolicy.HasArguments(playbook))
            return Fail("Invalid playbook: arguments are supported only by schema 2 catalog packages.",
                out error);
        error = null;
        return true;
    }

    private static bool TryValidateWindowsCompatibility(
        WindowsBugfixCompatibilityRange rule, out string? error)
    {
        if (!Version.TryParse(rule.MinWindowsVersion, out var minimum)
            || !Version.TryParse(rule.MaxWindowsVersionExclusive,
                out var maximum) || minimum >= maximum)
            return Fail("Windows version bounds are invalid.", out error);
        if (rule.EditionIds is null || rule.EditionIds.Any(value =>
                string.IsNullOrWhiteSpace(value) || value.Length > 64
                || !value.All(character => char.IsAsciiLetterOrDigit(character)
                    || character is '-' or '_'))
            || rule.EditionIds.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != rule.EditionIds.Count)
            return Fail("Windows edition IDs must be unique identifiers.", out error);
        error = null;
        return true;
    }

    private static OfficeBugfixArtifactDescriptor CreateOfficeBugfixDescriptor(
        OfficeBugfixManifestEntry entry, BatchPlaybook playbook, string sha256,
        IReadOnlyList<BatchSupportFile> supportFiles) => new(entry.ArtifactId,
            entry.DisplayName.Trim(), entry.Description.Trim(), entry.FileName,
            sha256, Version.Parse(entry.MinOfficeVersion).ToString(),
            Version.Parse(entry.MaxOfficeVersionExclusive).ToString(),
            playbook, BatchPlaybookPolicy.ComputeSha256(playbook),
            $"/api/files/office/bugfix/{entry.ArtifactId}",
            entry.OfficeProductReleaseIds.ToArray(),
            entry.MinWindowsVersion is null ? null
                : Version.Parse(entry.MinWindowsVersion).ToString(),
            entry.MaxWindowsVersionExclusive is null ? null
                : Version.Parse(entry.MaxWindowsVersionExclusive).ToString(),
            supportFiles, supportFiles.Count == 0 ? null
                : BatchSupportFilePolicy.ComputeBundleSha256(sha256, supportFiles),
            entry.AdditionalCompatibility.Select(NormalizeOfficeCompatibility).ToArray());

    private static WindowsBugfixArtifactDescriptor CreateWindowsBugfixDescriptor(
        WindowsBugfixManifestEntry entry, BatchPlaybook playbook, string sha256,
        IReadOnlyList<BatchSupportFile> supportFiles) => new(entry.ArtifactId,
            entry.DisplayName.Trim(), entry.Description.Trim(), entry.FileName, sha256,
            $"/api/files/windows/bugfix/{entry.ArtifactId}",
            Version.Parse(entry.MinWindowsVersion).ToString(),
            Version.Parse(entry.MaxWindowsVersionExclusive).ToString(),
            entry.EditionIds.ToArray(), playbook,
            BatchPlaybookPolicy.ComputeSha256(playbook),
            supportFiles, supportFiles.Count == 0 ? null
                : BatchSupportFilePolicy.ComputeBundleSha256(sha256, supportFiles),
            entry.AdditionalCompatibility.Select(NormalizeWindowsCompatibility).ToArray());

    private static WindowsBugfixCompatibilityRange NormalizeWindowsCompatibility(
        WindowsBugfixCompatibilityRange rule) => new(
            Version.Parse(rule.MinWindowsVersion).ToString(),
            Version.Parse(rule.MaxWindowsVersionExclusive).ToString(),
            rule.EditionIds.ToArray());

    /// <summary>Reports lint warnings and rejects BATs with lint errors.</summary>
    private static async Task<bool> PassesLintAsync(FileArtifact file, string source,
        List<FileRepositoryIssue> issues, CancellationToken cancellationToken)
    {
        var lint = BatScriptLint.Inspect(
            await File.ReadAllTextAsync(file.FullPath, cancellationToken));
        issues.AddRange(lint.Warnings.Select(warning =>
            new FileRepositoryIssue(source, $"Warning: {warning}")));
        issues.AddRange(lint.Errors.Select(error =>
            new FileRepositoryIssue(source, $"Rejected: {error}")));
        return lint.Errors.Count == 0;
    }

    private async Task<FileArtifact?> InspectFileAsync(string path, string? expectedSha256,
        CancellationToken cancellationToken)
    {
        var candidate = File.Exists(path) ? new FileInfo(path) : null;
        if (candidate is null || candidate.Length is <= 0
            || candidate.Length > _options.MaxFileSizeBytes
            || (candidate.Attributes & FileAttributes.ReparsePoint) != 0
            || !IsTrustedFile(candidate.FullName))
            return null;

        await using var stream = new FileStream(candidate.FullName, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (expectedSha256 is not null
            && !hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            return null;
        return new FileArtifact(candidate.FullName, candidate.Name, candidate.Length,
            candidate.LastWriteTimeUtc, hash);
    }

    private bool PrepareTrustedRoot()
    {
        if (!_options.RequireProtectedAcl)
        {
            Directory.CreateDirectory(_rootPath);
            return true;
        }
        return OperatingSystem.IsWindows()
            && ProtectedWindowsPath.IsDirectorySecure(_rootPath,
                requireProtectedAcl: true);
    }

    private bool PrepareTrustedDirectory(string path)
    {
        if (!PrepareTrustedRoot()) return false;
        Directory.CreateDirectory(path);
        return !_options.RequireProtectedAcl
            || OperatingSystem.IsWindows()
            && ProtectedWindowsPath.IsDirectorySecure(_rootPath,
                requireProtectedAcl: true)
            && ProtectedWindowsPath.IsDirectorySecure(path,
                requireProtectedAcl: false);
    }

    private bool IsTrustedFile(string path) => !_options.RequireProtectedAcl
        || OperatingSystem.IsWindows() && ProtectedWindowsPath.IsFileSecure(path);

    private bool TryValidateEntry(OfficeArtifactManifestEntry entry, out string? error)
    {
        if (!ArtifactIdPattern().IsMatch(entry.ArtifactId ?? string.Empty))
            return Fail("Artifact ID must contain only lowercase letters, numbers, dots, underscores, or hyphens.", out error);
        if (string.IsNullOrWhiteSpace(entry.DisplayName) || string.IsNullOrWhiteSpace(entry.Description))
            return Fail("Display name and description are required.", out error);
        if (string.IsNullOrWhiteSpace(entry.FileName)
            || Path.GetFileName(entry.FileName) != entry.FileName
            || !AllowedOfficeExtensions.Contains(Path.GetExtension(entry.FileName)))
            return Fail("Artifact filename or extension is not allowed.", out error);
        if (NormalizeSha256(entry.Sha256) is null)
            return Fail("Artifact SHA-256 must contain exactly 64 hexadecimal characters.", out error);
        if (!AllowedOfficeKinds.Contains(entry.Kind ?? string.Empty))
            return Fail("Artifact kind is not supported.", out error);
        if ((entry.Kind ?? string.Empty).Equals(
                OfficeRemediationArtifactKinds.MicrosoftSignedExecutable,
                StringComparison.OrdinalIgnoreCase)
            && !Path.GetExtension(entry.FileName).Equals(".exe",
                StringComparison.OrdinalIgnoreCase))
            return Fail("MicrosoftSignedExecutable artifacts must use the .exe extension.", out error);
        if (entry.SupportedProductFamilies is not { Count: > 0 }
            || entry.SupportedProductFamilies.Any(family => !AllowedFamilies.Contains(family)))
            return Fail("At least one supported Office product family is required.", out error);
        if (entry.ActivationStates is not { Count: > 0 }
            || entry.ActivationStates.Any(state => !AllowedStates.Contains(state)))
            return Fail("At least one supported activation state is required.", out error);
        if (!ValidVersion(entry.MinOfficeVersion) || !ValidVersion(entry.MaxOfficeVersion))
            return Fail("Office version bounds must use numeric dotted versions.", out error);
        if (Version.TryParse(entry.MinOfficeVersion, out var min)
            && Version.TryParse(entry.MaxOfficeVersion, out var max) && min > max)
            return Fail("Minimum Office version cannot exceed maximum version.", out error);
        if (entry.ErrorCodes is null
            || entry.ErrorCodes.Any(code => NormalizeErrorCode(code).Length == 0))
            return Fail("Error codes must use the form 0x followed by eight hexadecimal characters.", out error);
        error = null;
        return true;
    }

    private static bool TryValidateBugfixEntry(OfficeBugfixManifestEntry entry,
        out BatchPlaybook? playbook, out string? error)
    {
        playbook = null;
        if (entry.ArtifactId is not { } artifactId
            || !ArtifactIdPattern().IsMatch(artifactId))
            return Fail("Artifact ID must contain only lowercase letters, numbers, dots, underscores, or hyphens.", out error);
        if (!char.IsAsciiLetterOrDigit(artifactId[^1]))
            return Fail("Artifact ID must end with a lowercase letter or number.", out error);
        if (string.IsNullOrWhiteSpace(entry.DisplayName)
            || string.IsNullOrWhiteSpace(entry.Description))
            return Fail("Display name and description are required.", out error);
        if (string.IsNullOrWhiteSpace(entry.FileName)
            || Path.GetFileName(entry.FileName) != entry.FileName
            || !entry.FileName.Equals($"{artifactId}.bat",
                StringComparison.OrdinalIgnoreCase))
            return Fail("Artifact filename must be the artifact ID followed by .bat.", out error);
        if (NormalizeSha256(entry.Sha256) is null)
            return Fail("Artifact SHA-256 must contain exactly 64 hexadecimal characters.", out error);
        if (!TryValidateOfficeCompatibility(new OfficeBugfixCompatibility(
                entry.MinOfficeVersion, entry.MaxOfficeVersionExclusive,
                entry.OfficeProductReleaseIds, entry.MinWindowsVersion,
                entry.MaxWindowsVersionExclusive), out error))
            return false;
        if (entry.AdditionalCompatibility is null
            || entry.AdditionalCompatibility.Count > 31
            || entry.AdditionalCompatibility.Any(rule => rule is null
                || !TryValidateOfficeCompatibility(rule, out _)))
            return Fail("Additional Office compatibility ranges are invalid.",
                out error);
        if (!BatchPlaybookPolicy.TryNormalize(entry.Playbook, out playbook,
                out var playbookError))
            return Fail($"Invalid playbook: {playbookError}", out error);
        if (BatchPlaybookPolicy.HasArguments(playbook))
            return Fail("Invalid playbook: arguments are supported only by schema 2 catalog packages.",
                out error);
        error = null;
        return true;
    }

    private static bool TryValidateOfficeCompatibility(
        OfficeBugfixCompatibility rule, out string? error)
    {
        if (!Version.TryParse(rule.MinOfficeVersion, out var minimum)
            || !Version.TryParse(rule.MaxOfficeVersionExclusive, out var maximum))
            return Fail("Office version bounds must use numeric dotted versions.", out error);
        if (minimum >= maximum)
            return Fail("Minimum Office version must be lower than the exclusive maximum version.", out error);
        if (rule.OfficeProductReleaseIds is null
            || rule.OfficeProductReleaseIds.Any(id => string.IsNullOrWhiteSpace(id)
                || id.Length > 64 || !id.All(character =>
                    char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
            || rule.OfficeProductReleaseIds.Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != rule.OfficeProductReleaseIds.Count)
            return Fail("Office product release IDs must be unique, non-empty identifiers.", out error);
        if (rule.MinWindowsVersion is not null
            && !Version.TryParse(rule.MinWindowsVersion, out _)
            || rule.MaxWindowsVersionExclusive is not null
            && !Version.TryParse(rule.MaxWindowsVersionExclusive, out _))
            return Fail("Windows version bounds must use numeric dotted versions.", out error);
        if (rule.MinWindowsVersion is not null
            && rule.MaxWindowsVersionExclusive is not null
            && Version.Parse(rule.MinWindowsVersion)
                >= Version.Parse(rule.MaxWindowsVersionExclusive))
            return Fail("Minimum Windows version must be lower than the exclusive maximum version.", out error);
        error = null;
        return true;
    }

    private static OfficeBugfixCompatibility NormalizeOfficeCompatibility(
        OfficeBugfixCompatibility rule) => new(
            Version.Parse(rule.MinOfficeVersion).ToString(),
            Version.Parse(rule.MaxOfficeVersionExclusive).ToString(),
            rule.OfficeProductReleaseIds.ToArray(),
            rule.MinWindowsVersion is null ? null
                : Version.Parse(rule.MinWindowsVersion).ToString(),
            rule.MaxWindowsVersionExclusive is null ? null
                : Version.Parse(rule.MaxWindowsVersionExclusive).ToString());

    private bool IsWithinOfficeRoot(string path)
    {
        var prefix = _officeRootPath.TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsWithinRoot(string path)
    {
        var prefix = _rootPath.TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static OfficeArtifactCatalogSnapshot InvalidManifest(string message) =>
        new([], [new FileRepositoryIssue("office.manifest", message)]);

    private static OfficeBugfixCatalogSnapshot InvalidBugfixManifest(string message) =>
        new([], [new FileRepositoryIssue("office_bugfix.manifest", message)]);

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }

    private static string SafeIssueId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "invalid" : value;

    private static HashSet<string> FindDuplicates(IEnumerable<string> values) => values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
        .Where(group => group.Count() > 1)
        .Select(group => group.Key)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool ValidVersion(string? value) =>
        string.IsNullOrWhiteSpace(value) || Version.TryParse(value, out _);

    private static string? NormalizeVersion(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Version.Parse(value).ToString();

    private static string NormalizeErrorCode(string? value) =>
        value is not null && ErrorCodePattern().IsMatch(value.Trim())
            ? value.Trim().ToUpperInvariant()
            : string.Empty;

    private static string? NormalizeSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().Replace("-", string.Empty, StringComparison.Ordinal);
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit)
            ? normalized.ToUpperInvariant() : null;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex ArtifactIdPattern();

    [GeneratedRegex("^0x[0-9a-fA-F]{8}$")]
    private static partial Regex ErrorCodePattern();
}
