using EndpointAgents.Platform.Windows.Packages;
using EndpointAgents.Shared.Security;
using EndpointAgents.Shared.Updates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace EndpointAgents.Platform.Windows.Updates;

/// <summary>
/// Downloads one offered agent version, proves it is the administrator's signed build, unpacks
/// it next to the running version (nothing in use is touched), and starts the updater from the
/// new folder. The updater does the switch and the rollback; see EndpointAgents.Updater.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentUpdateInstaller(
    EndpointArtifactTransport transport,
    IOptions<AgentUpdateOptions> options,
    IOptions<PackageSigningOptions> signing,
    ILogger<AgentUpdateInstaller> logger)
{
    private readonly AgentUpdateOptions _options = options.Value;

    public string StatePath => _options.StatePath;

    /// <summary>The product folder: configured, or the parent of the running Service folder.</summary>
    public string ResolveInstallRoot(string runningServiceExe)
    {
        if (!string.IsNullOrWhiteSpace(_options.InstallRoot))
            return Path.GetFullPath(_options.InstallRoot).TrimEnd('\\');
        // ...\Endpoint\Service\EndpointAgents.Service.exe or ...\Endpoint\versions\1.2.3\Service\EndpointAgents.Service.exe
        var serviceDirectory = Path.GetDirectoryName(Path.GetFullPath(runningServiceExe))!;
        var parent = Path.GetDirectoryName(serviceDirectory)!;
        var grandParent = Path.GetDirectoryName(parent);
        return grandParent is not null && string.Equals(Path.GetFileName(grandParent),
                AgentUpdatePaths.VersionsDirectoryName, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(grandParent)!
            : parent;
    }

    /// <summary>Checks the offer without downloading anything. Null when it may be installed.</summary>
    public string? Reject(AgentUpdateOffer offer, string currentVersion, IReadOnlyCollection<string> failedVersions)
    {
        if (!AgentUpdatePolicy.TryValidate(offer.Manifest, out var error)) return error;
        if (offer.DownloadPath != AgentUpdateRoutes.Download(offer.Manifest.Version))
            return "The download path is not the agent update route.";
        if (!AgentUpdatePolicy.Verify(offer.Manifest, signing.Value.TrustedPublicKeys))
            return "The update is not signed by a trusted administrator key.";
        if (!AgentUpdatePolicy.IsNewer(offer.Manifest.Version, currentVersion))
            return $"{offer.Manifest.Version} is not newer than {currentVersion}.";
        if (failedVersions.Contains(offer.Manifest.Version, StringComparer.OrdinalIgnoreCase))
            return $"{offer.Manifest.Version} failed here before.";
        if (Environment.OSVersion.Version.Build < offer.Manifest.MinWindowsBuild)
            return "This Windows build is too old for the update.";
        return null;
    }

    /// <summary>
    /// Stages the update and starts the updater. Returns the new state, or throws with a
    /// plain reason when anything does not match the signed manifest.
    /// </summary>
    public async Task<AgentUpdateState> StageAndLaunchAsync(AgentUpdateOffer offer, string currentVersion,
        AgentUpdateState? previousState, CancellationToken cancellationToken)
    {
        var manifest = offer.Manifest;
        var runningExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("The running service path is unknown.");
        var installRoot = ResolveInstallRoot(runningExe);
        var programFiles = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)).TrimEnd('\\');
        if (!installRoot.StartsWith(programFiles + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Self-update works only for an installed agent below Program Files (this one runs from {installRoot}).");

        var stagingRoot = Path.GetFullPath(_options.StagingRoot);
        CreateProtectedDirectory(stagingRoot, allowUsersRead: false);
        ProtectedWindowsPath.EnsureDirectory(stagingRoot, requireProtectedAcl: true);
        var zipPath = Path.Combine(stagingRoot, manifest.FileName);
        await DownloadAsync(offer, zipPath, cancellationToken);

        var versionsRoot = Path.Combine(installRoot, AgentUpdatePaths.VersionsDirectoryName);
        var target = Path.Combine(versionsRoot, manifest.Version);
        if (Path.GetFullPath(target).Equals(Path.GetDirectoryName(Path.GetDirectoryName(runningExe)),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The update would overwrite the running version.");
        Directory.CreateDirectory(versionsRoot);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Extract(zipPath, target, manifest);
        File.Delete(zipPath);

        EnsureSiteSettings();
        var state = new AgentUpdateState
        {
            Version = manifest.Version,
            FromVersion = currentVersion,
            Phase = AgentUpdatePhases.Installing,
            InstallRoot = installRoot,
            PreviousServiceExe = Path.GetFullPath(runningExe),
            PreviousTrayCommand = ReadTrayRunValue(),
            NewServiceExe = Path.Combine(target, "Service", "EndpointAgents.Service.exe"),
            NewTrayExe = Path.Combine(target, "Tray", "EndpointAgents.TrayWeb.exe"),
            ServiceName = _options.ServiceName,
            HealthTimeoutSeconds = Math.Clamp(_options.HealthTimeoutSeconds, 30, 900),
            Message = "Starting the updater.",
            StartedAtUtc = DateTimeOffset.UtcNow,
            FailedVersions = previousState?.FailedVersions ?? []
        };
        state.Write(_options.StatePath);

        var updater = Path.Combine(target, "Updater", "EndpointAgents.Updater.exe");
        var start = new ProcessStartInfo(updater)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(updater)!
        };
        start.ArgumentList.Add("--state");
        start.ArgumentList.Add(_options.StatePath);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("The updater did not start.");
        logger.LogInformation("Agent update {From} -> {To}: updater started (process {Pid}).",
            currentVersion, manifest.Version, process.Id);
        return state;
    }

    private async Task DownloadAsync(AgentUpdateOffer offer, string zipPath, CancellationToken cancellationToken)
    {
        var manifest = offer.Manifest;
        using var client = transport.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(30);
        using var request = await transport.CreateGetRequestAsync(offer.DownloadPath, cancellationToken);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length != manifest.SizeBytes)
            throw new InvalidDataException("The download size does not match the signed manifest.");
        var temp = zipPath + ".part";
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > manifest.SizeBytes)
                    throw new InvalidDataException("The download is larger than the signed manifest says.");
                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            if (total != manifest.SizeBytes
                || !Convert.ToHexString(hash.GetHashAndReset()).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The download does not match the signed SHA-256.");
        }
        File.Move(temp, zipPath, overwrite: true);
    }

    /// <summary>Unpacks only the files the signed manifest lists, each checked against its hash.</summary>
    internal static void Extract(string zipPath, string target, AgentUpdateManifest manifest)
    {
        var expected = manifest.Files.ToDictionary(file => file.Path, file => file.Sha256,
            StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            var name = entry.FullName.Replace('\\', '/');
            if (!expected.TryGetValue(name, out var sha) || !AgentUpdatePolicy.IsSafeRelativePath(name))
                throw new InvalidDataException($"The update contains a file the manifest does not list: {name}");
            var destination = Path.GetFullPath(Path.Combine(target, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The update contains an unsafe path: {name}");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (var input = entry.Open())
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
            }
            using (var check = File.OpenRead(destination))
            {
                if (!Convert.ToHexString(SHA256.HashData(check)).Equals(sha, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{name} does not match its signed hash.");
            }
            seen.Add(name);
        }
        var missing = expected.Keys.FirstOrDefault(path => !seen.Contains(path));
        if (missing is not null) throw new InvalidDataException($"The update is missing {missing}.");
    }

    /// <summary>Creates a folder only SYSTEM and Administrators can change (installs made before self-update lack it).</summary>
    internal static void CreateProtectedDirectory(string path, bool allowUsersRead)
    {
        if (Directory.Exists(path)) return;
        var security = new DirectorySecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        if (allowUsersRead)
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, inherit, PropagationFlags.None,
                AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
    }

    private static string? ReadTrayRunValue()
    {
        using var key = Registry.LocalMachine.OpenSubKey(AgentUpdatePaths.RunKeyPath);
        return key?.GetValue(AgentUpdatePaths.TrayRunValueName) as string;
    }

    /// <summary>
    /// Copies this PC's own settings out of the program folder the first time an update runs, so
    /// the new version (with fresh default appsettings.json) keeps the same control plane and keys.
    /// </summary>
    private void EnsureSiteSettings()
    {
        if (File.Exists(AgentUpdatePaths.SiteSettingsPath)) return;
        var runningSettings = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(runningSettings)) return;
        var site = AgentSiteSettings.Extract(File.ReadAllText(runningSettings));
        var directory = Path.GetDirectoryName(AgentUpdatePaths.SiteSettingsPath)!;
        CreateProtectedDirectory(directory, allowUsersRead: true);
        ProtectedWindowsPath.EnsureDirectory(directory, requireProtectedAcl: false);
        File.WriteAllText(AgentUpdatePaths.SiteSettingsPath, site);
        logger.LogInformation("Saved this PC's settings to {Path} so updates keep them.",
            AgentUpdatePaths.SiteSettingsPath);
    }
}
