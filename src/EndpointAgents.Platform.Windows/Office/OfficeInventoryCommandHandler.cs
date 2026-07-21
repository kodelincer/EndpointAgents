using EndpointAgents.Core.Commands;
using EndpointAgents.Platform.Windows.Diagnostics;
using EndpointAgents.Shared.Rpc;
using Microsoft.Extensions.Logging;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Office;

/// <summary>Returns read-only installed Office information for the endpoint.</summary>
[SupportedOSPlatform("windows")]
public sealed class OfficeInventoryCommandHandler(
    OfficeInventoryCollector collector,
    ILogger<OfficeInventoryCommandHandler> logger) : IEndpointCommandHandler
{
    public string Method => "software.office_inventory";

    public Task<RpcResponse> HandleAsync(RpcRequest request,
        CommandExecutionContext context, CancellationToken cancellationToken)
    {
        var snapshot = collector.Collect(cancellationToken);
        foreach (var issue in snapshot.Issues)
            logger.LogWarning("{Source}: {Message} ({Exception})",
                issue.Source, issue.Message, issue.Exception);

        var warnings = snapshot.Issues
            .Select(issue => new DiagnosticWarning(issue.Source, issue.Message, issue.Exception))
            .ToArray();
        var response = RpcResponse.Success(request.Id,
            DiagnosticResponse.Create(context, snapshot.Inventory, warnings));

        return Task.FromResult(response);
    }
}
