using EndpointAgents.Shared.Ibsng;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Ibsng;

/// <summary>Combines authoritative Windows RAS state with supplementary dialer UI evidence.</summary>
[SupportedOSPlatform("windows")]
public sealed class IbsngConnectionInspector(RasConnectionInspector rasInspector)
{
    /// <summary>Returns the normalized IBSng connection state and matching RAS evidence.</summary>
    public Task<IbsngConnectionStateResult> GetConnectionStateAsync(IbsngVpnProtocol protocol,
        IbsngDialerUiSnapshot uiSnapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ras = rasInspector.GetActiveConnections()
            .Select(connection => new IbsngRasConnectionSnapshot(connection.EntryName,
                connection.DeviceType, connection.DeviceName, connection.State,
                connection.ErrorCode, connection.ErrorMessage))
            .ToArray();
        return Task.FromResult(Resolve(protocol, uiSnapshot, ras));
    }

    /// <summary>Applies the documented RAS-first precedence to already-collected evidence.</summary>
    public static IbsngConnectionStateResult Resolve(IbsngVpnProtocol protocol,
        IbsngDialerUiSnapshot uiSnapshot,
        IReadOnlyList<IbsngRasConnectionSnapshot> ras)
    {
        var matching = ras.Where(connection => MatchesProtocol(connection, protocol)).ToArray();

        if (matching.Any(connection => connection.State == "Connected"))
            return new IbsngConnectionStateResult(IbsngConnectionState.Connected, ras,
                "Windows reports an active RAS VPN connection.");
        if (matching.Any(connection => connection.ErrorCode != 0))
            return new IbsngConnectionStateResult(IbsngConnectionState.Failed, ras,
                "Windows RAS reports a VPN connection error.");
        if (matching.Any(connection => connection.State == "Authenticating"))
            return new IbsngConnectionStateResult(IbsngConnectionState.Authenticating, ras,
                "Windows is authenticating the VPN connection.");
        if (matching.Any(connection => connection.State == "Connecting"))
            return new IbsngConnectionStateResult(IbsngConnectionState.Connecting, ras,
                "Windows is establishing the VPN connection.");

        var state = uiSnapshot.ParsedState == IbsngConnectionState.Connected
            ? IbsngConnectionState.Inconsistent
            : uiSnapshot.ParsedState;
        if (state == IbsngConnectionState.Unknown
            && uiSnapshot.ReadState == IbsngUiReadState.ProcessNotRunning)
            state = IbsngConnectionState.Disconnected;
        return new IbsngConnectionStateResult(state, ras,
            state == IbsngConnectionState.Inconsistent
                ? "The dialer says Connected but Windows has no matching RAS connection."
                : "No active matching Windows RAS connection was found.");
    }

    private static bool MatchesProtocol(IbsngRasConnectionSnapshot connection,
        IbsngVpnProtocol protocol)
    {
        var evidence = $"{connection.EntryName} {connection.DeviceType} {connection.DeviceName}";
        if (protocol == IbsngVpnProtocol.Auto)
            return evidence.Contains("VPN", StringComparison.OrdinalIgnoreCase)
                || evidence.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase)
                || evidence.Contains("IBS", StringComparison.OrdinalIgnoreCase);
        return protocol switch
        {
            IbsngVpnProtocol.L2tp => evidence.Contains("L2TP", StringComparison.OrdinalIgnoreCase),
            IbsngVpnProtocol.Pptp => evidence.Contains("PPTP", StringComparison.OrdinalIgnoreCase),
            IbsngVpnProtocol.Sstp => evidence.Contains("SSTP", StringComparison.OrdinalIgnoreCase),
            IbsngVpnProtocol.Ikev2 => evidence.Contains("IKEv2", StringComparison.OrdinalIgnoreCase),
            _ => true
        };
    }
}

/// <summary>Normalized connection decision plus its Windows RAS evidence.</summary>
public sealed record IbsngConnectionStateResult(
    IbsngConnectionState State,
    IReadOnlyList<IbsngRasConnectionSnapshot> RasConnections,
    string Summary);
