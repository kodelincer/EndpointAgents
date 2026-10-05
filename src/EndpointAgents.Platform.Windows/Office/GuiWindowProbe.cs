using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace EndpointAgents.Platform.Windows.Office;

/// <summary>
/// Finds visible top-level windows owned by a BAT's process tree. The runner starts
/// cmd without a console window, so any visible window there is a GUI the hidden
/// unattended runner cannot answer (a message box, an mshta dialog, a GUI program).
/// Windows are enumerated on the caller's desktop, which the BAT inherits.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class GuiWindowProbe
{
    public static bool HasVisibleWindow(int rootProcessId)
    {
        var tree = GetProcessTree(rootProcessId);
        if (tree.Count == 0) return false;

        var found = false;
        NativeMethods.EnumWindows((window, _) =>
        {
            if (!NativeMethods.IsWindowVisible(window)) return true;
            NativeMethods.GetWindowThreadProcessId(window, out var processId);
            if (!tree.Contains((int)processId)) return true;
            var className = new StringBuilder(256);
            NativeMethods.GetClassName(window, className, className.Capacity);
            // A console host window would be cmd's own terminal, not a dialog.
            if (className.ToString() == "ConsoleWindowClass") return true;
            found = true;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static HashSet<int> GetProcessTree(int rootProcessId)
    {
        var parents = new Dictionary<int, int>();
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(
            NativeMethods.Th32csSnapProcess, 0);
        if (snapshot == NativeMethods.InvalidHandle) return [];
        try
        {
            var entry = new NativeMethods.ProcessEntry32
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry32>()
            };
            if (!NativeMethods.Process32First(snapshot, ref entry)) return [];
            do parents[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            while (NativeMethods.Process32Next(snapshot, ref entry));
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        var tree = new HashSet<int> { rootProcessId };
        bool added;
        do
        {
            added = false;
            foreach (var (child, parent) in parents)
                if (tree.Contains(parent) && child != parent && tree.Add(child))
                    added = true;
        }
        while (added);
        return tree;
    }

    private static class NativeMethods
    {
        public const uint Th32csSnapProcess = 0x00000002;
        public static readonly IntPtr InvalidHandle = new(-1);

        public delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct ProcessEntry32
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int PriorityClassBase;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExeFile;
        }

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr window, StringBuilder className,
            int maxCount);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
        public static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
        public static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
