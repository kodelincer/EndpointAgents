using EndpointAgents.Shared.Actions;
using Microsoft.Win32;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Actions.SystemSide;

[SupportedOSPlatform("windows")]
internal static class UserProfiles
{
    /// <summary>The profile folder of a signed-in user, from the machine's profile list.</summary>
    public static string? PathOf(string sid)
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
        return key?.GetValue("ProfileImagePath") is string path && Directory.Exists(path)
            ? path : null;
    }
}

[SupportedOSPlatform("windows")]
public sealed class DiskUsageAction : IEndpointAction
{
    public string Id => "disk.usage";
    private const int MaxFiles = 200_000;

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var drives = DriveInfo.GetDrives()
            .Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
            .Select(drive => new
            {
                Letter = drive.Name.TrimEnd('\\'),
                SizeGb = Math.Round(drive.TotalSize / 1e9, 1),
                FreeGb = Math.Round(drive.AvailableFreeSpace / 1e9, 1),
                FreePercent = drive.TotalSize == 0 ? 0
                    : (int)(drive.AvailableFreeSpace * 100 / drive.TotalSize)
            }).ToArray();

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var systemDrive = Path.GetPathRoot(windows) ?? @"C:\";
        var places = new List<(string Name, string Path)>
        {
            ("Windows temporary files", Path.Combine(windows, "Temp")),
            ("Windows Update download cache", Path.Combine(windows, "SoftwareDistribution", "Download"))
        };
        if (invocation.UserSid is { } sid)
        {
            places.Add(("Recycle bin", Path.Combine(systemDrive, "$Recycle.Bin", sid)));
            if (UserProfiles.PathOf(sid) is { } profile)
            {
                places.Add(("User temporary files", Path.Combine(profile, "AppData", "Local", "Temp")));
                places.Add(("Downloads folder", Path.Combine(profile, "Downloads")));
            }
        }
        var sizes = places.Select(place => new
        {
            place.Name,
            SizeMb = Directory.Exists(place.Path) ? SizeMb(place.Path, cancellationToken) : 0
        }).ToArray();

        var low = drives.Where(drive => drive.FreePercent < 10 || drive.FreeGb < 5).ToArray();
        var biggest = sizes.MaxBy(item => item.SizeMb);
        return Task.FromResult(EndpointActionResult.Ok(
            (low.Length == 0 ? "Disk space is fine."
                : $"Low disk space on {string.Join(", ", low.Select(drive => $"{drive.Letter} ({drive.FreeGb} GB free)"))}.")
            + (biggest is { SizeMb: > 1024 } ? $" The largest cleanable place is {biggest.Name} ({biggest.SizeMb / 1024.0:0.0} GB)." : ""),
            new { Drives = drives, Places = sizes, LowSpace = low.Length > 0 }));
    }

    private static long SizeMb(string path, CancellationToken cancellationToken)
    {
        long bytes = 0;
        var count = 0;
        try
        {
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         IgnoreInaccessible = true,
                         AttributesToSkip = FileAttributes.ReparsePoint
                     }))
            {
                if (++count > MaxFiles || cancellationToken.IsCancellationRequested) break;
                bytes += file.Length;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return bytes / (1024 * 1024);
    }
}

[SupportedOSPlatform("windows")]
public sealed class RestartPendingAction : IEndpointAction
{
    public string Id => "restart.pending";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var reasons = new List<string>();
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        if (Exists(machine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
            reasons.Add("Windows components");
        if (Exists(machine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
            reasons.Add("Windows Update");
        using (var session = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager"))
            if (session?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 })
                reasons.Add("Files replaced by an installation");
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var lastBoot = DateTimeOffset.UtcNow - uptime;
        var hours = (int)uptime.TotalHours;
        return Task.FromResult(EndpointActionResult.Ok(
            (reasons.Count == 0 ? "No restart is pending." : $"A restart is pending ({string.Join(", ", reasons)}).")
            + $" The PC has been running for {(hours >= 48 ? $"{hours / 24} days" : $"{hours} hours")}.",
            new
            {
                Pending = reasons.Count > 0,
                Reasons = reasons,
                UptimeHours = hours,
                LastBootUtc = lastBoot
            }));
    }

    private static bool Exists(RegistryKey root, string path)
    {
        using var key = root.OpenSubKey(path);
        return key is not null;
    }
}
