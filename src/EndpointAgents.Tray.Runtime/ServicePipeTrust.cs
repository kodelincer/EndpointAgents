using System.IO.Pipes;
using System.Security.Principal;

namespace EndpointAgents.Tray;

/// <summary>
/// Checks, right after connecting, that the pipe the Tray reached was created by the real service.
/// On a shared PC another signed-in user could create a pipe with the service's name before the
/// service starts and pose as it. Windows records who created a pipe as its owner, and an ordinary
/// user cannot make SYSTEM or the Administrators group the owner of an object, so the owner tells
/// the two apart. The current user is also accepted so the unelevated development host still works;
/// a pipe that user created can only fool that same user.
/// </summary>
public static class ServicePipeTrust
{
    public static bool IsTrustedOwner(SecurityIdentifier? owner, SecurityIdentifier? currentUser) =>
        owner is not null &&
        (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
         owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
         owner == currentUser);

    public static void EnsureTrustedServer(NamedPipeClientStream pipe)
    {
        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        using var identity = WindowsIdentity.GetCurrent();
        if (!IsTrustedOwner(owner, identity.User))
            throw new UnauthorizedAccessException(
                "The local service pipe is not owned by the EndpointAgents service.");
    }
}
