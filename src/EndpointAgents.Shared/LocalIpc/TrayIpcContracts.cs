using EndpointAgents.Shared.Ibsng;
using EndpointAgents.Shared.Office;

namespace EndpointAgents.Shared.LocalIpc;

public static class TrayIpcProtocol
{
    public const string DefaultPipePrefix = "EndpointAgents.Tray";
    public const string LaunchIbsngMethod = "ibsng.launch";
    public const string ConfirmIbsngInstallMethod = "ibsng.confirm_install";
    public const string ReconcileIbsngMethod = "ibsng.reconcile";
    public const string InspectIbsngMethod = "ibsng.inspect";
    public const string ReadIbsngUiMethod = "ibsng.read_ui_snapshot";
    public const string OfficeActivationStatusMethod = "office.activation_status";
    public const string OfficeRemediationLaunchMethod = "office.remediation_launch";
    public const string OfficeBugfixConsentMethod = "office.bugfix_consent";
    public const int MaxMessageCharacters = 16 * 1024;

    public static string GetPipeName(string prefix, int sessionId) =>
        $"{prefix}.{sessionId}.v1";
}

public sealed record TrayIpcRequest(
    string Id,
    string Method,
    string DialerPath,
    string ExpectedSha256,
    string? ServerHost,
    string DialerSearchPattern = "IBSngDialer*.exe",
    OfficeRemediationLaunchRequest? OfficeRemediation = null,
    OfficeBugfixConsentRequest? OfficeBugfixConsent = null);

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
    string? SignerSubject = null);
