using EndpointAgents.Platform.Windows.Actions.Infrastructure;
using EndpointAgents.Shared.Actions;
using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.ServiceProcess;

namespace EndpointAgents.Platform.Windows.Actions.SystemSide;

[SupportedOSPlatform("windows")]
public sealed class SpoolerRestartAndClearAction : IEndpointAction
{
    public string Id => "spooler.restart_and_clear";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        using var spooler = new ServiceController("Spooler");
        await ServiceTools.StopAsync(spooler, TimeSpan.FromSeconds(30), cancellationToken);
        var folder = Path.Combine(Environment.SystemDirectory, "spool", "PRINTERS");
        var removed = 0;
        if (Directory.Exists(folder))
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var extension = Path.GetExtension(file);
                if (!extension.Equals(".SPL", StringComparison.OrdinalIgnoreCase)
                    && !extension.Equals(".SHD", StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(file); removed++; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        spooler.Start();
        await ServiceTools.WaitAsync(spooler, ServiceControllerStatus.Running,
            TimeSpan.FromSeconds(20), cancellationToken);
        return EndpointActionResult.Done(removed == 0
            ? "The print service was restarted; there were no stuck jobs."
            : $"The print service was restarted and {removed / 2.0:0} stuck jobs were removed.",
            new { RemovedJobFiles = removed });
    }
}

[SupportedOSPlatform("windows")]
public sealed class CertificateTrustAction : IEndpointAction
{
    public string Id => "cert.check_trust";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var expected = (invocation.Setting(ActionSettings.RootCaThumbprints) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant())
            .Where(item => item.Length == 40 && item.All(Uri.IsHexDigit))
            .ToArray();
        if (expected.Length == 0)
            return Task.FromResult(EndpointActionResult.Ok(
                "No organisation root certificates are listed in the environment map.",
                new { Expected = 0 }));
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var missing = new List<string>();
        var expired = new List<string>();
        foreach (var thumbprint in expected)
        {
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false);
            if (found.Count == 0) missing.Add(thumbprint);
            else if (found[0].NotAfter < DateTime.Now) expired.Add(thumbprint);
            foreach (var certificate in found) certificate.Dispose();
        }
        return Task.FromResult(EndpointActionResult.Ok(missing.Count + expired.Count == 0
                ? "The organisation's root certificates are trusted."
                : $"{missing.Count} organisation root certificate(s) missing, {expired.Count} expired.",
            new { Expected = expected.Length, Missing = missing, Expired = expired }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class RecentChangesAction : IEndpointAction
{
    public string Id => "system.recent_changes";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var days = invocation.IntArgument("days", 7);
        var since = WindowsEvents.Since(days);
        var changes = new List<(DateTimeOffset When, string Kind, string Name)>();

        foreach (var item in WindowsEvents.Read("Application",
            $"*[System[Provider[@Name='MsiInstaller'] and (EventID=1033 or EventID=1034) and {since}]]", 40))
            changes.Add((item.WhenUtc, item.EventId == 1033 ? "SoftwareInstalled" : "SoftwareRemoved",
                item.Message));
        foreach (var item in WindowsEvents.Read("System",
            $"*[System[Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] and (EventID=19 or EventID=20) and {since}]]", 40))
            changes.Add((item.WhenUtc, item.EventId == 19 ? "UpdateInstalled" : "UpdateFailed",
                item.Message));
        foreach (var item in WindowsEvents.Read("System",
            $"*[System[Provider[@Name='Microsoft-Windows-UserPnp'] and EventID=20001 and {since}]]", 20))
            changes.Add((item.WhenUtc, "DriverInstalled", item.Message));
        foreach (var item in WindowsEvents.Read("System",
            $"*[System[(EventID=6008 or EventID=1074 or EventID=41 or EventID=1001) and {since}]]", 40))
        {
            var kind = item.EventId switch
            {
                6008 => "UnexpectedShutdown",
                41 when item.Source.Contains("Kernel-Power", StringComparison.Ordinal) => "PowerLoss",
                1001 when item.Source.Contains("WER-SystemErrorReporting", StringComparison.Ordinal) => "BlueScreen",
                1074 => "RestartRequested",
                _ => null
            };
            if (kind is not null) changes.Add((item.WhenUtc, kind, item.Message));
        }

        var ordered = changes.OrderByDescending(item => item.When).Take(60)
            .Select(item => new { WhenUtc = item.When, item.Kind, Name = item.Name })
            .ToArray();
        var counts = ordered.GroupBy(item => item.Kind)
            .ToDictionary(group => group.Key, group => group.Count());
        return Task.FromResult(EndpointActionResult.Ok(ordered.Length == 0
                ? $"Nothing notable changed in the last {days} days."
                : $"{ordered.Length} changes in the last {days} days.",
            new { Days = days, Counts = counts, Changes = ordered }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class PerformanceSnapshotAction : IEndpointAction
{
    public string Id => "perf.snapshot";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var processes = Process.GetProcesses();
        var before = processes.ToDictionary(item => item.Id, CpuTime);
        ActionNative.GetSystemTimes(out var idle1, out var kernel1, out var user1);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        ActionNative.GetSystemTimes(out var idle2, out var kernel2, out var user2);
        var total = (kernel2.Value - kernel1.Value) + (user2.Value - user1.Value);
        var cpu = total == 0 ? 0 : Math.Round(100.0 * (total - (idle2.Value - idle1.Value)) / total, 0);

        var cores = Environment.ProcessorCount;
        var topCpu = processes
            .Select(item => (Process: item, Delta: CpuTime(item) - before[item.Id]))
            .Where(item => item.Delta > TimeSpan.Zero)
            .GroupBy(item => SafeName(item.Process))
            .Select(group => new
            {
                Name = group.Key,
                CpuPercent = Math.Round(group.Sum(item => item.Delta.TotalMilliseconds) / 10.0 / cores, 0)
            })
            .OrderByDescending(item => item.CpuPercent).Take(8).ToArray();
        var topMemory = processes
            .GroupBy(SafeName)
            .Select(group => new
            {
                Name = group.Key,
                MemoryMb = Math.Round(group.Sum(SafeWorkingSet) / 1024d / 1024, 0),
                Count = group.Count()
            })
            .OrderByDescending(item => item.MemoryMb).Take(8).ToArray();
        foreach (var process in processes) process.Dispose();

        var memory = new ActionNative.MEMORYSTATUSEX
        {
            dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ActionNative.MEMORYSTATUSEX>()
        };
        ActionNative.GlobalMemoryStatusEx(ref memory);
        var systemDrive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
        int? startup = null;
        try { startup = Wmi.Cimv2("SELECT Name FROM Win32_StartupCommand").Count; }
        catch { }

        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var busy = cpu > 85 ? "CPU is very busy" : memory.dwMemoryLoad > 90 ? "memory is nearly full"
            : systemDrive.AvailableFreeSpace < 5L * 1024 * 1024 * 1024 ? "the system disk is nearly full"
            : null;
        return EndpointActionResult.Ok(busy is null ? "CPU, memory and disk look normal."
                : $"The PC is under pressure: {busy}.",
            new
            {
                CpuPercent = cpu,
                MemoryLoadPercent = memory.dwMemoryLoad,
                MemoryTotalGb = Math.Round(memory.ullTotalPhys / 1024d / 1024 / 1024, 1),
                SystemDriveFreeGb = Math.Round(systemDrive.AvailableFreeSpace / 1024d / 1024 / 1024, 1),
                TopByCpu = topCpu,
                TopByMemory = topMemory,
                UptimeDays = Math.Round(uptime.TotalDays, 1),
                StartupPrograms = startup
            });
    }

    private static TimeSpan CpuTime(Process process)
    {
        try { return process.TotalProcessorTime; }
        catch { return TimeSpan.Zero; }
    }

    private static long SafeWorkingSet(Process process)
    {
        try { return process.WorkingSet64; }
        catch { return 0; }
    }

    private static string SafeName(Process process)
    {
        try { return process.ProcessName; }
        catch { return "unknown"; }
    }
}

[SupportedOSPlatform("windows")]
public sealed class EventsSummaryAction : IEndpointAction
{
    public string Id => "events.summary";

    private static readonly Dictionary<string, (string Log, string Filter)[]> Queries = new()
    {
        ["crashes"] = [("Application", "Provider[@Name='Application Error' or @Name='Application Hang'] and (EventID=1000 or EventID=1002)")],
        ["bluescreen"] = [("System", "(EventID=1001 or EventID=41 or EventID=6008) and Level<=2")],
        ["disk"] = [("System", "Provider[@Name='disk' or @Name='Ntfs' or @Name='volmgr' or @Name='storahci' or @Name='stornvme' or @Name='Microsoft-Windows-Ntfs'] and Level<=3")],
        ["network"] = [("System", "Provider[@Name='Microsoft-Windows-DNS-Client' or @Name='Microsoft-Windows-Dhcp-Client' or @Name='Tcpip' or @Name='NetBT' or @Name='Microsoft-Windows-NDIS'] and Level<=3")],
        ["grouppolicy"] = [("System", "Provider[@Name='Microsoft-Windows-GroupPolicy'] and Level<=2"),
                           ("Microsoft-Windows-GroupPolicy/Operational", "Level<=2")],
        ["printing"] = [("Microsoft-Windows-PrintService/Admin", "Level<=3")],
        ["updates"] = [("System", "Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] and Level<=2")],
        ["signin"] = [("System", "Provider[@Name='NETLOGON' or @Name='LsaSrv' or @Name='Microsoft-Windows-Security-Kerberos' or @Name='Microsoft-Windows-Time-Service'] and Level<=3")]
    };

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var area = invocation.Argument("area")!;
        var events = Queries[area]
            .SelectMany(query => WindowsEvents.Read(query.Log,
                $"*[System[{query.Filter} and {WindowsEvents.Since(7)}]]", 300))
            .ToArray();
        var groups = events
            .GroupBy(item => (item.Source, item.EventId))
            .Select(group => new
            {
                group.Key.Source,
                group.Key.EventId,
                Count = group.Count(),
                LastUtc = group.Max(item => item.WhenUtc),
                Sample = group.First().Message
            })
            .OrderByDescending(item => item.Count).Take(15).ToArray();
        return Task.FromResult(EndpointActionResult.Ok(events.Length == 0
                ? $"No {area} errors in the last 7 days."
                : $"{events.Length} {area} errors in the last 7 days.",
            new { Area = area, Total = events.Length, Groups = groups }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class ServicesStatusAction : IEndpointAction
{
    public string Id => "services.status";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var requested = invocation.Argument("service");
        var names = requested is null
            ? EndpointActionCatalog.ServiceChoices.Select(item => item.Value)
            : [requested];
        var services = names.Select(name =>
        {
            try
            {
                using var service = new ServiceController(name);
                return new { Name = name, State = service.Status.ToString(),
                    StartType = service.StartType.ToString() };
            }
            catch (InvalidOperationException)
            {
                return new { Name = name, State = "NotInstalled", StartType = "" };
            }
        }).ToArray();
        var stopped = services.Where(item => item.State != "Running"
            && item.StartType is "Automatic").Select(item => item.Name).ToArray();
        return Task.FromResult(EndpointActionResult.Ok(requested is not null
                ? $"{requested} is {services[0].State}."
                : stopped.Length == 0 ? "All automatic support services are running."
                : $"Stopped automatic services: {string.Join(", ", stopped)}.",
            new { Services = services }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class ServiceRestartAction : IEndpointAction
{
    public string Id => "service.restart";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var name = invocation.Argument("service")!;
        using var service = new ServiceController(name);
        if (service.StartType == ServiceStartMode.Disabled)
            return EndpointActionResult.Blocked($"{name} is disabled on this PC.");
        await ServiceTools.StopAsync(service, TimeSpan.FromSeconds(30), cancellationToken);
        service.Start();
        await ServiceTools.WaitAsync(service, ServiceControllerStatus.Running,
            TimeSpan.FromSeconds(30), cancellationToken);
        return EndpointActionResult.Done($"{name} was restarted.", new { State = "Running" });
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsUpdateStatusAction : IEndpointAction
{
    public string Id => "windows_update.status";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var events = WindowsEvents.Read("System",
            $"*[System[Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] and (EventID=19 or EventID=20) and {WindowsEvents.Since(30)}]]",
            100);
        var successes = events.Where(item => item.EventId == 19).ToArray();
        var failures = events.Where(item => item.EventId == 20).ToArray();
        DateTimeOffset? lastHotfix = null;
        try
        {
            lastHotfix = Wmi.Cimv2("SELECT InstalledOn FROM Win32_QuickFixEngineering")
                .Select(row => DateTime.TryParse(Wmi.String(row, "InstalledOn"),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal, out var date)
                    ? new DateTimeOffset(date).ToUniversalTime() : (DateTimeOffset?)null)
                .Max();
        }
        catch { }
        var restart = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") is { } key
            && Dispose(key);
        var last = successes.Select(item => (DateTimeOffset?)item.WhenUtc).Max() ?? lastHotfix;
        return Task.FromResult(EndpointActionResult.Ok(
            failures.Length > 0 ? $"{failures.Length} updates failed in the last 30 days."
            : last is null ? "No update installed in the last 30 days."
            : "Windows Update is working.",
            new
            {
                LastInstalledUtc = last,
                RecentSuccesses = successes.Length,
                RecentFailures = failures.Length,
                FailureSamples = failures.Take(3).Select(item => item.Message),
                RestartPending = restart
            }));
    }

    private static bool Dispose(RegistryKey key)
    {
        key.Dispose();
        return true;
    }
}

[SupportedOSPlatform("windows")]
public sealed class SecurityStatusAction : IEndpointAction
{
    public string Id => "security.status";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        object? defender = null;
        try
        {
            var row = Wmi.Query(@"root\Microsoft\Windows\Defender",
                "SELECT AntivirusEnabled, RealTimeProtectionEnabled, AntivirusSignatureAge, QuickScanAge FROM MSFT_MpComputerStatus").FirstOrDefault();
            if (row is not null)
                defender = new
                {
                    Enabled = Wmi.Bool(row, "AntivirusEnabled"),
                    RealTimeProtection = Wmi.Bool(row, "RealTimeProtectionEnabled"),
                    SignatureAgeDays = Wmi.Long(row, "AntivirusSignatureAge"),
                    DaysSinceQuickScan = Wmi.Long(row, "QuickScanAge")
                };
        }
        catch { }

        var firewall = new Dictionary<string, bool?>();
        foreach (var (name, key) in new[] { ("Domain", "DomainProfile"),
                     ("Private", "StandardProfile"), ("Public", "PublicProfile") })
        {
            using var policy = Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Policies\Microsoft\WindowsFirewall\{key}");
            using var local = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\{key}");
            var value = policy?.GetValue("EnableFirewall") ?? local?.GetValue("EnableFirewall");
            firewall[name] = value is int flag ? flag == 1 : null;
        }

        string? bitLocker = null;
        try
        {
            var drive = Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');
            var row = Wmi.Query(@"root\CIMV2\Security\MicrosoftVolumeEncryption",
                $"SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter='{drive}'")
                .FirstOrDefault();
            bitLocker = Wmi.Long(row ?? new Dictionary<string, object?>(), "ProtectionStatus") switch
            {
                0 => "Off",
                1 => "On",
                _ => "Unknown"
            };
        }
        catch { }

        return Task.FromResult(EndpointActionResult.Ok("Security status read.",
            new { Defender = defender, Firewall = firewall, BitLocker = bitLocker }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class DefenderQuickScanAction : IEndpointAction
{
    public string Id => "defender.quick_scan";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken) =>
        Task.FromResult(FixedTool.StartDefenderQuickScan()
            ? EndpointActionResult.Done("A quick virus scan was started in the background.")
            : EndpointActionResult.Blocked("Microsoft Defender is not available on this PC."));
}

[SupportedOSPlatform("windows")]
public sealed class DeviceProblemsAction : IEndpointAction
{
    public string Id => "devices.problems";

    internal static readonly Dictionary<long, string> Problems = new()
    {
        [1] = "Not configured correctly",
        [3] = "Driver may be corrupted or low memory",
        [10] = "Device cannot start",
        [12] = "Not enough free resources",
        [14] = "Restart needed",
        [18] = "Drivers need reinstalling",
        [19] = "Registry configuration is damaged",
        [22] = "Device is disabled",
        [24] = "Device not present or not working",
        [28] = "Driver not installed",
        [31] = "Windows cannot load the driver",
        [39] = "Driver is corrupted or missing",
        [43] = "Device reported a problem and was stopped",
        [52] = "Driver signature cannot be verified"
    };

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var devices = Wmi.Cimv2(
                "SELECT Name, PNPClass, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0")
            .Select(row => (Row: row, Code: Wmi.Long(row, "ConfigManagerErrorCode") ?? 0))
            .Where(item => item.Code != 45) // 45 = not connected right now
            .Select(item => new
            {
                Name = Wmi.String(item.Row, "Name"),
                Class = Wmi.String(item.Row, "PNPClass"),
                ProblemCode = item.Code,
                Problem = Problems.GetValueOrDefault(item.Code, "Device problem")
            })
            .Take(30).ToArray();
        return Task.FromResult(EndpointActionResult.Ok(devices.Length == 0
                ? "No device reports a problem."
                : $"{devices.Length} device(s) report a problem.",
            new { Devices = devices }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class DevicesRescanAction : IEndpointAction
{
    public string Id => "devices.rescan";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var status = ActionNative.CM_Locate_DevNode(out var root, null, 0);
        if (status == 0) status = ActionNative.CM_Reenumerate_DevNode(root, 0);
        return Task.FromResult(status == 0
            ? EndpointActionResult.Done("Windows scanned for hardware changes.")
            : EndpointActionResult.Failed($"The hardware scan failed (code {status})."));
    }
}

[SupportedOSPlatform("windows")]
public sealed class AppsFindAction : IEndpointAction
{
    public string Id => "apps.find";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var text = invocation.Argument("name")!;
        var matches = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<RegistryKey>
        {
            RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
            RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
        };
        var userRoot = invocation.UserSid is { } sid
            ? Registry.Users.OpenSubKey(sid) : null;
        if (userRoot is not null) roots.Add(userRoot);
        try
        {
            foreach (var root in roots)
            {
                using var uninstall = root.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var app = uninstall.OpenSubKey(name);
                    var display = app?.GetValue("DisplayName") as string;
                    if (display is null || app!.GetValue("SystemComponent") is 1
                        || !display.Contains(text, StringComparison.OrdinalIgnoreCase)
                        || !seen.Add(display)) continue;
                    matches.Add(new
                    {
                        Name = display,
                        Version = app.GetValue("DisplayVersion") as string,
                        Publisher = app.GetValue("Publisher") as string,
                        InstalledOn = app.GetValue("InstallDate") as string
                    });
                    if (matches.Count >= 20) break;
                }
            }
        }
        finally
        {
            foreach (var root in roots) root.Dispose();
        }
        return Task.FromResult(EndpointActionResult.Ok(matches.Count == 0
                ? $"No installed program matches '{text}'."
                : $"{matches.Count} installed program(s) match.", new { Matches = matches }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class DiskCleanupSafeAction : IEndpointAction
{
    public string Id => "disk.cleanup_safe";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
        var before = drive.AvailableFreeSpace;
        var cutoff = DateTime.UtcNow.AddDays(-2);
        long freed = 0;
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        freed += Clean(Path.Combine(windowsDir, "Temp"), cutoff, cancellationToken);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        freed += Clean(Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive"),
            DateTime.UtcNow, cancellationToken);
        freed += Clean(Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue"),
            DateTime.UtcNow, cancellationToken);
        if (UserProfilePath(invocation.UserSid) is { } profile)
            freed += Clean(Path.Combine(profile, "AppData", "Local", "Temp"), cutoff,
                cancellationToken);
        drive = new DriveInfo(drive.Name);
        return Task.FromResult(EndpointActionResult.Done(
            $"Freed {freed / 1024d / 1024:0} MB of temporary files.",
            new
            {
                FreedMb = Math.Round(freed / 1024d / 1024, 0),
                FreeGbBefore = Math.Round(before / 1024d / 1024 / 1024, 1),
                FreeGbAfter = Math.Round(drive.AvailableFreeSpace / 1024d / 1024 / 1024, 1)
            }));
    }

    internal static string? UserProfilePath(string? sid)
    {
        if (string.IsNullOrWhiteSpace(sid) || !sid.StartsWith("S-1-5-21-", StringComparison.Ordinal))
            return null;
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
        return key?.GetValue("ProfileImagePath") as string;
    }

    /// <summary>Deletes files older than the cutoff below one folder; never follows links.</summary>
    internal static long Clean(string folder, DateTime cutoffUtc,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(folder)) return 0;
        long freed = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
        };
        foreach (var file in Directory.EnumerateFiles(folder, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc > cutoffUtc) continue;
                var length = info.Length;
                info.Delete();
                freed += length;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return freed;
    }
}

[SupportedOSPlatform("windows")]
public sealed class TimeZoneSetStandardAction : IEndpointAction
{
    public string Id => "timezone.set_standard";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var standard = invocation.Setting(ActionSettings.StandardTimeZone);
        if (standard is null)
            return EndpointActionResult.Blocked("No standard time zone is set in the environment map.");
        return await TimeZones.SetAsync(standard, cancellationToken);
    }
}

[SupportedOSPlatform("windows")]
public sealed class TimeZoneRestoreAction : IEndpointAction
{
    public string Id => "timezone.restore";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken) =>
        TimeZones.SetAsync(invocation.Argument("zone")!, cancellationToken);
}

[SupportedOSPlatform("windows")]
internal static class TimeZones
{
    public static async Task<EndpointActionResult> SetAsync(string zoneId,
        CancellationToken cancellationToken)
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId); }
        catch (TimeZoneNotFoundException)
        {
            return EndpointActionResult.Blocked("That time zone does not exist on this PC.");
        }
        var previous = TimeZoneInfo.Local.Id;
        if (previous.Equals(zone.Id, StringComparison.OrdinalIgnoreCase))
            return EndpointActionResult.NotNeeded("The time zone is already correct.");
        var result = await FixedTool.RunAsync("tzutil.exe", ["/s", zone.Id],
            TimeSpan.FromSeconds(15), cancellationToken);
        TimeZoneInfo.ClearCachedData();
        return result.Succeeded
            ? EndpointActionResult.Done($"The time zone is now {zone.Id}.",
                new { TimeZone = zone.Id },
                new Dictionary<string, string> { ["zone"] = previous })
            : EndpointActionResult.Failed("Changing the time zone failed.");
    }
}

[SupportedOSPlatform("windows")]
public sealed class PowerStatusAction : IEndpointAction
{
    public string Id => "power.status";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var scheme = await FixedTool.RunAsync("powercfg.exe", ["/getactivescheme"],
            TimeSpan.FromSeconds(10), cancellationToken);
        var guid = System.Text.RegularExpressions.Regex.Match(scheme.Output,
            "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}").Value;
        var plan = guid.ToLowerInvariant() switch
        {
            "381b4222-f694-41f0-9685-ff5bb260df2e" => "Balanced",
            "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" => "High performance",
            "a1841308-3541-4fab-bc81-f71556f20b4a" => "Power saver",
            "e9a42b02-d5df-448d-aa00-03f14749eb61" => "Ultimate performance",
            "" => null,
            _ => "Custom"
        };
        object? battery = null;
        try
        {
            var row = Wmi.Cimv2("SELECT EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery")
                .FirstOrDefault();
            if (row is not null)
            {
                long? health = null;
                try
                {
                    var full = Wmi.Query(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity").FirstOrDefault();
                    var design = Wmi.Query(@"root\wmi", "SELECT DesignedCapacity FROM BatteryStaticData").FirstOrDefault();
                    var fullValue = full is null ? null : Wmi.Long(full, "FullChargedCapacity");
                    var designValue = design is null ? null : Wmi.Long(design, "DesignedCapacity");
                    if (fullValue > 0 && designValue > 0) health = fullValue * 100 / designValue;
                }
                catch { }
                battery = new
                {
                    ChargePercent = Wmi.Long(row, "EstimatedChargeRemaining"),
                    OnAcPower = Wmi.Long(row, "BatteryStatus") is 2 or 6 or 7 or 8 or 9,
                    HealthPercent = health
                };
            }
        }
        catch { }
        return EndpointActionResult.Ok(battery is null ? $"Power plan: {plan ?? "unknown"}."
                : $"Power plan: {plan ?? "unknown"}; this PC has a battery.",
            new { PowerPlan = plan, Battery = battery });
    }
}

[SupportedOSPlatform("windows")]
public sealed class RebootScheduleAction : IEndpointAction
{
    public string Id => "reboot.schedule";

    // Shown by Windows in the restart warning; fixed text, never from the agent.
    private const string Warning =
        "کامپیوتر برای رفع مشکل شما به‌زودی دوباره راه‌اندازی می‌شود. لطفاً کارهای باز را ذخیره کنید.";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var minutes = invocation.IntArgument("minutes", 15);
        var seconds = (minutes * 60).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var result = await FixedTool.RunAsync("shutdown.exe",
            ["/r", "/t", seconds, "/d", "p:0:0", "/c", Warning], TimeSpan.FromSeconds(15),
            cancellationToken);
        if (result.ExitCode == 1190)
            return EndpointActionResult.NotNeeded("A restart is already scheduled.");
        return result.Succeeded
            ? EndpointActionResult.Done($"The PC will restart in {minutes} minutes.",
                new { RestartAtUtc = DateTimeOffset.UtcNow.AddMinutes(minutes) })
            : EndpointActionResult.Failed("Scheduling the restart failed.");
    }
}

[SupportedOSPlatform("windows")]
public sealed class RebootCancelAction : IEndpointAction
{
    public string Id => "reboot.cancel";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var result = await FixedTool.RunAsync("shutdown.exe", ["/a"],
            TimeSpan.FromSeconds(15), cancellationToken);
        return result.ExitCode switch
        {
            0 => EndpointActionResult.Done("The scheduled restart was cancelled."),
            1116 => EndpointActionResult.NotNeeded("No restart was scheduled."),
            _ => EndpointActionResult.Failed("Cancelling the restart failed.")
        };
    }
}
