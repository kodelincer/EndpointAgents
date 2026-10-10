using EndpointAgents.Platform.Windows.Windows;
using EndpointAgents.Shared.Support;
using Microsoft.Win32;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.SystemInfo;

/// <summary>Windows version, edition, install date, uptime, language and pending restarts.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsInfoSection : ISystemInfoSection
{
    public string Name => SystemInfoContracts.Windows;

    public Task<object?> CollectAsync(SystemInfoWarnings warnings,
        CancellationToken cancellationToken)
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = key?.GetValue("CurrentBuildNumber")?.ToString();
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);

        object? result = new
        {
            ProductName = WindowsVersionInfo.NormalizeProductName(
                key?.GetValue("ProductName")?.ToString(), build),
            Edition = key?.GetValue("EditionID")?.ToString(),
            DisplayVersion = key?.GetValue("DisplayVersion")?.ToString(),
            Build = build,
            UpdateBuildRevision = key?.GetValue("UBR"),
            InstallationType = key?.GetValue("InstallationType")?.ToString(),
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            InstalledOn = NetworkInfoSection.FromUnixSeconds(key?.GetValue("InstallDate")),
            LastBootUtc = DateTimeOffset.UtcNow - uptime,
            UptimeDays = Math.Round(uptime.TotalDays, 1),
            DisplayLanguage = CultureInfo.InstalledUICulture.Name,
            TimeZone = TimeZoneInfo.Local.Id,
            PendingRestart = warnings.Try("windows.pending_restart", ReadPendingRestart)
        };
        return Task.FromResult(result);
    }

    private static object ReadPendingRestart()
    {
        static bool KeyExists(string path)
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key is not null;
        }
        using var session = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\Session Manager");
        var componentServicing = KeyExists(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
        var windowsUpdate = KeyExists(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        var fileRename = session?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 };
        return new
        {
            Required = componentServicing || windowsUpdate || fileRename,
            ComponentServicing = componentServicing,
            WindowsUpdate = windowsUpdate,
            PendingFileRename = fileRename
        };
    }
}
