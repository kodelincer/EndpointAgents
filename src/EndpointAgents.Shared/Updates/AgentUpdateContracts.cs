using EndpointAgents.Shared.Packages;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EndpointAgents.Shared.Updates;

/// <summary>Where endpoints download an agent update. Under /api/files so the device token applies.</summary>
public static class AgentUpdateRoutes
{
    public const string Root = "/api/files/agent-updates";

    public static string Download(string version) => $"{Root}/{version}/package";
}

/// <summary>Hub method names for agent updates.</summary>
public static class AgentUpdateHubMethods
{
    /// <summary>Endpoint -> control plane: is there a newer version for me?</summary>
    public const string Check = "CheckForAgentUpdate";

    /// <summary>Endpoint -> control plane: how an update went.</summary>
    public const string Report = "ReportAgentUpdate";

    /// <summary>Control plane -> endpoint: check for an update now (an admin changed the target).</summary>
    public const string Available = "AgentUpdateAvailable";
}

/// <summary>One file inside the update zip, pinned by its SHA-256.</summary>
public sealed record AgentUpdateFile(string Path, string Sha256);

/// <summary>
/// A signed description of one agent version: the zip, every file in it, and the oldest
/// Windows build it runs on. The administrator signs it on the admin PC with the same key
/// as repair packages, so neither the control plane nor the network can change what runs.
/// </summary>
public sealed record AgentUpdateManifest(
    int SchemaVersion,
    string Version,
    string FileName,
    string Sha256,
    long SizeBytes,
    int MinWindowsBuild,
    string Architecture,
    string ReleaseNotes,
    IReadOnlyList<AgentUpdateFile> Files,
    PackageSignature? Signature);

/// <summary>What the endpoint sends when it asks for an update.</summary>
public sealed record AgentUpdateCheck(
    string CurrentVersion,
    int WindowsBuild,
    string Architecture,
    IReadOnlyList<string> FailedVersions,
    bool Busy);

/// <summary>An update the control plane wants this endpoint to install.</summary>
public sealed record AgentUpdateOffer(AgentUpdateManifest Manifest, string DownloadPath);

public static class AgentUpdateStatuses
{
    public const string Downloading = "Downloading";
    public const string Installing = "Installing";
    public const string Succeeded = "Succeeded";
    public const string RolledBack = "RolledBack";
    public const string Failed = "Failed";

    public static bool IsKnown(string? status) =>
        status is Downloading or Installing or Succeeded or RolledBack or Failed;
}

/// <summary>What the endpoint reports about an update attempt.</summary>
public sealed record AgentUpdateReport(string FromVersion, string ToVersion, string Status, string Message);

/// <summary>Rules both sides use for agent update manifests.</summary>
public static partial class AgentUpdatePolicy
{
    public const int CurrentSchemaVersion = 1;
    private const string StatementHeader = "EndpointAgents.AgentUpdate.v1";

    /// <summary>The three folders an update may contain.</summary>
    public static readonly string[] Components = ["Service", "Tray", "Updater"];

    public const string ServiceExe = "Service/EndpointAgents.Service.exe";
    public const string TrayExe = "Tray/EndpointAgents.TrayWeb.exe";
    public const string UpdaterExe = "Updater/EndpointAgents.Updater.exe";

    [GeneratedRegex(@"^\d{1,5}\.\d{1,5}\.\d{1,5}(\.\d{1,5})?$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,120}$")]
    private static partial Regex PathPartPattern();

    public static bool IsValidVersion(string? version) =>
        version is not null && VersionPattern().IsMatch(version);

