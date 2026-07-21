namespace EndpointAgents.Platform.Windows.Ibsng;

/// <summary>Endpoint-side IBSng discovery, diagnostics, and artifact settings.</summary>
public sealed class IbsngOptions
{
    public string? PortalUrl { get; set; }

    public bool IncludeWindowsUser { get; set; }

    public string? ServerHost { get; set; }

    public string DialerFileName { get; set; } = "IBSngDialer-pap(1).exe";

    public string DialerSearchPattern { get; set; } = "IBSngDialer*.exe";

    public string ArtifactApiPath { get; set; } = "/api/files/ibsng-dialer";

    /// <summary>
    /// SHA-256 approved by the endpoint administrator. Delivery is refused when
    /// this value is blank or does not match the downloaded executable.
    /// </summary>
    public string? TrustedSha256 { get; set; }

    public long MaxDownloadBytes { get; set; } = 100 * 1024 * 1024;

    public string QuarantineRoot { get; set; } =
        @"C:\ProgramData\EndpointAgents\Quarantine\Ibsng";

    public int ProbeTimeoutSeconds { get; set; } = 3;

    public int MaxProbeTargets { get; set; } = 10;

    public string TrayPipePrefix { get; set; } = "EndpointAgents.Tray";

    public int TrayConnectTimeoutSeconds { get; set; } = 5;

    public int TrayResponseTimeoutSeconds { get; set; } = 30;
}
