using EndpointAgents.Shared.Office;
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
    OfficeBugfixArtifactDescriptor Descriptor);

public sealed record OfficeBugfixCatalogSnapshot(
    IReadOnlyList<OfficeBugfixFileArtifact> Artifacts,
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
    private readonly string _officeBugfixFilePath;

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
        _officeBugfixFilePath = Path.GetFullPath(Path.Combine(_rootPath,
            _options.OfficeBugfixFileName));
    }

    public string RootPath => _rootPath;
    public string OfficeRootPath => _officeRootPath;
    public string OfficeManifestPath => _officeManifestPath;
    public string OfficeBugfixFilePath => _officeBugfixFilePath;
    public string IbsngDialerFileName => _options.IbsngDialerFileName;

    public async Task<FileArtifact?> GetIbsngDialerAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_rootPath);
        var path = Path.Combine(_rootPath, _options.IbsngDialerFileName);
        return await InspectFileAsync(path, expectedSha256: null, cancellationToken);
    }

    public async Task<OfficeArtifactCatalogSnapshot> GetOfficeCatalogAsync(
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_officeRootPath);
        if (!File.Exists(_officeManifestPath))
            return new OfficeArtifactCatalogSnapshot([], []);

        OfficeArtifactManifest manifest;
        try
        {
            var manifestInfo = new FileInfo(_officeManifestPath);
            if (manifestInfo.Length is <= 0 or > MaxManifestBytes)
                return InvalidManifest("The Office artifact manifest is empty or too large.");
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

        var duplicateIds = manifest.Artifacts
            .Where(entry => !string.IsNullOrWhiteSpace(entry.ArtifactId))
            .GroupBy(entry => entry.ArtifactId, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var artifacts = new List<OfficeFileArtifact>();
        var issues = duplicateIds.Select(id => new FileRepositoryIssue(
            $"office.{id}", "Duplicate artifact ID was rejected.")).ToList();

        foreach (var entry in manifest.Artifacts)
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

    public async Task<OfficeBugfixCatalogSnapshot> GetOfficeBugfixCatalogAsync(
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_rootPath);
        if (!IsWithinRoot(_officeBugfixFilePath)
            || !Path.GetFileName(_officeBugfixFilePath).Equals(
                "office-activation-repair.bat", StringComparison.OrdinalIgnoreCase))
            return new OfficeBugfixCatalogSnapshot([], [new FileRepositoryIssue(
                "office_bugfix.file", "The Office activation repair filename must be office-activation-repair.bat.")]);

        var file = await InspectFileAsync(_officeBugfixFilePath,
            expectedSha256: null, cancellationToken);
        if (file is null)
            return new OfficeBugfixCatalogSnapshot([], []);

        var descriptor = new OfficeBugfixArtifactDescriptor(
            "office-activation-repair", "Office activation repair",
            "Retailer-provided Office activation repair batch file.", file.FileName,
            file.Sha256, "0.0", "65536.0",
            "/api/files/office/bugfix/office-activation-repair");
        return new OfficeBugfixCatalogSnapshot(
            [new OfficeBugfixFileArtifact(file, descriptor)], []);
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

    private async Task<FileArtifact?> InspectFileAsync(string path, string? expectedSha256,
        CancellationToken cancellationToken)
    {
        var candidate = File.Exists(path) ? new FileInfo(path) : null;
        if (candidate is null || candidate.Length is <= 0
            || candidate.Length > _options.MaxFileSizeBytes)
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
        if (entry.SupportedProductFamilies.Count == 0
            || entry.SupportedProductFamilies.Any(family => !AllowedFamilies.Contains(family)))
            return Fail("At least one supported Office product family is required.", out error);
        if (entry.ActivationStates.Count == 0
            || entry.ActivationStates.Any(state => !AllowedStates.Contains(state)))
            return Fail("At least one supported activation state is required.", out error);
        if (!ValidVersion(entry.MinOfficeVersion) || !ValidVersion(entry.MaxOfficeVersion))
            return Fail("Office version bounds must use numeric dotted versions.", out error);
        if (Version.TryParse(entry.MinOfficeVersion, out var min)
            && Version.TryParse(entry.MaxOfficeVersion, out var max) && min > max)
            return Fail("Minimum Office version cannot exceed maximum version.", out error);
        if (entry.ErrorCodes.Any(code => NormalizeErrorCode(code).Length == 0))
            return Fail("Error codes must use the form 0x followed by eight hexadecimal characters.", out error);
        error = null;
        return true;
    }

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

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }

    private static string SafeIssueId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "invalid" : value;

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
