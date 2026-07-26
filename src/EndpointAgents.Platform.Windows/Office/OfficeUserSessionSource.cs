using EndpointAgents.Platform.Windows.UserSessions;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace EndpointAgents.Platform.Windows.Office;

public interface IOfficeUserSessionSource
{
    InteractiveUserSession? GetActiveSession(string? userSid = null);
}

[SupportedOSPlatform("windows")]
public sealed class OfficeUserSessionSource(
    InteractiveUserSessionLocator locator) : IOfficeUserSessionSource
{
    public InteractiveUserSession? GetActiveSession(string? userSid = null)
    {
        var active = string.IsNullOrWhiteSpace(userSid)
            ? locator.GetActiveSessions().FirstOrDefault()
            : locator.GetActiveSessions().FirstOrDefault(session =>
                session.UserSid.Equals(userSid, StringComparison.OrdinalIgnoreCase));
        if (active is not null)
            return active;

        // Interactive development runs are not LocalSystem services and can use
        // their own session even when the profile registry lookup is unavailable.
        using var identity = WindowsIdentity.GetCurrent();
        var sessionId = Process.GetCurrentProcess().SessionId;
        var currentSid = identity.User?.Value;
        if (identity.IsSystem || sessionId <= 0 || string.IsNullOrWhiteSpace(currentSid))
            return null;

        var desktop = Environment.GetFolderPath(
            Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop))
            desktop = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Desktop");
        if (!string.IsNullOrWhiteSpace(userSid)
            && !currentSid.Equals(userSid, StringComparison.OrdinalIgnoreCase))
            return null;
        return new InteractiveUserSession(sessionId, Environment.UserName,
            Environment.UserDomainName, currentSid, desktop);
    }
}