    /// <summary>The numeric part of a version, ignoring "-beta" or "+build" suffixes.</summary>
    public static Version? ParseVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var core = version.Trim().Split('-', '+')[0];
        return System.Version.TryParse(core, out var parsed) ? Normalize(parsed) : null;
    }

    private static Version Normalize(Version value) => new(value.Major, value.Minor,
        Math.Max(value.Build, 0), Math.Max(value.Revision, 0));

    /// <summary>True when <paramref name="candidate"/> is strictly newer than <paramref name="current"/>.</summary>
    public static bool IsNewer(string candidate, string current)
    {
        var next = ParseVersion(candidate);
        if (next is null) return false;
        var now = ParseVersion(current);
        return now is null || next > now;
    }

    public static string ExpectedFileName(string version) => $"endpoint-agent-{version}.zip";

    /// <summary>A path inside the zip: forward slashes, inside one of <see cref="Components"/>, no "..".</summary>
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 400 || path.Contains('\\')
            || path.StartsWith('/') || path.Contains(':'))
            return false;
        var parts = path.Split('/');
        if (parts.Length < 2 || !Components.Contains(parts[0], StringComparer.Ordinal)) return false;
        return parts.All(part => part is not ("." or "..") && PathPartPattern().IsMatch(part));
    }

    public static bool TryValidate(AgentUpdateManifest? manifest, out string error)
    {
        error = string.Empty;
        if (manifest is null) { error = "The manifest is missing."; return false; }
        if (manifest.SchemaVersion != CurrentSchemaVersion)
            error = $"Unsupported manifest schema {manifest.SchemaVersion}.";
        else if (!IsValidVersion(manifest.Version))
            error = "The version must look like 1.2.3.";
        else if (!string.Equals(manifest.FileName, ExpectedFileName(manifest.Version), StringComparison.Ordinal))
            error = $"The file name must be {ExpectedFileName(manifest.Version)}.";
        else if (!IsSha256(manifest.Sha256))
            error = "The zip hash must be a SHA-256 hex string.";
        else if (manifest.SizeBytes is <= 0 or > 1024L * 1024 * 1024)
            error = "The zip size is invalid.";
        else if (manifest.MinWindowsBuild is < 10240 or > 99999)
            error = "MinWindowsBuild must be a Windows 10 or 11 build number.";
        else if (manifest.Architecture != "x64")
            error = "Only x64 updates are supported.";
        else if (manifest.Files is null || manifest.Files.Count == 0 || manifest.Files.Count > 5000)
            error = "The file list is empty or too long.";
        else if (manifest.Files.Any(file => !IsSafeRelativePath(file.Path) || !IsSha256(file.Sha256)))
            error = "The file list contains an unsafe path or an invalid hash.";
        else if (manifest.Files.Select(file => file.Path.ToLowerInvariant()).Distinct().Count() != manifest.Files.Count)
            error = "The file list contains duplicates.";
        else if (!new[] { ServiceExe, TrayExe, UpdaterExe }.All(required =>
                     manifest.Files.Any(file => file.Path.Equals(required, StringComparison.OrdinalIgnoreCase))))
            error = "The update must contain the service, the Tray and the updater.";
        else if (manifest.Files.Any(file => file.Path.EndsWith("appsettings.Development.json", StringComparison.OrdinalIgnoreCase)))
            error = "Development settings must not be shipped.";
        return error.Length == 0;
    }

    public static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    /// <summary>One hash over the sorted file list, so the signature covers every file.</summary>
    public static string ComputeFilesDigest(IEnumerable<AgentUpdateFile> files)
    {
        var text = string.Join('\n', files
            .Select(file => $"{file.Path.ToLowerInvariant()}:{file.Sha256.ToUpperInvariant()}")
            .OrderBy(line => line, StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public static string BuildStatement(AgentUpdateManifest manifest) => string.Join('\n',
        StatementHeader, manifest.Version, manifest.FileName.ToLowerInvariant(),
        manifest.Sha256.ToUpperInvariant(), manifest.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        manifest.MinWindowsBuild.ToString(System.Globalization.CultureInfo.InvariantCulture),
        manifest.Architecture, ComputeFilesDigest(manifest.Files));

    public static bool Verify(AgentUpdateManifest manifest, IEnumerable<string>? trustedPublicKeys) =>
        PackageSignaturePolicy.Verify(BuildStatement(manifest), manifest.Signature, trustedPublicKeys);
}
