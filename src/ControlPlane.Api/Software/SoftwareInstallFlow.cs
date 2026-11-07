using ControlPlane.Api.Agents;
using ControlPlane.Api.Commands;
using ControlPlane.Api.Files;
using EndpointAgents.Shared.Packages;
using EndpointAgents.Shared.Rpc;
using System.Text.Json;

namespace ControlPlane.Api.Software;

/// <summary>What the control plane decided for one install request.</summary>
public enum InstallDecision
{
    /// <summary>The PC could not be checked (offline, old agent, catalog error).</summary>
    CheckFailed,
    AlreadyInstalled,
    RequirementsNotMet,
    /// <summary>The installer cannot run silently: a helpdesk task is created.</summary>
    ManualInstall,
    NeedsApproval,
    InstallNow
}

public sealed record InstallPlan(InstallDecision Decision, RequirementReport? Report, string? Error,
    IReadOnlyList<RepairPackageArtifact> InstallFirst);

/// <summary>
/// The fixed install flow: check the PC's requirements (before any approval), skip what is
/// already installed, send what cannot be silent to a person, ask for approval unless the
/// package is on the "No approval needed" list, and install missing runtimes first.
/// No AI is involved in any of these decisions.
/// </summary>
public static class SoftwareInstallFlow
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<(RequirementReport? Report, string? Error)> CheckOnPcAsync(
        AgentRegistry registry, CommandCatalog catalog, string agentId, string packageId,
        CancellationToken cancellationToken)
    {
        if (!catalog.Contains(PackageCommands.CheckRequirements))
            return (null, "The requirements check is not allow-listed on this control plane.");
        var response = await registry.SendCommandAsync(agentId, RpcRequest.Create(
                Guid.NewGuid().ToString("N"), PackageCommands.CheckRequirements,
                JsonSerializer.SerializeToElement(new { artifactId = packageId })),
            cancellationToken);
        if (response.Error is not null) return (null, response.Error.Message);
        try
        {
            var report = JsonSerializer.Deserialize<RequirementReport>(
                JsonSerializer.Serialize(response.Result, Json), Json);
            return report is null ? (null, "The PC returned no requirements report.") : (report, null);
        }
        catch (JsonException)
        {
            return (null, "The PC returned an unreadable requirements report.");
        }
    }

    public static async Task<InstallPlan> PlanAsync(RepairPackageArtifact package,
        AgentRegistry registry, CommandCatalog catalog, FileArtifactRepository files,
        SoftwareApprovalStore approvals, string agentId, bool alreadyApproved,
        CancellationToken cancellationToken)
    {
        RequirementReport? report = null;
        var installFirst = new List<RepairPackageArtifact>();
        if (package.Descriptor.Software is { } spec)
        {
            var (checkedReport, error) = await CheckOnPcAsync(registry, catalog, agentId,
                package.Descriptor.PackageId, cancellationToken);
            if (checkedReport is null) return new(InstallDecision.CheckFailed, null, error, []);
            report = checkedReport;
            if (report.AlreadyInstalled) return new(InstallDecision.AlreadyInstalled, report, null, []);
            if (!report.Met) return new(InstallDecision.RequirementsNotMet, report, null, []);
            if (spec.Install is { Kind: InstallerKinds.Manual })
                return new(InstallDecision.ManualInstall, report, null, []);
            foreach (var id in report.InstallFirst)
            {
                // Only published runtime packages from this catalog; never one the PC named itself.
                var dependency = await files.GetRepairPackageAsync(id, cancellationToken);
                if (dependency?.Descriptor.Software?.Provides is null)
                    return new(InstallDecision.RequirementsNotMet, report with
                    {
                        Met = false,
                        Results = [.. report.Results, new RequirementResult("runtime", false, id, "missing",
                            $"The runtime package {id} is not published.")]
                    }, null, []);
                installFirst.Add(dependency);
            }
        }
        if (!alreadyApproved && await approvals.NeedsApprovalAsync(package, cancellationToken))
            return new(InstallDecision.NeedsApproval, report, null, installFirst);
        return new(InstallDecision.InstallNow, report, null, installFirst);
    }

    /// <summary>The approval card's details: requirement results, runtimes installed first, licence.</summary>
    public static string ApprovalArguments(RepairPackageArtifact package, InstallPlan plan) =>
        JsonSerializer.Serialize(new
        {
            software = package.Descriptor.Software?.Name ?? package.Descriptor.DisplayName,
            version = package.Descriptor.Software?.Version,
            licensed = package.Licensed,
            requirementsMet = plan.Report?.Met,
            requirements = plan.Report?.Results.Select(result => new
            {
                result.Rule, result.Passed, result.Needed, result.Found
            }),
            installFirst = plan.InstallFirst.Select(item => item.Descriptor.DisplayName)
        }, Json);

    /// <summary>Plain reasons for the agent, without paths or identifiers.</summary>
    public static IReadOnlyList<string> Problems(RequirementReport? report) =>
        report?.Problems ?? [];
}
