using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;

namespace EndpointAgents.Platform.Windows.UserSessions;

public sealed record InteractiveUserSession(
    int SessionId,
    string UserName,
    string? DomainName,
    string DesktopPath);

/// <summary>Resolves desktop folders for users with active Windows sessions.</summary>
[SupportedOSPlatform("windows")]
public sealed class InteractiveUserSessionLocator
{
    public IReadOnlyList<InteractiveUserSession> GetActiveSessions()
    {
        var sessions = new List<InteractiveUserSession>();
        var consoleSession = NativeMethods.WTSGetActiveConsoleSessionId();

        if (!NativeMethods.WTSEnumerateSessionsW(IntPtr.Zero, 0, 1,
            out var sessionsPointer, out var count))
        {
            return sessions;
        }

        try
        {
            var itemSize = Marshal.SizeOf<NativeMethods.WtsSessionInfo>();
            for (var index = 0; index < count; index++)
            {
                var itemPointer = IntPtr.Add(sessionsPointer, index * itemSize);
                var session = Marshal.PtrToStructure<NativeMethods.WtsSessionInfo>(itemPointer);
                if (session.State != NativeMethods.WtsConnectState.Active)
                    continue;

                var userName = QuerySessionString(session.SessionId, NativeMethods.WtsInfoClass.UserName);
                if (string.IsNullOrWhiteSpace(userName))
                    continue;

                var domainName = QuerySessionString(session.SessionId, NativeMethods.WtsInfoClass.DomainName);
                var desktopPath = ResolveDesktopPath(userName, domainName);
                if (!string.IsNullOrWhiteSpace(desktopPath))
                    sessions.Add(new InteractiveUserSession(
                        session.SessionId, userName, domainName, desktopPath));
            }
        }
        finally
        {
            NativeMethods.WTSFreeMemory(sessionsPointer);
        }

        return sessions
            .OrderByDescending(item => (uint)item.SessionId == consoleSession)
            .ThenBy(item => item.SessionId)
            .ToArray();
    }

    private static string? QuerySessionString(int sessionId, NativeMethods.WtsInfoClass infoClass)
    {
        if (!NativeMethods.WTSQuerySessionInformationW(IntPtr.Zero, sessionId, infoClass,
            out var valuePointer, out _))
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(valuePointer);
        }
        finally
        {
            NativeMethods.WTSFreeMemory(valuePointer);
        }
    }

    private static string? ResolveDesktopPath(string userName, string? domainName)
    {
        try
        {
            var accountName = string.IsNullOrWhiteSpace(domainName)
                ? userName
                : $"{domainName}\\{userName}";
            var sid = ((SecurityIdentifier)new NTAccount(accountName)
                .Translate(typeof(SecurityIdentifier))).Value;

            using var profileKey = Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
            var profilePath = profileKey?.GetValue("ProfileImagePath") as string;
            if (string.IsNullOrWhiteSpace(profilePath))
                return null;

            profilePath = Environment.ExpandEnvironmentVariables(profilePath);

            using var shellFolders = Registry.Users.OpenSubKey(
                $@"{sid}\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            var configuredDesktop = shellFolders?.GetValue("Desktop", null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) as string;

            if (!string.IsNullOrWhiteSpace(configuredDesktop))
            {
                var expanded = configuredDesktop
                    .Replace("%USERPROFILE%", profilePath, StringComparison.OrdinalIgnoreCase)
                    .Replace("%USERNAME%", userName, StringComparison.OrdinalIgnoreCase);
                expanded = Environment.ExpandEnvironmentVariables(expanded);
                if (Path.IsPathFullyQualified(expanded))
                    return Path.GetFullPath(expanded);
            }

            return Path.Combine(profilePath, "Desktop");
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
        catch (SecurityException)
        {
            return null;
        }
    }

    private static class NativeMethods
    {
        internal enum WtsConnectState
        {
            Active,
            Connected,
            ConnectQuery,
            Shadow,
            Disconnected,
            Idle,
            Listen,
            Reset,
            Down,
            Init
        }

        internal enum WtsInfoClass
        {
            InitialProgram,
            ApplicationName,
            WorkingDirectory,
            OemId,
            SessionId,
            UserName,
            WinStationName,
            DomainName
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct WtsSessionInfo
        {
            public int SessionId;
            public IntPtr WinStationName;
            public WtsConnectState State;
        }

        [DllImport("Wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSEnumerateSessionsW(
            IntPtr serverHandle,
            int reserved,
            int version,
            out IntPtr sessionInfo,
            out int count);

        [DllImport("Wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSQuerySessionInformationW(
            IntPtr serverHandle,
            int sessionId,
            WtsInfoClass infoClass,
            out IntPtr buffer,
            out int bytesReturned);

        [DllImport("Wtsapi32.dll")]
        internal static extern void WTSFreeMemory(IntPtr memory);

        [DllImport("Kernel32.dll")]
        internal static extern uint WTSGetActiveConsoleSessionId();
    }
}
