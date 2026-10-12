using EndpointAgents.Shared.LocalIpc;
using EndpointAgents.Shared.Windows;

namespace EndpointAgents.Tray;

public sealed class WindowsRemediationConsent(IUserConsentPrompt? consent = null)
{
    private readonly IUserConsentPrompt _consent = consent ?? new MessageBoxConsentPrompt();

    public async Task<TrayIpcResponse> ConfirmAsync(TrayIpcRequest request,
        CancellationToken cancellationToken)
    {
        var bugfix = request.WindowsBugfixConsent;
        if (request.Method != TrayIpcProtocol.WindowsBugfixConsentMethod
            || bugfix is null
            || bugfix.ArtifactId != WindowsRemediationPaths.ArtifactId)
            return new TrayIpcResponse(request.Id, false,
                "InvalidWindowsBugfixRequest");

        var approved = await _consent.ConfirmAsync(
            UserConsentPrompts.WindowsBugfix(bugfix.DisplayName, bugfix.Description),
            cancellationToken);
        return approved
            ? new TrayIpcResponse(request.Id, true, "UserApproved")
            : new TrayIpcResponse(request.Id, false, "UserDeclined");
    }
}
