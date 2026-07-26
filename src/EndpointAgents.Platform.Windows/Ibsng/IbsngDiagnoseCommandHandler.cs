using EndpointAgents.Core.Commands;
using EndpointAgents.Platform.Windows.Diagnostics;
using EndpointAgents.Shared.Ibsng;
using EndpointAgents.Shared.Rpc;
using Microsoft.Extensions.Options;
using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;

namespace EndpointAgents.Platform.Windows.Ibsng;

/// <summary>Exposes the complete read-only IBSng diagnosis through one RPC method.</summary>
[SupportedOSPlatform("windows")]
public sealed class IbsngDiagnoseCommandHandler(
    IbsngDiagnosticService diagnostics,
    IOptions<IbsngOptions> options) : IEndpointCommandHandler
{
    public string Method => "network.ibsng_diagnose";

    /// <summary>Validates non-secret inputs and returns the endpoint diagnostic report.</summary>
    public async Task<RpcResponse> HandleAsync(RpcRequest request,
        CommandExecutionContext context, CancellationToken cancellationToken)
    {
        if (!TryParse(request.Params, options.Value.ServerHost, out var diagnosticRequest,
            out var error))
            return RpcResponse.Failure(request.Id, 400, error!);

        var report = await diagnostics.DiagnoseAsync(request.Id, diagnosticRequest!,
            cancellationToken);
        return RpcResponse.Success(request.Id, DiagnosticResponse.Create(context, report,
            Array.Empty<DiagnosticWarning>()));
    }

    /// <summary>Parses and validates the host and protocol accepted by the public command.</summary>
    public static bool TryParse(JsonElement? parameters, string? configuredHost,
        out IbsngDiagnosticRequest? request, out string? error)
    {
        var host = configuredHost;
        var protocol = IbsngVpnProtocol.Auto;
        var includeLog = true;
        string? userSid = null;
        if (parameters is { ValueKind: JsonValueKind.Object } element)
        {
            if (element.TryGetProperty("userSid", out var userSidValue))
            {
                if (userSidValue.ValueKind != JsonValueKind.Null)
                {
                    userSid = userSidValue.ValueKind == JsonValueKind.String
                        ? userSidValue.GetString()?.Trim()
                        : null;
                    if (userSid is not { Length: <= 184 }
                        || !userSid.StartsWith("S-1-", StringComparison.Ordinal))
                    {
                        request = null;
                        error = "userSid must be a valid Windows SID.";
                        return false;
                    }
                }
            }
            if (element.TryGetProperty("serverHost", out var hostValue)
                && hostValue.ValueKind == JsonValueKind.String)
            {
                var requestedHost = hostValue.GetString();
                if (!string.IsNullOrWhiteSpace(requestedHost)) host = requestedHost;
            }
            if (element.TryGetProperty("protocol", out var protocolValue))
            {
                var parsed = protocolValue.ValueKind == JsonValueKind.String
                    && Enum.TryParse<IbsngVpnProtocol>(protocolValue.GetString(), true, out var value)
                    ? value
                    : protocolValue.ValueKind == JsonValueKind.Number
                        && protocolValue.TryGetInt32(out var numeric)
                        && Enum.IsDefined(typeof(IbsngVpnProtocol), numeric)
                            ? (IbsngVpnProtocol)numeric : (IbsngVpnProtocol?)null;
                if (parsed is null)
                {
                    request = null;
                    error = "protocol must be Auto, L2TP, PPTP, SSTP, or IKEv2.";
                    return false;
                }
                protocol = parsed.Value;
            }
            if (element.TryGetProperty("includeDialerLog", out var includeValue)
                && includeValue.ValueKind is JsonValueKind.True or JsonValueKind.False)
                includeLog = includeValue.GetBoolean();
        }

        host = host?.Trim();
        if (!IsValidHost(host))
        {
            request = null;
            error = "serverHost must be one IP address or DNS host name without a URL path.";
            return false;
        }
        request = new IbsngDiagnosticRequest(host!, protocol, includeLog, userSid);
        error = null;
        return true;
    }

    private static bool IsValidHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253) return false;
        if (IPAddress.TryParse(host, out _)) return true;
        return !host.Contains('/') && !host.Contains('\\') && !host.Contains('@')
            && Uri.CheckHostName(host) == UriHostNameType.Dns;
    }
}
