using ControlPlane.Api.Files;
using EndpointAgents.Shared.Security;
using EndpointAgents.Shared.Updates;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace ControlPlane.Api.Updates;

/// <summary>One agent version found in the file repository, with whether endpoints may get it.</summary>
public sealed record AgentUpdatePackage(
    string Version,
    AgentUpdateManifest? Manifest,
    string Status,
    string? Problem,
    string? ZipPath,
    DateTimeOffset? AddedAtUtc)
{
    public bool IsReady => Status == AgentUpdatePackageStatus.Ready;
}

public static class AgentUpdatePackageStatus
{
    public const string Ready = "Ready";
    public const string NotSigned = "Not signed";
    public const string HashMismatch = "Hash mismatch";
    public const string Invalid = "Invalid";
}

/// <summary>
/// Reads signed agent versions from <c>&lt;FileRepository&gt;\agent-updates\&lt;version&gt;\</c>:
/// <c>agent-update.json</c> (the signed manifest) next to <c>endpoint-agent-&lt;version&gt;.zip</c>.
/// A version is offered only when the manifest is valid, signed by a trusted key and the zip
/// matches its pinned SHA-256. The endpoint checks all of this again before it installs.
/// </summary>
public sealed class AgentUpdateRepository(IOptions<FileRepositoryOptions> options)
{
    public const string DirectoryName = "agent-updates";
    public const string ManifestFileName = "agent-update.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly FileRepositoryOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, (long Length, DateTime Modified, string Hash)> _hashes =
        new(StringComparer.OrdinalIgnoreCase);

    public string RootPath => Path.Combine(Path.GetFullPath(_options.RootPath), DirectoryName);

    public async Task<IReadOnlyList<AgentUpdatePackage>> GetAllAsync(CancellationToken cancellationToken)
    {
        var root = RootPath;
        if (!Directory.Exists(root)) return [];
        if (_options.RequireProtectedAcl && !(OperatingSystem.IsWindows()
                && ProtectedWindowsPath.IsDirectorySecure(Path.GetFullPath(_options.RootPath), requireProtectedAcl: true)))
            return [];
        var result = new List<AgentUpdatePackage>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (!AgentUpdatePolicy.IsValidVersion(name)) continue;
            result.Add(await LoadAsync(name, directory, cancellationToken));
        }
        return result
            .OrderByDescending(item => AgentUpdatePolicy.ParseVersion(item.Version))
            .ToArray();
    }

    public async Task<AgentUpdatePackage?> GetAsync(string version, CancellationToken cancellationToken)
    {
        if (!AgentUpdatePolicy.IsValidVersion(version)) return null;
        var directory = Path.Combine(RootPath, version);
        return Directory.Exists(directory) ? await LoadAsync(version, directory, cancellationToken) : null;
    }

    private async Task<AgentUpdatePackage> LoadAsync(string version, string directory,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(directory, ManifestFileName);
        var added = Directory.GetCreationTimeUtc(directory);
        AgentUpdatePackage Bad(string status, string problem, AgentUpdateManifest? manifest = null) =>
            new(version, manifest, status, problem, null, added);
        if (!File.Exists(manifestPath))
            return Bad(AgentUpdatePackageStatus.Invalid, $"{ManifestFileName} is missing.");
        AgentUpdateManifest? manifest;
        try
        {
            await using var stream = File.OpenRead(manifestPath);
            manifest = await JsonSerializer.DeserializeAsync<AgentUpdateManifest>(stream, Json, cancellationToken);
        }
        catch (JsonException ex) { return Bad(AgentUpdatePackageStatus.Invalid, $"The manifest is not valid JSON: {ex.Message}"); }
        if (!AgentUpdatePolicy.TryValidate(manifest, out var error))
            return Bad(AgentUpdatePackageStatus.Invalid, error, manifest);
        if (manifest!.Version != version)
            return Bad(AgentUpdatePackageStatus.Invalid, "The folder name and the manifest version differ.", manifest);
        if (_options.TrustedPackageSigningKeys.Count == 0)
            return Bad(AgentUpdatePackageStatus.NotSigned,
                "No trusted signing key is configured on the control plane (FileRepository:TrustedPackageSigningKeys).", manifest);
        if (!AgentUpdatePolicy.Verify(manifest, _options.TrustedPackageSigningKeys))
            return Bad(AgentUpdatePackageStatus.NotSigned,
                "The manifest is not signed by a trusted key. Run scripts\\new-agent-update.ps1 -Sign.", manifest);
        var zipPath = Path.Combine(directory, manifest.FileName);
        if (!File.Exists(zipPath))
            return Bad(AgentUpdatePackageStatus.HashMismatch, $"{manifest.FileName} is missing.", manifest);
        var info = new FileInfo(zipPath);
        if (info.Length != manifest.SizeBytes)
            return Bad(AgentUpdatePackageStatus.HashMismatch, "The zip size does not match the manifest.", manifest);
        var hash = await HashAsync(info, cancellationToken);
        if (!hash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            return Bad(AgentUpdatePackageStatus.HashMismatch, "The zip does not match its pinned SHA-256.", manifest);
        return new AgentUpdatePackage(version, manifest, AgentUpdatePackageStatus.Ready, null, zipPath, added);
    }

    private async Task<string> HashAsync(FileInfo info, CancellationToken cancellationToken)
    {
        if (_hashes.TryGetValue(info.FullName, out var cached)
            && cached.Length == info.Length && cached.Modified == info.LastWriteTimeUtc)
            return cached.Hash;
        await using var stream = info.OpenRead();
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        _hashes[info.FullName] = (info.Length, info.LastWriteTimeUtc, hash);
        return hash;
    }
}
