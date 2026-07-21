namespace ControlPlane.Api.Commands;

/// <summary>Operator-visible metadata for one approved endpoint RPC method.</summary>
public sealed record CommandDefinition(string Method, string Name, string Description, object? DefaultParams);

/// <summary>
/// Allow-list of RPC methods exposed by the dashboard. The API never accepts an
/// arbitrary method supplied only by a browser request.
/// </summary>
public sealed class CommandCatalog
{
    private static readonly CommandDefinition[] Commands =
    [
        new("endpoint.get_info", "Get endpoint information",
            "Collect the active user, machine, network adapter, proxy, time, and internet status.", null),
        new("software.office_inventory", "Get installed Office information",
            "Read installed Office apps and their versions plus Click-to-Run architecture, language, and update-channel metadata.", null),
        new("software.office_activation_status", "Check Office activation",
            "Inspect sanitized Microsoft 365 Apps and volume Office activation state and recommend compatible approved remediation artifacts.", null),
        new("software.office_remediation_run", "Run approved Office remediation",
            "Download one compatible Microsoft-signed remediation by artifact ID, ask the signed-in user for consent, and launch it without caller-provided arguments.",
            new { artifactId = "" }),
        new("network.ibsng_diagnose", "Diagnose IBSng connection",
            "Inspect the approved dialer, gateway, DNS, DHCP, server route, protocol, UI log, and Windows RAS/VPN state.",
            new { serverHost = "", protocol = "Auto", includeDialerLog = true }),
        new("network.ibsng_ensure_dialer", "Ensure IBSng dialer",
            "Install or repair the approved hash-verified dialer on the active user's desktop.", null),
        new("network.ibsng_launch", "Launch IBSng dialer",
            "Ask the active user's Tray to launch the approved dialer without collecting credentials.", null)
    ];

    /// <summary>Returns all commands that may be selected by an operator.</summary>
    public IReadOnlyList<CommandDefinition> GetAll() => Commands;

    /// <summary>Returns whether a method is explicitly approved for dispatch.</summary>
    public bool Contains(string method) => Commands.Any(command => command.Method == method);
}
