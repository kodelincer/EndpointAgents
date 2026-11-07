using EndpointAgents.Platform.Windows.SystemInfo;
using EndpointAgents.Shared.Packages;
using Microsoft.Win32;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Software;

/// <summary>
/// Reads the facts install requirements need, from the registry, WMI and Windows APIs.
/// Every read is small and read-only; nothing is changed on the PC. Values are read once per
/// instance, so create one per check.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMachineFacts : IMachineFacts
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string ProvisionedAppsPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications";
    private const string WebView2Client = @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    private readonly Lazy<(int Build, string Edition)> _windows = new(ReadWindows);
    private readonly Lazy<(int Width, int Height)?> _screen = new(ReadScreen);
    private readonly Lazy<bool> _domain = new(() =>
    {
        try { return DomainInfoSection.ReadJoinInformation().JoinType == "Domain"; }
        catch (InvalidOperationException) { return false; }
    });

    public int WindowsBuild => _windows.Value.Build;
    public string EditionId => _windows.Value.Edition;

    public string Architecture => RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        _ => "x86"
    };

    public long MemoryMb
    {
        get
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref status) ? (long)(status.TotalPhysical / 1024 / 1024) : 0;
        }
    }

    public long FreeSystemDiskMb
    {
        get
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            return new DriveInfo(root).AvailableFreeSpace / 1024 / 1024;
        }
    }

    public int CpuCores => Environment.ProcessorCount;

    public (int Width, int Height)? Screen => _screen.Value;

    public bool DomainJoined => _domain.Value;

    public bool HasRuntime(string runtimeId)
    {
        switch (runtimeId)
        {
            case "webview2":
                using (var key = Registry.LocalMachine.OpenSubKey(WebView2Client))
                    return key?.GetValue("pv") is string version && version.Length > 0 && version != "0.0.0.0";
            case "vcredist-2015-x64":
            case "vcredist-2015-x86":
                var arch = runtimeId.EndsWith("x64", StringComparison.Ordinal) ? "x64" : "x86";
                foreach (var path in new[] { $@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\{arch}",
                             $@"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\{arch}" })
                {
                    using var key = Registry.LocalMachine.OpenSubKey(path);
                    if (key?.GetValue("Installed") is int installed && installed == 1) return true;
                }
                return false;
            case "java-8":
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var key = baseKey.OpenSubKey(@"SOFTWARE\JavaSoft\Java Runtime Environment\1.8");
                    if (key is not null) return true;
                }
                return false;
            default:
                if (!runtimeId.StartsWith("dotnet-desktop-", StringComparison.Ordinal)) return false;
                var major = runtimeId["dotnet-desktop-".Length..];
                var shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "dotnet", "shared", "Microsoft.WindowsDesktop.App");
                return Directory.Exists(shared)
                    && Directory.EnumerateDirectories(shared, major + ".*").Any();
        }
    }

    public bool IsDetected(DetectionRule rule)
    {
        try
        {
            return rule.Kind switch
            {
                DetectionKinds.MsiProduct => UninstallKeys().Any(key => key.Name.Equals(rule.ProductCode,
                    StringComparison.OrdinalIgnoreCase)),
                DetectionKinds.UninstallEntry => UninstallKeys().Any(key =>
                    key.DisplayName?.StartsWith(rule.DisplayName!, StringComparison.OrdinalIgnoreCase) == true
                    && AtLeast(key.DisplayVersion, rule.MinVersion)),
                DetectionKinds.File => FileMatches(rule),
                DetectionKinds.MsixFamily => ProvisionedMatches(rule.FamilyName!),
                _ => false
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException)
        {
            return false;
        }
    }

    internal static string? ResolveRoot(string root) => root switch
    {
        PathRoots.ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        PathRoots.ProgramFilesX86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        PathRoots.ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        PathRoots.Windows => Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        PathRoots.Fonts => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts"),
        _ => null
    };

    private static bool FileMatches(DetectionRule rule)
    {
        if (ResolveRoot(rule.Root!) is not { Length: > 0 } root) return false;
        var path = Path.GetFullPath(Path.Combine(root, rule.Path!));
        if (!path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path))
            return false;
        if (rule.MinVersion is null) return true;
        var info = FileVersionInfo.GetVersionInfo(path);
        return AtLeast($"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}",
            rule.MinVersion);
    }

    /// <summary>Provisioned (all-user) MSIX packages: name_version_arch_resource_publisherId.</summary>
    private static bool ProvisionedMatches(string familyName)
    {
        var separator = familyName.LastIndexOf('_');
        var name = familyName[..separator];
        var publisher = familyName[(separator + 1)..];
        using var key = Registry.LocalMachine.OpenSubKey(ProvisionedAppsPath);
        return key?.GetSubKeyNames().Any(full => full.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase)
            && full.EndsWith("_" + publisher, StringComparison.OrdinalIgnoreCase)) == true;
    }

    private sealed record UninstallKey(string Name, string? DisplayName, string? DisplayVersion);

    private static IEnumerable<UninstallKey> UninstallKeys()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var uninstall = baseKey.OpenSubKey(UninstallPath);
            if (uninstall is null) continue;
            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var entry = uninstall.OpenSubKey(name);
                yield return new UninstallKey(name, entry?.GetValue("DisplayName") as string,
                    entry?.GetValue("DisplayVersion") as string);
            }
        }
    }

    internal static bool AtLeast(string? actual, string? minimum)
    {
        if (minimum is null) return true;
        return Version.TryParse(actual?.Trim(), out var have) && Version.TryParse(minimum, out var need)
            && Normalize(have) >= Normalize(need);
    }

    private static Version Normalize(Version version) => new(version.Major, version.Minor,
        Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    private static (int Build, string Edition) ReadWindows()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = int.TryParse(key?.GetValue("CurrentBuildNumber") as string, out var number)
            ? number : Environment.OSVersion.Version.Build;
        return (build, key?.GetValue("EditionID") as string ?? "Unknown");
    }

    /// <summary>
    /// The service runs in session 0 without a desktop, so the screen comes from the video
    /// adapters WMI reports (the largest active mode).
    /// </summary>
    private static (int Width, int Height)? ReadScreen()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT CurrentHorizontalResolution, CurrentVerticalResolution FROM Win32_VideoController");
            (int Width, int Height)? best = null;
            foreach (var item in searcher.Get())
            {
                using (item)
                {
                    if (item["CurrentHorizontalResolution"] is not uint width
                        || item["CurrentVerticalResolution"] is not uint height
                        || width == 0 || height == 0)
                        continue;
                    if (best is null || width * height > best.Value.Width * best.Value.Height)
                        best = ((int)width, (int)height);
                }
            }
            return best;
        }
        catch (ManagementException) { return null; }
        catch (COMException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
            TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
