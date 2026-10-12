using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace EndpointAgents.Service;

/// <summary>
/// Decides whether a pipe caller may become the Tray of its Windows session. Every fact comes from
/// Windows (the kernel-reported client session and token), never from the message itself.
/// </summary>
public static class TrayAttachPolicy
{
    private const int ErrorPrivilegeNotHeld = 1314;

    public sealed record Caller(int SessionId, string UserSid, string UserName);

    public static Caller Identify(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var sessionId))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        string? sid = null;
        var name = string.Empty;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent();
            sid = identity.User?.Value;
            name = identity.Name;
        });
        return new Caller((int)sessionId,
            sid ?? throw new UnauthorizedAccessException("The pipe caller has no Windows user SID."),
            name);
    }

    /// <summary>Returns null when allowed, otherwise the reason for refusing.</summary>
    public static string? Check(Caller caller)
    {
        if (caller.SessionId == 0)
            return "A Tray must run in an interactive user session.";

        var sessionUser = TryGetSessionUserSid(caller.SessionId, out var error);
        if (sessionUser is not null)
            return string.Equals(sessionUser, caller.UserSid, StringComparison.OrdinalIgnoreCase)
                ? null
                : "The Tray does not belong to the user signed in to its session.";

        // Only LocalSystem can read another session's token. A service started interactively for
        // development runs as the user already, so only that same user may attach.
        if (error == ErrorPrivilegeNotHeld)
        {
            using var self = WindowsIdentity.GetCurrent();
            return string.Equals(self.User?.Value, caller.UserSid, StringComparison.OrdinalIgnoreCase)
                ? null
                : "The Tray user does not match the interactive service user.";
        }
        return "The signed-in user of the Tray session could not be verified.";
    }

    private static string? TryGetSessionUserSid(int sessionId, out int error)
    {
        error = 0;
        if (!WTSQueryUserToken((uint)sessionId, out var token))
        {
            error = Marshal.GetLastWin32Error();
            return null;
        }
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            return identity.User?.Value;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint sessionId);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle token);
}
