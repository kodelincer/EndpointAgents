using EndpointAgents.Platform.Windows.Actions.Infrastructure;
using EndpointAgents.Shared.Actions;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Actions.SystemSide;

/// <summary>Devices from Windows' device list, with hardware IDs and current driver.</summary>
[SupportedOSPlatform("windows")]
internal static class Devices
{
    /// <summary>Peripheral classes listed with scope "all".</summary>
    public static readonly string[] PeripheralClasses =
        ["Printer", "Image", "Camera", "Media", "Bluetooth", "Net", "SmartCardReader",
         "SmartCard", "Ports", "Biometric", "USB", "WPD", "Modem", "MEDIA"];

    /// <summary>Classes that are never turned off or on: losing them stops the PC or the user's input.</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "DiskDrive", "Display", "Keyboard", "Mouse", "HIDClass", "System", "Processor",
        "Volume", "VolumeSnapshot", "SCSIAdapter", "HDC", "Computer", "SoftwareDevice",
        "Firmware", "SecurityDevices", "Net", "USB", "Battery", "Monitor"
    };

    public sealed record Device(string InstanceId, string Name, string? Class,
        IReadOnlyList<string> HardwareIds, long ProblemCode);

    public static IReadOnlyList<Device> Read(bool onlyProblems)
    {
        var where = onlyProblems
            ? "ConfigManagerErrorCode <> 0 AND ConfigManagerErrorCode <> 45"
            : string.Join(" OR ", PeripheralClasses.Select(item => $"PNPClass = '{item}'"))
              + " OR (ConfigManagerErrorCode <> 0 AND ConfigManagerErrorCode <> 45)";
        return Wmi.Cimv2(
                $"SELECT Name, PNPClass, DeviceID, HardwareID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE {where}",
                400)
            .Select(ToDevice).OfType<Device>().ToArray();
    }

    public static Device? Find(string instanceId) => Wmi.Cimv2(
            $"SELECT Name, PNPClass, DeviceID, HardwareID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE DeviceID = '{Wmi.Literal(instanceId)}'")
        .Select(ToDevice).OfType<Device>().FirstOrDefault();

    public static bool IsProtected(Device device) =>
        device.Class is null || Protected.Contains(device.Class);

    public static string Problem(long code) =>
        code == 0 ? "None" : DeviceProblemsAction.Problems.GetValueOrDefault(code, "Device problem");

    private static Device? ToDevice(IReadOnlyDictionary<string, object?> row) =>
        Wmi.String(row, "DeviceID") is { } id
            ? new Device(id, Wmi.String(row, "Name") ?? id, Wmi.String(row, "PNPClass"),
                row.TryGetValue("HardwareID", out var ids) && ids is string[] list
                    ? list.Take(4).ToArray() : [],
                Wmi.Long(row, "ConfigManagerErrorCode") ?? 0)
            : null;

    public static int Locate(string instanceId, out uint node) =>
        ActionNative.CM_Locate_DevNode(out node, instanceId, ActionNative.CM_LOCATE_DEVNODE_NORMAL);
}

[SupportedOSPlatform("windows")]
public sealed class DriversInventoryAction : IEndpointAction
{
    public string Id => "drivers.inventory";
    private const int MaxDevices = 60;

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var onlyProblems = invocation.Argument("scope") != "all";
        var devices = Devices.Read(onlyProblems);
        var drivers = Wmi.Cimv2(
                "SELECT DeviceID, DriverProviderName, DriverVersion, DriverDate FROM Win32_PnPSignedDriver",
                2000)
            .Where(row => Wmi.String(row, "DeviceID") is not null)
            .GroupBy(row => Wmi.String(row, "DeviceID")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var listed = devices
            .OrderByDescending(item => item.ProblemCode != 0)
            .ThenBy(item => item.Class, StringComparer.OrdinalIgnoreCase)
            .Take(MaxDevices)
            .Select(item =>
            {
                drivers.TryGetValue(item.InstanceId, out var driver);
                return new
                {
                    item.Name,
                    item.Class,
                    item.InstanceId,
                    item.HardwareIds,
                    item.ProblemCode,
                    Problem = Devices.Problem(item.ProblemCode),
                    DriverProvider = driver is null ? null : Wmi.String(driver, "DriverProviderName"),
                    DriverVersion = driver is null ? null : Wmi.String(driver, "DriverVersion"),
                    DriverDate = driver is null ? null : Wmi.Date(driver, "DriverDate")?.ToString("yyyy-MM-dd")
                };
            }).ToArray();
        var problems = devices.Count(item => item.ProblemCode != 0);
        return Task.FromResult(EndpointActionResult.Ok(
            onlyProblems
                ? problems == 0 ? "No device reports a problem." : $"{problems} device(s) report a problem."
                : $"{devices.Count} device(s) listed; {problems} with a problem.",
            new { Devices = listed }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class DeviceEnableAction : IEndpointAction
{
    public string Id => "device.enable";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var device = Devices.Find(invocation.Argument("device")!);
        if (device is null)
            return Task.FromResult(EndpointActionResult.Failed("That device is not on this PC. Use drivers.inventory."));
        if (Devices.IsProtected(device))
            return Task.FromResult(EndpointActionResult.Blocked($"{device.Name} is a protected device class; the helpdesk must handle it."));
        if (device.ProblemCode != 22)
            return Task.FromResult(EndpointActionResult.NotNeeded($"{device.Name} is not turned off."));
        var status = Devices.Locate(device.InstanceId, out var node);
        if (status == 0) status = ActionNative.CM_Enable_DevNode(node, 0);
        return Task.FromResult(status == 0
            ? EndpointActionResult.Done($"{device.Name} was turned on.")
            : EndpointActionResult.Failed($"Windows could not turn {device.Name} on (code {status})."));
    }
}

[SupportedOSPlatform("windows")]
public sealed class DeviceRestartAction : IEndpointAction
{
    public string Id => "device.restart";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var device = Devices.Find(invocation.Argument("device")!);
        if (device is null)
            return EndpointActionResult.Failed("That device is not on this PC. Use drivers.inventory.");
        if (Devices.IsProtected(device))
            return EndpointActionResult.Blocked($"{device.Name} is a protected device class; the helpdesk must handle it.");
        var status = Devices.Locate(device.InstanceId, out var node);
        if (status != 0)
            return EndpointActionResult.Failed($"Windows could not find {device.Name} (code {status}).");
        if (device.ProblemCode != 22)
        {
            status = ActionNative.CM_Disable_DevNode(node, ActionNative.CM_DISABLE_UI_NOT_OK);
            if (status != 0)
                return EndpointActionResult.Blocked(
                    $"{device.Name} cannot be stopped while Windows runs (code {status}); a restart of the PC does the same.");
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        status = ActionNative.CM_Enable_DevNode(node, 0);
        if (status != 0)
            return EndpointActionResult.Failed(
                $"{device.Name} was turned off but did not come back on (code {status}); restart the PC or ask the helpdesk.");
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        return EndpointActionResult.Done($"{device.Name} was restarted.");
    }
}
