using EndpointAgents.Tray;
using System.IO.Pipes;
using System.Security.Principal;

namespace EndpointAgents.Platform.Windows.Tests;

public sealed class ServicePipeTrustTests
{
    private static readonly SecurityIdentifier Me = new("S-1-5-21-1000-2000-3000-1001");
    private static readonly SecurityIdentifier OtherUser = new("S-1-5-21-1000-2000-3000-1002");

    [Fact]
    public void AcceptsServiceAdministratorsAndCurrentUser()
    {
        Assert.True(ServicePipeTrust.IsTrustedOwner(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), Me));
        Assert.True(ServicePipeTrust.IsTrustedOwner(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), Me));
        Assert.True(ServicePipeTrust.IsTrustedOwner(Me, Me));
    }

    [Fact]
    public void RejectsAnotherUserOrUnknownOwner()
    {
        Assert.False(ServicePipeTrust.IsTrustedOwner(OtherUser, Me));
        Assert.False(ServicePipeTrust.IsTrustedOwner(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), Me));
        Assert.False(ServicePipeTrust.IsTrustedOwner(null, Me));
    }

    [Fact]
    public async Task AcceptsPipeCreatedInThisProcess()
    {
        var pipeName = $"EndpointAgents.Tests.{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var waiting = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        await client.ConnectAsync(3000, TestContext.Current.CancellationToken);
        await waiting;

        ServicePipeTrust.EnsureTrustedServer(client);
    }
}
