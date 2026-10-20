using EndpointAgents.Platform.Windows.Actions.Infrastructure;
using EndpointAgents.Shared.Actions;
using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text.RegularExpressions;

namespace EndpointAgents.Platform.Windows.Actions.SystemSide;

[SupportedOSPlatform("windows")]
public sealed class WindowsUpdateResetAction : IEndpointAction
{
    private static readonly string[] Services = ["wuauserv", "bits", "cryptsvc"];

    public string Id => "windows_update.reset_components";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        foreach (var name in Services)
        {
            using var service = new ServiceController(name);
            await ServiceTools.StopAsync(service, TimeSpan.FromSeconds(40), cancellationToken);
        }
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var moved = new List<string>();
        try
        {
            foreach (var folder in new[]
                     {
                         Path.Combine(windows, "SoftwareDistribution"),
                         Path.Combine(Environment.SystemDirectory, "catroot2")
                     })
            {
                if (!Directory.Exists(folder)) continue;
                var aside = folder + ".old";
                if (Directory.Exists(aside)) Directory.Delete(aside, recursive: true);
                Directory.Move(folder, aside);
                moved.Add(Path.GetFileName(folder));
            }
        }
        finally
        {
            foreach (var name in Services.Reverse())
                await ServiceTools.EnsureRunningAsync(name, TimeSpan.FromSeconds(40), cancellationToken);
        }
        return EndpointActionResult.Done(moved.Count == 0
                ? "The update services were restarted; there was no cache to set aside."
                : "Windows Update was reset: its download cache was set aside and the services restarted.",
            new { SetAside = moved });
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsUpdateScanNowAction : IEndpointAction
{
    public string Id => "windows_update.scan_now";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken) =>
        Task.FromResult(FixedTool.StartDetached("usoclient.exe", ["StartScan"])
            ? EndpointActionResult.Done("Windows Update was asked to look for updates.")
            : EndpointActionResult.Failed("The Windows Update client was not found."));
}

[SupportedOSPlatform("windows")]
public sealed class DefenderUpdateSignaturesAction : IEndpointAction
{
    public string Id => "defender.update_signatures";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(FixedTool.DefenderTool))
            return EndpointActionResult.Blocked("Microsoft Defender is not installed on this PC.");
        var result = await FixedTool.RunDefenderAsync(["-SignatureUpdate"],
            TimeSpan.FromSeconds(280), cancellationToken);
        return result.Succeeded
            ? EndpointActionResult.Done("The antivirus definitions are up to date.")
            : EndpointActionResult.Failed(result.TimedOut
                    ? "The definition download did not finish in time."
                    : "Defender could not download its definitions.",
                new { result.ExitCode });
    }
}

[SupportedOSPlatform("windows")]
public sealed class DefenderThreatsAction : IEndpointAction
{
    private const string Scope = @"root\Microsoft\Windows\Defender";

