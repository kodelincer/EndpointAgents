using System.Runtime.InteropServices;

namespace EndpointAgents.Platform.Windows.Actions.Infrastructure;

/// <summary>Windows APIs the actions call directly instead of running programs.</summary>
internal static partial class ActionNative
{
    [LibraryImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DnsFlushResolverCache();

    // Configuration manager: re-enumerate the device tree (Scan for hardware changes).
    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int CM_Locate_DevNode(out uint devInst, string? deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Reenumerate_DevNode")]
    internal static partial int CM_Reenumerate_DevNode(uint devInst, uint flags);

    // Turn one device on or off (Device Manager's Enable/Disable).
    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Enable_DevNode")]
    internal static partial int CM_Enable_DevNode(uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Disable_DevNode")]
    internal static partial int CM_Disable_DevNode(uint devInst, uint flags);

    internal const uint CM_LOCATE_DEVNODE_NORMAL = 0;
    /// <summary>Fail instead of asking the user (or needing a restart) when a device cannot stop now.</summary>
    internal const uint CM_DISABLE_UI_NOT_OK = 0x4;

    // Saved Windows credentials (only metadata is read; secrets are never touched).
    [LibraryImport("advapi32.dll", EntryPoint = "CredEnumerateW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CredEnumerate(string? filter, uint flags, out uint count,
        out IntPtr credentials);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CredDelete(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    internal static partial void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential)]
    internal struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    internal const uint CRED_TYPE_GENERIC = 1;
    internal const uint CRED_TYPE_DOMAIN_PASSWORD = 2;

    // Keyboard layouts.
    [LibraryImport("input.dll", EntryPoint = "InstallLayoutOrTip", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InstallLayoutOrTip(string profile, uint flags);

    // Mapped drives.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NETRESOURCE
    {
        public uint dwScope;
        public uint dwType;
        public uint dwDisplayType;
        public uint dwUsage;
        public string? lpLocalName;
        public string? lpRemoteName;
        public string? lpComment;
        public string? lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetAddConnection2W")]
    internal static extern int WNetAddConnection2(ref NETRESOURCE resource, string? password,
        string? userName, uint flags);

    [LibraryImport("mpr.dll", EntryPoint = "WNetCancelConnection2W", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int WNetCancelConnection2(string name, uint flags,
        [MarshalAs(UnmanagedType.Bool)] bool force);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetGetConnectionW")]
    internal static extern int WNetGetConnection(string localName,
        System.Text.StringBuilder remoteName, ref int length);

    internal const uint RESOURCETYPE_DISK = 1;
    internal const uint CONNECT_UPDATE_PROFILE = 1;

    // Tell running programs that proxy settings changed.
    [LibraryImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InternetSetOption(IntPtr handle, int option, IntPtr buffer,
        int length);

    internal const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    internal const int INTERNET_OPTION_REFRESH = 37;

    // Secure-channel query against the domain.
    [LibraryImport("netapi32.dll", EntryPoint = "I_NetLogonControl2", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int I_NetLogonControl2(string? serverName, uint functionCode,
        uint queryLevel, ref IntPtr data, out IntPtr buffer);

    [LibraryImport("netapi32.dll", EntryPoint = "NetApiBufferFree")]
    internal static partial int NetApiBufferFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NETLOGON_INFO_2
    {
        public uint netlog2_flags;
        public uint netlog2_pdc_connection_status;
        public IntPtr netlog2_trusted_dc_name;
        public uint netlog2_tc_connection_status;
    }

    internal const uint NETLOGON_CONTROL_TC_QUERY = 6;

    // Battery and memory.
    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GlobalMemoryStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [StructLayout(LayoutKind.Sequential)]
    internal struct FILETIME
    {
        public uint Low;
        public uint High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetSystemTimes", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemTimes(out FILETIME idle, out FILETIME kernel,
        out FILETIME user);
}
