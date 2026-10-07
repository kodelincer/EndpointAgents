using EndpointAgents.Shared.Support;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.SystemInfo;

/// <summary>Make, model, BIOS, processor and memory (no serial numbers or asset tags).</summary>
[SupportedOSPlatform("windows")]
public sealed class HardwareInfoSection : ISystemInfoSection
{
    public string Name => SystemInfoContracts.Hardware;

    public Task<object?> CollectAsync(SystemInfoWarnings warnings,
        CancellationToken cancellationToken)
    {
        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        using var cpu = Registry.LocalMachine.OpenSubKey(
            @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");

        object? result = new
        {
            Manufacturer = bios?.GetValue("SystemManufacturer")?.ToString()?.Trim(),
            Model = bios?.GetValue("SystemProductName")?.ToString()?.Trim(),
            BiosVersion = bios?.GetValue("BIOSVersion")?.ToString()?.Trim(),
            BiosReleaseDate = bios?.GetValue("BIOSReleaseDate")?.ToString()?.Trim(),
            Processor = cpu?.GetValue("ProcessorNameString")?.ToString()?.Trim(),
            LogicalProcessors = Environment.ProcessorCount,
            Memory = warnings.Try("hardware.memory", ReadMemory)
        };
        return Task.FromResult(result);
    }

    private static object ReadMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status))
            throw new InvalidOperationException("GlobalMemoryStatusEx failed.");
        return new
        {
            TotalGb = Math.Round(status.TotalPhysical / 1024d / 1024 / 1024, 1),
            AvailableGb = Math.Round(status.AvailablePhysical / 1024d / 1024 / 1024, 1),
            LoadPercent = status.MemoryLoad
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}
