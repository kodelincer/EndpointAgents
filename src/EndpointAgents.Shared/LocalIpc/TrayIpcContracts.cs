using EndpointAgents.Shared.Actions;
using EndpointAgents.Shared.Ibsng;
using EndpointAgents.Shared.Office;
using EndpointAgents.Shared.Windows;

namespace EndpointAgents.Shared.LocalIpc;

public static class TrayIpcProtocol
{
    public const string LaunchIbsngMethod = "ibsng.launch";
    public const string ConfirmIbsngInstallMethod = "ibsng.confirm_install";
    public const string ReconcileIbsngMethod = "ibsng.reconcile";
    public const string InspectIbsngMethod = "ibsng.inspect";
    public const string ReadIbsngUiMethod = "ibsng.read_ui_snapshot";
    public const string OfficeRemediationLaunchMethod = "office.remediation_launch";
    public const string OfficeBugfixConsentMethod = "office.bugfix_consent";
    public const string WindowsBugfixConsentMethod = "windows.bugfix_consent";
    public const string WindowsHelloLaunchMethod = "windows.hello_sample_launch";
    /// <summary>Runs one catalogued user-context action (EndpointActionCatalog, RunAs User).</summary>
    public const string EndpointUserActionMethod = "endpoint.user_action";
    public const int MaxMessageCharacters = 64 * 1024;
    /// <summary>User-action results larger than this are refused so a reply always fits a message.</summary>
    public const int MaxActionResultCharacters = 48 * 1024;
}

public sealed record TrayIpcRequest(
    string Id,
    string Method,
    string DialerPath,
    string ExpectedSha256,
    string? ServerHost,
    string DialerSearchPattern = "IBSngDialer*.exe",
    OfficeRemediationLaunchRequest? OfficeRemediation = null,
    OfficeBugfixConsentRequest? OfficeBugfixConsent = null,
    WindowsBugfixConsentRequest? WindowsBugfixConsent = null,
    WindowsHelloLaunchRequest? WindowsHello = null,
    EndpointActionRequest? Action = null);

public sealed record TrayIpcResponse(
    string Id,
    bool Success,
    string State,
    int? ProcessId = null,
    string? Message = null,
    IReadOnlyList<int>? StoppedProcessIds = null,
    IReadOnlyList<int>? CandidateProcessIds = null,
    IbsngDialerUiSnapshot? DialerUi = null,
    OfficeActivationProbeResult? OfficeActivation = null,
    string? OfficeRemediationArtifactId = null,
    string? SignerSubject = null,
    EndpointActionResult? ActionResult = null);
