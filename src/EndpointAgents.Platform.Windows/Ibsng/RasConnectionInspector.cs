using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Ibsng;

/// <summary>One Windows Remote Access Service connection and its current state.</summary>
public sealed record RasConnection(
    string EntryName,
    string DeviceType,
    string DeviceName,
    string State = "Connected",
    int ErrorCode = 0,
    string? ErrorMessage = null);

/// <summary>Reads active and transitioning VPN connections directly from Windows RAS.</summary>
[SupportedOSPlatform("windows")]
public sealed class RasConnectionInspector
{
    private const int ErrorSuccess = 0;
    private const int ErrorBufferTooSmall = 603;
    private const int RasMaxEntryName = 256;
    private const int RasMaxDeviceType = 16;
    private const int RasMaxDeviceName = 128;

    /// <summary>Returns the RAS connections visible to the endpoint service.</summary>
    public IReadOnlyList<RasConnection> GetActiveConnections()
    {
        var bufferBytes = 0;
        var result = NativeMethods.RasEnumConnections(IntPtr.Zero, ref bufferBytes,
            out var connectionCount);
        if (result == ErrorSuccess && connectionCount == 0)
            return Array.Empty<RasConnection>();
        if (result != ErrorBufferTooSmall || bufferBytes <= 0 || connectionCount <= 0)
            throw new Win32Exception(result,
                $"RasEnumConnections size query failed with error {result}.");

        var structureSize = bufferBytes / connectionCount;
        var handleOffset = IntPtr.Size == 8 ? 8 : 4;
        var entryNameOffset = IntPtr.Size == 8 ? 16 : 8;
        var deviceTypeOffset = entryNameOffset + ((RasMaxEntryName + 1) * sizeof(char));
        var deviceNameOffset = deviceTypeOffset + ((RasMaxDeviceType + 1) * sizeof(char));
        if (structureSize < deviceNameOffset + ((RasMaxDeviceName + 1) * sizeof(char)))
            throw new InvalidDataException("Windows returned an unsupported RASCONN layout.");

        var buffer = Marshal.AllocHGlobal(bufferBytes);
        try
        {
            // RAS requires dwSize in the first structure before it fills the array.
            Marshal.Copy(new byte[bufferBytes], 0, buffer, bufferBytes);
            Marshal.WriteInt32(buffer, structureSize);
            result = NativeMethods.RasEnumConnections(buffer, ref bufferBytes,
                out connectionCount);
            if (result != ErrorSuccess)
                throw new Win32Exception(result,
                    $"RasEnumConnections failed with error {result}.");

            var connections = new List<RasConnection>(connectionCount);
            for (var index = 0; index < connectionCount; index++)
            {
                var item = IntPtr.Add(buffer, index * structureSize);
                var handle = Marshal.ReadIntPtr(item, handleOffset);
                var status = GetStatus(handle);
                connections.Add(new RasConnection(
                    ReadFixedString(item, entryNameOffset, RasMaxEntryName + 1),
                    ReadFixedString(item, deviceTypeOffset, RasMaxDeviceType + 1),
                    ReadFixedString(item, deviceNameOffset, RasMaxDeviceName + 1),
                    status.State, status.ErrorCode, status.ErrorMessage));
            }
            return connections;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static RasStatus GetStatus(IntPtr connection)
    {
        var status = new RasConnectionStatus
        {
            Size = Marshal.SizeOf<RasConnectionStatus>(),
            DeviceType = string.Empty,
            DeviceName = string.Empty,
            PhoneNumber = string.Empty
        };
        var result = NativeMethods.RasGetConnectStatus(connection, ref status);
        if (result != ErrorSuccess)
            return new RasStatus("Connected", result, new Win32Exception(result).Message);
        return new RasStatus(FriendlyState(status.ConnectionState), status.ErrorCode,
            status.ErrorCode == 0 ? null : new Win32Exception(status.ErrorCode).Message);
    }

    private static string FriendlyState(int state) => state switch
    {
        0x2000 => "Connected",
        0x2001 => "Disconnected",
        5 or 6 or 7 or 8 or 9 or 10 or 12 or 13 or 14 or 19 => "Authenticating",
        >= 0 and <= 21 => "Connecting",
        _ => $"State{state}"
    };

    private static string ReadFixedString(IntPtr item, int offset, int characterCount) =>
        Marshal.PtrToStringUni(IntPtr.Add(item, offset), characterCount)?.TrimEnd('\0')
        ?? string.Empty;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RasConnectionStatus
    {
        public int Size;
        public int ConnectionState;
        public int ErrorCode;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RasMaxDeviceType + 1)]
        public string DeviceType;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RasMaxDeviceName + 1)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)]
        public string PhoneNumber;
    }

    private sealed record RasStatus(string State, int ErrorCode, string? ErrorMessage);

    private static class NativeMethods
    {
        [DllImport("rasapi32.dll", EntryPoint = "RasEnumConnectionsW")]
        internal static extern int RasEnumConnections(
            IntPtr connections,
            ref int bufferBytes,
            out int connectionCount);

        [DllImport("rasapi32.dll", EntryPoint = "RasGetConnectStatusW",
            CharSet = CharSet.Unicode)]
        internal static extern int RasGetConnectStatus(IntPtr connection,
            ref RasConnectionStatus status);
    }
}
