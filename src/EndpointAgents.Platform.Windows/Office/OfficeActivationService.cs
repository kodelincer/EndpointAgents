using EndpointAgents.Platform.Windows.Ibsng;
using EndpointAgents.Platform.Windows.UserSessions;
using EndpointAgents.Shared.LocalIpc;
using EndpointAgents.Shared.Office;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Office;

[SupportedOSPlatform("windows")]
public sealed class OfficeActivationService(
    OfficeInventoryCollector inventoryCollector,
    IOfficeUserSessionSource sessionSource,
    ITrayCommandClient trayClient,
    IOfficeVolumeActivationProbe volumeProbe,
    IOfficeActivationCatalogClient catalogClient,
    IOfficeBugfixCatalogClient bugfixCatalogClient)
{
    public async Task<OfficeActivationStatusSnapshot> GetStatusAsync(string requestId,
        CancellationToken cancellationToken, string? userSid = null)
    {
        var inventorySnapshot = inventoryCollector.Collect(cancellationToken);
        var inventory = inventorySnapshot.Inventory;
        var issues = inventorySnapshot.Issues.ToList();
        var families = OfficeProductFamilyDetector.Detect(inventory).ToList();
        var probes = new List<OfficeActivationProbeResult>();

        if (inventory.Installed)
        {
            var probeUnknown = families.Contains(OfficeProductFamilies.Unknown,
                StringComparer.OrdinalIgnoreCase);
            if (probeUnknown || families.Contains(OfficeProductFamilies.Microsoft365Apps,
                StringComparer.OrdinalIgnoreCase))
                probes.Add(await ProbeVNextAsync(
                    requestId, issues, cancellationToken, userSid));

            if (probeUnknown || families.Contains(OfficeProductFamilies.VolumeLicensed,
                StringComparer.OrdinalIgnoreCase))
                probes.Add(await volumeProbe.ProbeAsync(inventory, cancellationToken));
        }

        var products = probes.SelectMany(probe => probe.Products).ToArray();
        RefineFamilies(families, products);
        var overallState = GetOverallState(products, probes, inventory.Installed);

        IReadOnlyList<OfficeRemediationArtifactDescriptor> artifacts = [];
        try
        {
            artifacts = await catalogClient.GetArtifactsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            issues.Add(new OfficeInventoryIssue("office.remediation_catalog",
                "The approved Office remediation catalog could not be read.",
                ex.GetType().Name));
        }

        IReadOnlyList<OfficeBugfixArtifactDescriptor> bugfixes = [];
        try
        {
            bugfixes = await bugfixCatalogClient.GetArtifactsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            issues.Add(new OfficeInventoryIssue("office.bugfix_catalog",
                "The approved Office repair catalog could not be read.",
                ex.GetType().Name));
        }

        var recommendation = OfficeActivationRecommendation.Evaluate(overallState,
            inventory.Version, families, products, artifacts);
        var compatibleBugfixIds = Version.TryParse(inventory.Version,
                out var officeVersion)
            ? bugfixes.Where(item => OfficeBugfixCommandHandler.IsCompatible(
                    item, officeVersion))
                .Select(item => item.ArtifactId)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
        var mechanisms = products.Select(product => product.Source)
            .Concat(probes.Where(probe => probe.ToolAvailable).Select(probe => probe.Mechanism))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var report = new OfficeActivationReport(inventory.Installed, inventory.Version,
            inventory.Architecture, families, mechanisms, overallState, products, probes,
            recommendation.Action, recommendation.ArtifactIds,
            compatibleBugfixIds,
            recommendation.RequiresUserInteraction);
        return new OfficeActivationStatusSnapshot(report, issues, artifacts);
    }

    private async Task<OfficeActivationProbeResult> ProbeVNextAsync(string requestId,
        List<OfficeInventoryIssue> issues, CancellationToken cancellationToken,
        string? userSid)
    {
        var session = sessionSource.GetActiveSession(userSid);
        if (session is null)
        {
            issues.Add(new OfficeInventoryIssue("office.activation.vnext",
                "Microsoft 365 activation requires an active user session.",
                "UserContextUnavailable"));
            return new OfficeActivationProbeResult(OfficeActivationMechanisms.VNext,
                false, false, [], "UserContextUnavailable");
        }

        var response = await trayClient.SendAsync(session.SessionId,
            new TrayIpcRequest(requestId, TrayIpcProtocol.OfficeActivationStatusMethod,
                string.Empty, string.Empty, null), cancellationToken);
        if (response.OfficeActivation is not null)
            return response.OfficeActivation;

        issues.Add(new OfficeInventoryIssue("office.activation.vnext",
            "The active user's Tray could not inspect Microsoft 365 activation.",
            response.State));
        return new OfficeActivationProbeResult(OfficeActivationMechanisms.VNext,
            false, false, [], response.State);
    }

    private static void RefineFamilies(List<string> families,
        IReadOnlyList<OfficeActivationProduct> products)
    {
        if (products.Any(product => product.Source == OfficeActivationMechanisms.VNext))
            AddFamily(families, OfficeProductFamilies.Microsoft365Apps);
        if (products.Any(product => product.Source == OfficeActivationMechanisms.Ospp))
            AddFamily(families, OfficeProductFamilies.VolumeLicensed);
        if (families.Count > 1)
            families.RemoveAll(family => family == OfficeProductFamilies.Unknown);
    }

    private static void AddFamily(List<string> families, string family)
    {
        if (!families.Contains(family, StringComparer.OrdinalIgnoreCase))
            families.Add(family);
    }

    internal static string GetOverallState(IReadOnlyList<OfficeActivationProduct> products,
        IReadOnlyList<OfficeActivationProbeResult> probes, bool officeInstalled)
    {
        string[] precedence =
        [
            OfficeActivationStates.Error,
            OfficeActivationStates.Unlicensed,
            OfficeActivationStates.Notification,
            OfficeActivationStates.Grace,
            OfficeActivationStates.Unknown,
            OfficeActivationStates.Licensed
        ];
        foreach (var state in precedence)
            if (products.Any(product => product.State == state))
                return state;

        if (!officeInstalled)
            return OfficeActivationStates.Unknown;
        if (probes.Any(probe => probe.ToolAvailable && !probe.Succeeded))
            return OfficeActivationStates.Error;
        if (probes.Count > 0 && probes.All(probe => !probe.ToolAvailable))
            return OfficeActivationStates.ToolUnavailable;
        return OfficeActivationStates.Unknown;
    }
}
