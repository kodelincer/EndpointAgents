using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace EndpointAgents.Updater;

/// <summary>The real Windows calls behind <see cref="IMachine"/>. Runs as SYSTEM.</summary>
internal sealed class WindowsMachine(UpdaterLog log) : IMachine
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services\";
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string TrayValue = "EndpointAgents.Tray";
    private static readonly string[] TrayProcessNames = ["EndpointAgents.TrayWeb", "EndpointAgents.Tray"];

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public void Sleep(TimeSpan delay) => Thread.Sleep(delay);

    public bool StopService(string name, TimeSpan timeout)
    {
        try
        {
            using var service = new ServiceController(name);
            if (service.Status is ServiceControllerStatus.Stopped) return true;
            if (service.Status is not ServiceControllerStatus.StopPending) service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException
                                       or System.ComponentModel.Win32Exception)
        {
            log.Write($"Stopping {name} failed: {ex.Message}");
            return false;
        }
    }

    public bool StartService(string name)
    {
        try
        {
            using var service = new ServiceController(name);
            // Windows may already be restarting it after a crash (service recovery).
            if (service.Status is ServiceControllerStatus.StopPending)
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            if (service.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending) return true;
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException
                                       or System.ComponentModel.Win32Exception)
        {
            log.Write($"Starting {name} failed: {ex.Message}");
            return false;
        }
    }

    public string? GetServiceImagePath(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(ServicesKey + name);
        return key?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public void SetServiceImagePath(string name, string imagePath)
    {
        using var key = Registry.LocalMachine.OpenSubKey(ServicesKey + name, writable: true)
            ?? throw new InvalidOperationException($"The service {name} is not installed.");
        key.SetValue("ImagePath", imagePath, RegistryValueKind.ExpandString);
        log.Write($"Service now runs {imagePath}.");
    }

    public string? GetTrayRunCommand()
    {
        using var key = Registry.LocalMachine.OpenSubKey(RunKey);
        return key?.GetValue(TrayValue) as string;
    }

    public void SetTrayRunCommand(string? command)
    {
        using var key = Registry.LocalMachine.CreateSubKey(RunKey, writable: true);
        if (string.IsNullOrWhiteSpace(command)) key.DeleteValue(TrayValue, throwOnMissingValue: false);
        else key.SetValue(TrayValue, command, RegistryValueKind.String);
    }

    public IReadOnlyCollection<int> StopTrays(string installRoot)
    {
        var root = Path.GetFullPath(installRoot).TrimEnd('\\') + "\\";
        var sessions = new HashSet<int>();
        foreach (var name in TrayProcessNames)
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (path is null || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                        sessions.Add(process.SessionId);
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(10_000);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        log.Write($"Could not close a Tray: {ex.Message}");
                    }
                }
            }
        if (sessions.Count > 0) log.Write($"Closed the Tray in session(s) {string.Join(", ", sessions)}.");
        return sessions;
    }

    /// <summary>Starts the Tray again for each signed-in user that had it open, as that user.</summary>
    public void StartTray(string trayExe, IReadOnlyCollection<int> sessions)
    {
        if (!File.Exists(trayExe)) { log.Write($"The Tray {trayExe} is missing."); return; }
        foreach (var session in sessions)
        {
            if (!Native.WTSQueryUserToken((uint)session, out var userToken))
            {
                log.Write($"No signed-in user in session {session}; the Tray starts at their next sign-in.");
                continue;
            }
            var environment = IntPtr.Zero;
            try
            {
                Native.CreateEnvironmentBlock(out environment, userToken, false);
                var startup = new Native.STARTUPINFO { cb = Marshal.SizeOf<Native.STARTUPINFO>(), lpDesktop = @"winsta0\default" };
                var commandLine = UpdaterPaths.Quote(trayExe);
                if (Native.CreateProcessAsUser(userToken, trayExe, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                        Native.CREATE_UNICODE_ENVIRONMENT, environment, Path.GetDirectoryName(trayExe),
                        ref startup, out var info))
                {
                    Native.CloseHandle(info.hProcess);
                    Native.CloseHandle(info.hThread);
                    log.Write($"Started the Tray in session {session}.");
                }
                else log.Write($"Could not start the Tray in session {session} (error {Marshal.GetLastWin32Error()}).");
            }
            finally
            {
                if (environment != IntPtr.Zero) Native.DestroyEnvironmentBlock(environment);
                Native.CloseHandle(userToken);
            }
        }
    }

    public void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    public IReadOnlyList<string> ListDirectories(string path) =>
        Directory.Exists(path) ? Directory.GetDirectories(path) : [];

    private static class Native
    {
        public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

        [DllImport("userenv.dll", SetLastError = true)]
        public static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

        [DllImport("userenv.dll", SetLastError = true)]
        public static extern bool DestroyEnvironmentBlock(IntPtr environment);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessAsUser(IntPtr token, string applicationName, string commandLine,
            IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
            IntPtr environment, string? currentDirectory, ref STARTUPINFO startupInfo,
            out PROCESS_INFORMATION processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
