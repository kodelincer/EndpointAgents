using EndpointAgents.Core.Commands;
using EndpointAgents.Platform.Windows.Diagnostics;
using EndpointAgents.Shared.Rpc;
using EndpointAgents.Shared.Support;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace EndpointAgents.Platform.Windows.SystemInfo;

/// <summary>
/// Read-only overview of the PC (network, Windows, domain, hardware, storage). Callers may
/// ask for a subset with <c>params.sections</c>; every section is collected independently.
/// </summary>
public sealed class SystemInfoCommandHandler(
    IEnumerable<ISystemInfoSection> sections,
    ILogger<SystemInfoCommandHandler> logger) : IEndpointCommandHandler
{
    private readonly IReadOnlyDictionary<string, ISystemInfoSection> _sections =
        sections.ToDictionary(section => section.Name, StringComparer.Ordinal);

    public string Method => SystemInfoContracts.Method;

    public async Task<RpcResponse> HandleAsync(RpcRequest request,
        CommandExecutionContext context, CancellationToken cancellationToken)
    {
        if (!TryParseSections(request.Params, out var requested))
            return RpcResponse.Failure(request.Id, 400,
                $"params.sections must contain only: {string.Join(", ", SystemInfoContracts.Sections)}.");

        var warnings = new SystemInfoWarnings();
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in requested)
        {
            if (!_sections.TryGetValue(name, out var section))
            {
                result[name] = null;
                continue;
            }
            try
            {
                result[name] = await section.CollectAsync(warnings, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "System info section {Section} failed.", name);
                warnings.Add(name, ex);
                result[name] = null;
            }
        }

        return RpcResponse.Success(request.Id,
            DiagnosticResponse.Create(context, result, warnings.Items));
    }

    internal static bool TryParseSections(JsonElement? parameters,
        out IReadOnlyList<string> sections)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } value
            || !value.TryGetProperty("sections", out var list)
            || list.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return SystemInfoContracts.TryNormalizeSections(null, out sections);
        if (list.ValueKind != JsonValueKind.Array
            || list.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
        {
            sections = [];
            return false;
        }
        return SystemInfoContracts.TryNormalizeSections(
            list.EnumerateArray().Select(item => item.GetString()), out sections);
    }
}
