using EndpointAgents.Core.Commands;
using EndpointAgents.Shared.Rpc;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsActivationStatusCommandHandler(
    IWindowsActivationProbe probe, WindowsArtifactClient artifacts) : IEndpointCommandHandler
{
    public string Method => "system.windows_activation_status";

    public async Task<RpcResponse> HandleAsync(RpcRequest request,
        CommandExecutionContext context, CancellationToken cancellationToken)
    {
        var report = await probe.ProbeAsync(cancellationToken);
        if (report.ProbeSucceeded
            && report.State != EndpointAgents.Shared.Windows.WindowsActivationStates.Licensed)
        {
            try
            {
                var catalog = await artifacts.GetArtifactsAsync(cancellationToken);
                report = report with
                {
                    CompatibleBugfixArtifactIds = WindowsBugfixCompatibility
                        .SelectCompatibleArtifactIds(catalog, report,
                            Environment.OSVersion.Version)
                };
            }
            catch (OperationCanceledException) { throw; }
            catch { report = report with { CompatibleBugfixArtifactIds = [] }; }
        }
        return RpcResponse.Success(request.Id, report);
    }
}
