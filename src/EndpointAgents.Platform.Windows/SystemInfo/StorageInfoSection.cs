using EndpointAgents.Shared.Support;

namespace EndpointAgents.Platform.Windows.SystemInfo;

/// <summary>Size and free space of each local fixed drive, flagging nearly full ones.</summary>
public sealed class StorageInfoSection : ISystemInfoSection
{
    public string Name => SystemInfoContracts.Storage;

    public Task<object?> CollectAsync(SystemInfoWarnings warnings,
        CancellationToken cancellationToken)
    {
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
        object? result = DriveInfo.GetDrives()
            .Where(drive => drive.DriveType == DriveType.Fixed)
            .Select(drive => warnings.Try($"storage.{drive.Name}", () =>
                Describe(drive.Name.TrimEnd('\\'), drive.DriveFormat, drive.TotalSize,
                    drive.AvailableFreeSpace,
                    string.Equals(drive.Name, systemDrive, StringComparison.OrdinalIgnoreCase))))
            .Where(drive => drive is not null)
            .ToArray();
        return Task.FromResult(result);
    }

    internal static object Describe(string drive, string format, long totalBytes,
        long freeBytes, bool isSystemDrive)
    {
        var freePercent = totalBytes > 0 ? Math.Round(freeBytes * 100d / totalBytes, 1) : 0;
        var freeGb = Math.Round(freeBytes / 1024d / 1024 / 1024, 1);
        return new
        {
            Drive = drive,
            Format = format,
            IsSystemDrive = isSystemDrive,
            TotalGb = Math.Round(totalBytes / 1024d / 1024 / 1024, 1),
            FreeGb = freeGb,
            FreePercent = freePercent,
            LowSpace = freePercent < 10 || freeGb < 5
        };
    }
}
