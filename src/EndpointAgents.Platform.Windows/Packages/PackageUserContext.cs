using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace EndpointAgents.Platform.Windows.Packages;

/// <summary>
/// The Windows user a package runs for. Packages run as LocalSystem; a package that fixes
/// something in the user's own profile (for example a browser reset) reads these
/// environment variables instead of guessing whose profile to change.
/// </summary>
public sealed record PackageUserContext(string UserSid, string ProfilePath,
    string LocalAppDataPath)
{
    public const string SidVariable = "ENDPOINTAGENTS_USER_SID";
    public const string ProfileVariable = "ENDPOINTAGENTS_USER_PROFILE";
    public const string LocalAppDataVariable = "ENDPOINTAGENTS_USER_LOCALAPPDATA";

    /// <summary>
    /// Resolves a local or domain user's profile from the control plane's user SID.
    /// Returns null for a missing, well-known or service SID, or a profile that is not a
    /// plain local directory.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static PackageUserContext? Resolve(string? userSid)
    {
        if (string.IsNullOrWhiteSpace(userSid)) return null;
        SecurityIdentifier sid;
        try { sid = new SecurityIdentifier(userSid); }
        catch (ArgumentException) { return null; }
        // Only real user accounts (S-1-5-21-...) and Entra ID accounts (S-1-12-1-...).
        if (!sid.IsAccountSid() && !sid.Value.StartsWith("S-1-12-1-", StringComparison.Ordinal))
            return null;

        using var profileKey = Registry.LocalMachine.OpenSubKey(
            $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid.Value}");
        if (profileKey?.GetValue("ProfileImagePath") is not string configured) return null;
        var profile = Environment.ExpandEnvironmentVariables(configured);
        if (!IsPlainDirectory(profile)) return null;

        var localAppData = Path.Combine(profile, "AppData", "Local");
        using var shellFolders = Registry.Users.OpenSubKey(
            $@"{sid.Value}\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
        if (shellFolders?.GetValue("Local AppData", null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) is string redirected)
        {
            var expanded = redirected.Replace("%USERPROFILE%", profile,
                StringComparison.OrdinalIgnoreCase);
            if (Path.IsPathFullyQualified(expanded) && !expanded.Contains('%'))
                localAppData = expanded;
        }
        return new PackageUserContext(sid.Value, Path.GetFullPath(profile),
            Path.GetFullPath(localAppData));
    }

    public void Apply(ProcessStartInfo startInfo)
    {
        startInfo.Environment[SidVariable] = UserSid;
        startInfo.Environment[ProfileVariable] = ProfilePath;
        startInfo.Environment[LocalAppDataVariable] = LocalAppDataPath;
    }

    private static bool IsPlainDirectory(string path) =>
        Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal)
        && Directory.Exists(path)
        && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
}
