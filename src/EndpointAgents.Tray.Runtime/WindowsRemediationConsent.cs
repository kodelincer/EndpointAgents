using EndpointAgents.Shared.LocalIpc;
using EndpointAgents.Shared.Windows;

using System.Windows.Forms;

namespace EndpointAgents.Tray;

public sealed class WindowsRemediationConsent
{
    public TrayIpcResponse Confirm(TrayIpcRequest request)
    {
        var bugfix = request.WindowsBugfixConsent;
        if (request.Method != TrayIpcProtocol.WindowsBugfixConsentMethod
            || bugfix is null
            || bugfix.ArtifactId != WindowsRemediationPaths.ArtifactId)
            return new TrayIpcResponse(request.Id, false,
                "InvalidWindowsBugfixRequest");

        var consent = MessageBox.Show(
            $"The helpdesk recommends this Windows activation repair:\n\n" +
            $"{bugfix.DisplayName}\n\n{bugfix.Description}\n\n" +
            "It will run silently with local SYSTEM privileges and no arguments. Run it now?",
            "EndpointAgents Windows repair",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        return consent == DialogResult.Yes
            ? new TrayIpcResponse(request.Id, true, "UserApproved")
            : new TrayIpcResponse(request.Id, false, "UserDeclined");
    }
}
