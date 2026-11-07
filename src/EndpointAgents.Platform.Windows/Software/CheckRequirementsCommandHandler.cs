using EndpointAgents.Core.Commands;
using EndpointAgents.Platform.Windows.Diagnostics;
using EndpointAgents.Platform.Windows.Office;
using EndpointAgents.Platform.Windows.Packages;
using EndpointAgents.Shared.Packages;
using EndpointAgents.Shared.Rpc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Software;

/// <summary>
/// Read-only: checks one install package's signed system requirements against this PC and
/// whether the software is already installed. Nothing is downloaded or changed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CheckRequirementsCommandHandler(
    PackageArtifactClient artifacts,
    IOptions<PackageSigningOptions> signing,
    ILogger<CheckRequirementsCommandHandler> logger) : IEndpointCommandHandler
{
    public string Method => PackageCommands.CheckRequirements;

    public async Task<RpcResponse> HandleAsync(RpcRequest request,
        CommandExecutionContext context, CancellationToken cancellationToken)
    {
        if (!PackageRunCommandHandler.HasOnlyApprovedParameters(request.Params)
            || !OfficeRemediationCommandHandler.TryParseArtifactId(request.Params, out var packageId)
            || !PackageDescriptorPolicy.IsValidId(packageId))
            return RpcResponse.Failure(request.Id, 400, "params.artifactId must be an approved package ID.");

        IReadOnlyList<PackageDescriptor> catalog;
        try { catalog = await artifacts.GetCatalogAsync(cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The package catalog could not be read.");
            return RpcResponse.Failure(request.Id, 502, "The package catalog could not be read from the control plane.",
                DiagnosticResponse.Sanitize(ex));
        }
        var descriptor = catalog.FirstOrDefault(item => item.PackageId == packageId);
        if (descriptor is null)
            return RpcResponse.Failure(request.Id, 404, "The requested package is not available.");
        if (!PackageDescriptorPolicy.TryValidate(descriptor, out _)
            || !PackageSignaturePolicy.Verify(descriptor, signing.Value.TrustedPublicKeys))
            return RpcResponse.Failure(request.Id, 412, "The package is not signed by a trusted administrator key.");

        var report = RequirementChecker.Check(descriptor, new WindowsMachineFacts(), catalog);
        return RpcResponse.Success(request.Id, report);
    }
}
