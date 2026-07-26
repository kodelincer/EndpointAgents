namespace EndpointAgents.Shared.Ibsng;

/// <summary>VPN protocols that the IBSng guided workflow can diagnose.</summary>
public enum IbsngVpnProtocol
{
    Auto,
    L2tp,
    Pptp,
    Sstp,
    Ikev2
}

/// <summary>Normalized connection state derived from Windows RAS and dialer evidence.</summary>
public enum IbsngConnectionState
{
    Unknown,
    Disconnected,
    Connecting,
    Authenticating,
    Connected,
    Failed,
    Inconsistent
}

/// <summary>Explains whether the interactive Tray could read the dialer's log control.</summary>
public enum IbsngUiReadState
{
    Success,
    ProcessNotRunning,
    WindowNotFound,
    LogControlNotAccessible,
    Error
}

/// <summary>Validated non-secret inputs for one IBSng diagnostic run.</summary>
public sealed record IbsngDiagnosticRequest(
    string ServerHost,
    IbsngVpnProtocol Protocol = IbsngVpnProtocol.Auto,
    bool IncludeDialerLog = true,
    string? UserSid = null);

/// <summary>One bounded diagnostic result suitable for local and remote display.</summary>
public sealed record IbsngDiagnosticCheck(
    string Name,
    string Status,
    string Summary,
    string? Detail = null);

/// <summary>Safe, bounded information read from the dialer's interactive window.</summary>
public sealed record IbsngDialerUiSnapshot(
    IbsngUiReadState ReadState,
    int? ProcessId,
    bool WindowAvailable,
    IbsngConnectionState ParsedState,
    IReadOnlyList<string> LogLines,
    string? Message = null);

/// <summary>Windows RAS evidence for one active or transitioning VPN connection.</summary>
public sealed record IbsngRasConnectionSnapshot(
    string EntryName,
    string DeviceType,
    string DeviceName,
    string State,
    int ErrorCode,
    string? ErrorMessage = null);

/// <summary>Relevant IP configuration for one operational Windows network adapter.</summary>
public sealed record IbsngNetworkAdapterSnapshot(
    string Name,
    string Description,
    string InterfaceType,
    bool? DhcpEnabled,
    string? DhcpServer,
    bool HasApipaAddress,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<string> Gateways,
    IReadOnlyList<string> DnsServers);

/// <summary>Complete read-only IBSng diagnosis returned by the endpoint agent.</summary>
public sealed record IbsngDiagnosticReport(
    DateTimeOffset GeneratedAtUtc,
    string ServerHost,
    IbsngVpnProtocol Protocol,
    IbsngConnectionState ConnectionState,
    string Summary,
    bool CredentialsCollected,
    string? DialerPath,
    bool DialerTrusted,
    bool DialerRunning,
    IbsngDialerUiSnapshot DialerUi,
    IReadOnlyList<IbsngRasConnectionSnapshot> RasConnections,
    IReadOnlyList<IbsngNetworkAdapterSnapshot> Adapters,
    IReadOnlyList<IbsngDiagnosticCheck> Checks);

/// <summary>Compact, sanitized diagnosis that may be attached to a helpdesk request.</summary>
public sealed record IbsngSupportEvidence(
    DateTimeOffset GeneratedAtUtc,
    string ServerHost,
    IbsngVpnProtocol Protocol,
    IbsngConnectionState ConnectionState,
    string Summary,
    IReadOnlyList<IbsngDiagnosticCheck> Checks,
    IReadOnlyList<string> SanitizedLogLines)
{
    /// <summary>Creates a bounded support attachment from a full local report.</summary>
    public static IbsngSupportEvidence FromReport(IbsngDiagnosticReport report) =>
        new(report.GeneratedAtUtc, report.ServerHost, report.Protocol,
            report.ConnectionState, report.Summary, report.Checks.Take(20).ToArray(),
            report.DialerUi.LogLines.TakeLast(20).ToArray());
}
