using EndpointAgents.Shared.Problems;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Diagnostics;

/// <summary>
/// What a problem report says about this PC, read once from the registry: no WMI, nothing about the
/// user, no serial numbers.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsDeviceFacts
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";

    public static ProblemDeviceFacts Read(string? agentVersion, string machineName)
    {
        var build = Value(CurrentVersionKey, "CurrentBuildNumber");
        var update = Registry.GetValue($@"HKEY_LOCAL_MACHINE\{CurrentVersionKey}", "UBR", null);
        return new ProblemDeviceFacts(agentVersion, machineName,
            OsName(build),
            Value(CurrentVersionKey, "EditionID"),
            Value(CurrentVersionKey, "DisplayVersion") ?? Value(CurrentVersionKey, "ReleaseId"),
            update is int number && build is not null ? $"{build}.{number}" : build,
            RuntimeInformation.OSArchitecture.ToString(),
            Value(BiosKey, "SystemManufacturer"),
            Value(BiosKey, "SystemProductName"));
    }

    /// <summary>Windows 11 still calls itself Windows 10 in the registry; the build tells them apart.</summary>
    public static string? OsName(string? build) =>
        !int.TryParse(build, out var number) ? null
            : number >= 22000 ? "Windows 11" : number >= 10240 ? "Windows 10" : "Windows";

    private static string? Value(string key, string name)
    {
        try { return (Registry.GetValue($@"HKEY_LOCAL_MACHINE\{key}", name, null) as string)?.Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or global::System.Security.SecurityException)
        {
            return null;
        }
    }
}
