using EndpointAgents.Platform.Windows.UserSessions;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Office;

public interface IOfficeUserSessionSource
{
    InteractiveUserSession? GetActiveSession();
}

[SupportedOSPlatform("windows")]
public sealed class OfficeUserSessionSource(
    InteractiveUserSessionLocator locator) : IOfficeUserSessionSource
{
    public InteractiveUserSession? GetActiveSession() =>
        locator.GetActiveSessions().FirstOrDefault();
}