    public string Id => "defender.threats";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> detections, threats;
        try
        {
            detections = Wmi.Query(Scope,
                "SELECT ThreatID, InitialDetectionTime, ThreatStatusID FROM MSFT_MpThreatDetection");
            threats = Wmi.Query(Scope, "SELECT ThreatID, ThreatName, SeverityID FROM MSFT_MpThreat");
        }
        catch (System.Management.ManagementException)
        {
            return Task.FromResult(EndpointActionResult.Ok(
                "Microsoft Defender is not the antivirus on this PC.", new { Count = 0 }));
        }
        var names = threats
            .Where(row => Wmi.Long(row, "ThreatID") is not null)
            .GroupBy(row => Wmi.Long(row, "ThreatID")!.Value)
            .ToDictionary(group => group.Key, group => group.First());
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        var found = detections
            .Select(row => (Row: row, When: Wmi.Date(row, "InitialDetectionTime")))
            .Where(item => item.When >= since)
            .OrderByDescending(item => item.When)
            .Take(30)
            .Select(item =>
            {
                var id = Wmi.Long(item.Row, "ThreatID") ?? 0;
                names.TryGetValue(id, out var threat);
                var status = Wmi.Long(item.Row, "ThreatStatusID");
                return new
                {
                    Name = threat is null ? "Unknown" : Wmi.String(threat, "ThreatName") ?? "Unknown",
                    Severity = (threat is null ? null : Wmi.Long(threat, "SeverityID")) switch
                    {
                        1 => "Low", 2 => "Moderate", 4 => "High", 5 => "Severe", _ => "Unknown"
                    },
                    DetectedUtc = item.When,
                    Action = status switch
                    {
                        1 => "Detected", 2 => "Cleaned", 3 => "Quarantined", 4 => "Removed",
                        5 => "Allowed", 6 => "Blocked", 102 => "QuarantineFailed",
                        103 => "RemoveFailed", 104 => "AllowFailed", 105 => "Abandoned",
                        107 => "BlockFailed", _ => "Unknown"
                    },
                    Resolved = status is 2 or 3 or 4 or 6
                };
            })
            .ToArray();
        var open = found.Count(item => !item.Resolved);
        return Task.FromResult(EndpointActionResult.Ok(found.Length == 0
                ? "Defender found no threats in the last 30 days."
                : $"Defender found {found.Length} threat(s) in the last 30 days; {open} not resolved.",
            new { Threats = found, Count = found.Length }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class BitLockerStatusAction : IEndpointAction
{
    public string Id => "bitlocker.status";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows;
        try
        {
            // Only status properties are selected; no key protector method is ever called.
            rows = Wmi.Query(@"root\CIMV2\Security\MicrosoftVolumeEncryption",
                "SELECT * FROM Win32_EncryptableVolume");
        }
        catch (System.Management.ManagementException)
        {
            return Task.FromResult(EndpointActionResult.Ok(
                "BitLocker is not available on this edition of Windows.", new { Volumes = Array.Empty<object>() }));
        }
        var volumes = rows
            .Where(row => Wmi.Long(row, "VolumeType") is null or 0 or 1)
            .Where(row => !string.IsNullOrEmpty(Wmi.String(row, "DriveLetter")))
            .Select(row => new
            {
                Drive = Wmi.String(row, "DriveLetter"),
                Protection = Wmi.Long(row, "ProtectionStatus") switch { 0 => "Off", 1 => "On", _ => "Unknown" },
                Encrypted = Wmi.Long(row, "ConversionStatus") switch
                {
                    1 or 2 => (bool?)true,
                    0 or 3 => false,
                    _ => null
                },
                Method = Wmi.Long(row, "EncryptionMethod") switch
                {
                    0 => "None", 1 or 3 => "AES-128", 2 or 4 => "AES-256", 5 => "Hardware",
                    6 => "XTS-AES-128", 7 => "XTS-AES-256", _ => "Unknown"
                }
            })
            .OrderBy(item => item.Drive, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var on = volumes.Count(item => item.Protection == "On");
        return Task.FromResult(EndpointActionResult.Ok(volumes.Length == 0
                ? "No fixed drives report a BitLocker state."
                : $"BitLocker protection is on for {on} of {volumes.Length} fixed drive(s).",
            new { Volumes = volumes }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class DiskHealthAction : IEndpointAction
{
    public string Id => "disk.health";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var disks = Wmi.Query(@"root\Microsoft\Windows\Storage",
                "SELECT FriendlyName, MediaType, Size, HealthStatus, OperationalStatus FROM MSFT_PhysicalDisk")
            .Select(row => new
            {
                Name = Wmi.String(row, "FriendlyName") ?? "Disk",
                MediaType = Wmi.Long(row, "MediaType") switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unknown" },
                SizeGb = Math.Round((Wmi.Long(row, "Size") ?? 0) / 1e9, 0),
                Health = Wmi.Long(row, "HealthStatus") switch
                {
                    0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown"
                },
                Operational = row.TryGetValue("OperationalStatus", out var status) && status is ushort[] codes
                    ? codes.All(code => code == 2) : (bool?)null
            })
            .ToArray();
        bool predictsFailure;
        try
        {
            predictsFailure = Wmi.Query(@"root\wmi",
                    "SELECT PredictFailure FROM MSStorageDriver_FailurePredictStatus")
                .Any(row => Wmi.Bool(row, "PredictFailure"));
        }
        catch (System.Management.ManagementException)
        {
            predictsFailure = false;
        }
        var anyWarning = predictsFailure
                         || disks.Any(disk => disk.Health is "Warning" or "Unhealthy" || disk.Operational == false);
        return Task.FromResult(EndpointActionResult.Ok(anyWarning
                ? "A disk reports a health warning; back up the user's files and escalate."
                : $"{disks.Length} disk(s) report healthy.",
            new { Disks = disks, PredictsFailure = predictsFailure, AnyWarning = anyWarning }));
    }
}

[SupportedOSPlatform("windows")]
internal static class SystemRepair
{
    public static bool Running(string name)
    {
        var processes = Process.GetProcessesByName(name);
        var running = processes.Length > 0;
        foreach (var process in processes) process.Dispose();
        return running;
    }

    public static bool AnyRunning => Running("sfc") || Running("dism") || Running("DismHost");

    /// <summary>Reads at most the last 4 MB of a Windows log that may still be open for writing.</summary>
    public static string[] Tail(string path)
    {
        if (!File.Exists(path)) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        const long max = 4 * 1024 * 1024;
        if (stream.Length > max) stream.Seek(-max, SeekOrigin.End);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n');
    }

    /// <summary>The local time at the start of a CBS or DISM log line ("2026-10-20 10:15:22, ...").</summary>
    public static DateTimeOffset? LineTime(string line) =>
        line.Length >= 19 && DateTime.TryParseExact(line[..19], "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var when)
            ? new DateTimeOffset(when).ToUniversalTime() : null;

    public static object FileCheck()
    {
        var lines = Tail(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Logs", "CBS", "CBS.log"));
        var start = Array.FindLastIndex(lines, line =>
            line.Contains("[SR] Beginning Verify and Repair transaction", StringComparison.Ordinal));
        var running = Running("sfc");
        if (start < 0)
            return new { Running = running, LastResult = "Unknown", FinishedUtc = (DateTimeOffset?)null };
        var run = lines[start..];
        var finished = Array.FindLastIndex(run, line =>
            line.Contains("[SR] Verify complete", StringComparison.Ordinal)
            || line.Contains("[SR] Repair complete", StringComparison.Ordinal));
        string result;
        if (finished < 0) result = running ? "Unknown" : "Failed";
        else if (run.Any(line => line.Contains("[SR] Cannot repair member file", StringComparison.Ordinal)))
            result = "CouldNotRepair";
        else if (run.Any(line => line.Contains("[SR] Repairing corrupted file", StringComparison.Ordinal)
                                 || line.Contains("[SR] Repaired file", StringComparison.Ordinal)))
            result = "Repaired";
        else result = "NoProblems";
        return new
        {
            Running = running,
            LastResult = result,
            FinishedUtc = finished < 0 ? null : LineTime(run[finished])
        };
    }

    public static object ImageRepair()
    {
        var lines = Tail(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Logs", "DISM", "dism.log"));
        var start = Array.FindLastIndex(lines, line =>
            line.Contains("RestoreHealth", StringComparison.OrdinalIgnoreCase)
            && line.Contains("Command line", StringComparison.OrdinalIgnoreCase));
        var running = Running("dism") || Running("DismHost");
        if (start < 0)
            return new { Running = running, LastResult = "Unknown", FinishedUtc = (DateTimeOffset?)null };
        var run = lines[start..];
        var finished = Array.FindLastIndex(run, line =>
            line.Contains("Image session has been closed", StringComparison.OrdinalIgnoreCase));
        string result;
        if (finished < 0) result = running ? "Unknown" : "Failed";
        else if (run.Any(line => line.Contains("0x800f081f", StringComparison.OrdinalIgnoreCase)
                                 || line.Contains("source files could not be found", StringComparison.OrdinalIgnoreCase)))
            result = "CouldNotRepair";
        else if (run.Any(line => Regex.IsMatch(line, @"Error\s+DISM.*hr:0x8", RegexOptions.IgnoreCase)))
            result = "Failed";
        else if (run.Any(line => line.Contains("restore operation completed successfully", StringComparison.OrdinalIgnoreCase)
                                 || line.Contains("Repair succeeded", StringComparison.OrdinalIgnoreCase)))
            result = "Repaired";
        else result = "Unknown";
        return new
        {
            Running = running,
            LastResult = result,
            FinishedUtc = finished < 0 ? null : LineTime(run[finished])
        };
    }
}

[SupportedOSPlatform("windows")]
public sealed class SystemFileCheckAction : IEndpointAction
{
    public string Id => "system.file_check";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (SystemRepair.AnyRunning)
            return Task.FromResult(EndpointActionResult.NotNeeded(
                "A Windows file check or repair is already running."));
        return Task.FromResult(FixedTool.StartDetached("sfc.exe", ["/scannow"])
            ? EndpointActionResult.Done("The Windows file check started. It takes 10 to 20 minutes.")
            : EndpointActionResult.Failed("The system file checker was not found."));
    }
}

[SupportedOSPlatform("windows")]
public sealed class SystemImageRepairAction : IEndpointAction
{
    public string Id => "system.image_repair";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (SystemRepair.AnyRunning)
            return Task.FromResult(EndpointActionResult.NotNeeded(
                "A Windows file check or repair is already running."));
        return Task.FromResult(FixedTool.StartDetached("dism.exe",
                ["/Online", "/Cleanup-Image", "/RestoreHealth"])
            ? EndpointActionResult.Done("The Windows image repair started. It takes 10 to 30 minutes.")
            : EndpointActionResult.Failed("DISM was not found."));
    }
}

[SupportedOSPlatform("windows")]
public sealed class SystemRepairStatusAction : IEndpointAction
{
    public string Id => "system.repair_status";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var fileCheck = SystemRepair.FileCheck();
        var imageRepair = SystemRepair.ImageRepair();
        return Task.FromResult(EndpointActionResult.Ok(SystemRepair.AnyRunning
                ? "A Windows file check or repair is still running."
                : "Read the last result of the Windows file check and image repair.",
            new { FileCheck = fileCheck, ImageRepair = imageRepair }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class BatteryReportAction : IEndpointAction
{
    public string Id => "battery.report";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (Wmi.Cimv2("SELECT DeviceID FROM Win32_Battery").Count == 0)
            return Task.FromResult(EndpointActionResult.Ok("This PC has no battery.",
                new { HasBattery = false }));
        long? Read(string query, string property)
        {
            try
            {
                var row = Wmi.Query(@"root\wmi", query).FirstOrDefault();
                return row is null ? null : Wmi.Long(row, property);
            }
            catch (System.Management.ManagementException) { return null; }
        }
        var design = Read("SELECT DesignedCapacity FROM BatteryStaticData", "DesignedCapacity");
        var full = Read("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", "FullChargedCapacity");
        var cycles = Read("SELECT CycleCount FROM BatteryCycleCount", "CycleCount");
        bool? charging = null, pluggedIn = null;
        try
        {
            var status = Wmi.Query(@"root\wmi", "SELECT Charging, PowerOnline FROM BatteryStatus").FirstOrDefault();
            if (status is not null)
            {
                charging = Wmi.Bool(status, "Charging");
                pluggedIn = Wmi.Bool(status, "PowerOnline");
            }
        }
        catch (System.Management.ManagementException) { }
        long? wear = design > 0 && full > 0 ? Math.Max(0L, 100 - full!.Value * 100 / design!.Value) : null;
        return Task.FromResult(EndpointActionResult.Ok(wear is null
                ? "The battery does not report its capacity."
                : $"The battery holds {100 - wear}% of its original charge.",
            new
            {
                HasBattery = true,
                DesignMwh = design,
                FullChargeMwh = full,
                WearPercent = wear,
                CycleCount = cycles is > 0 ? cycles : null,
                Charging = charging,
                PluggedIn = pluggedIn
            }));
    }
}

[SupportedOSPlatform("windows")]
internal static class PowerPlans
{
    public static readonly IReadOnlyDictionary<string, string> Standard =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["balanced"] = "381b4222-f694-41f0-9685-ff5bb260df2e",
            ["high"] = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
            ["saver"] = "a1841308-3541-4fab-bc81-f71556f20b4a"
        };

    public static async Task<string?> ActiveAsync(CancellationToken cancellationToken)
    {
        var result = await FixedTool.RunAsync("powercfg.exe", ["/getactivescheme"],
            TimeSpan.FromSeconds(10), cancellationToken);
        var match = Regex.Match(result.Output,
            "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }

    public static async Task<bool> SetAsync(string guid, CancellationToken cancellationToken) =>
        (await FixedTool.RunAsync("powercfg.exe", ["/setactive", guid],
            TimeSpan.FromSeconds(15), cancellationToken)).Succeeded;
}

[SupportedOSPlatform("windows")]
public sealed class PowerSetPlanStandardAction : IEndpointAction
{
    public string Id => "power.set_plan_standard";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var standard = invocation.Setting(ActionSettings.PowerPlan);
        if (standard is null || !PowerPlans.Standard.TryGetValue(standard, out var wanted))
            return EndpointActionResult.Blocked("No standard power plan is set in the environment map.");
        var current = await PowerPlans.ActiveAsync(cancellationToken);
        if (current == wanted)
            return EndpointActionResult.NotNeeded("The PC already uses the standard power plan.");
        if (!await PowerPlans.SetAsync(wanted, cancellationToken))
            return EndpointActionResult.Failed("This PC does not offer the standard power plan.");
        return EndpointActionResult.Done("The PC now uses the standard power plan.",
            new { Plan = standard },
            current is null ? null : new Dictionary<string, string> { ["plan"] = current });
    }
}

[SupportedOSPlatform("windows")]
public sealed class PowerRestorePlanAction : IEndpointAction
{
    public string Id => "power.restore_plan";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(invocation.Argument("plan"), out var plan))
            return EndpointActionResult.Failed("The saved power plan is not valid.");
        return await PowerPlans.SetAsync(plan.ToString("D"), cancellationToken)
            ? EndpointActionResult.Done("The previous power plan is active again.")
            : EndpointActionResult.Failed("The previous power plan no longer exists on this PC.");
    }
}

[SupportedOSPlatform("windows")]
public sealed class OfficeQuickRepairAction : IEndpointAction
{
    public string Id => "office.quick_repair";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var configuration = root.OpenSubKey(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration");
        var platform = configuration?.GetValue("Platform") as string;
        var culture = configuration?.GetValue("ClientCulture") as string;
        if (platform is not ("x86" or "x64") || culture is null
            || !Regex.IsMatch(culture, "^[a-zA-Z]{2,3}(-[a-zA-Z]{2,4})?$")
            || !File.Exists(FixedTool.OfficeClickToRun))
            return EndpointActionResult.Blocked("Office on this PC is not a Click-to-Run installation.");
        var result = await FixedTool.RunOfficeClickToRunAsync(
            ["scenario=Repair", $"platform={platform}", $"culture={culture}",
             "forceappshutdown=True", "RepairType=QuickRepair", "DisplayLevel=False"],
            TimeSpan.FromSeconds(580), cancellationToken);
        return result.Succeeded
            ? EndpointActionResult.Done("Office Quick Repair finished. The user can open Office again.")
            : EndpointActionResult.Failed(result.TimedOut
                    ? "Office Quick Repair did not finish in time."
                    : "Office Quick Repair did not succeed.",
                new { result.ExitCode });
    }
}
