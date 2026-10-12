using EndpointAgents.Shared.Office;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Office;

[SupportedOSPlatform("windows")]
public sealed class OfficeActivationService(
    OfficeInventoryCollector inventoryCollector,
    IOfficeVolumeActivationProbe volumeProbe,
    IOfficeBugfixCatalogClient bugfixCatalogClient)
{
    public async Task<OfficeActivationStatusSnapshot> GetStatusAsync(string requestId,
        CancellationToken cancellationToken, string? userSid = null)
    {
        var inventorySnapshot = inventoryCollector.Collect(cancellationToken);
        var inventory = inventorySnapshot.Inventory;
        var issues = inventorySnapshot.Issues.ToList();
        IReadOnlyList<string> families = inventory.Installed
            ? [OfficeProductFamilies.VolumeLicensed]
            : [OfficeProductFamilies.Unknown];
        var probes = new List<OfficeActivationProbeResult>();
        OfficeActivationProbeResult? volumeResult = null;

        if (inventory.Installed)
        {
            volumeResult = await volumeProbe.ProbeAsync(inventory, cancellationToken);
            probes.Add(volumeResult);
        }

        var products = probes.SelectMany(probe => probe.Products).ToArray();
        // Editions without an installed key are listed but do not decide the state;
        // when no edition has a key at all, Office is simply not activated.
        var keyed = OfficeActivationRecommendation.WithInstalledKey(products).ToArray();
        var overallState = keyed.Length == 0 && products.Length > 0
            ? OfficeActivationStates.Unlicensed
            : GetOverallState(keyed, probes, inventory.Installed);
        var recommendation = OfficeActivationRecommendation.Evaluate(volumeResult);
        var kmsHealthy = volumeResult is not null
            && OfficeActivationRecommendation.IsHealthyKms(volumeResult);
        IReadOnlyList<string> compatibleBugfixIds = [];

        if (!kmsHealthy && Version.TryParse(inventory.Version, out _))
        {
            try
            {
                var bugfixes = await bugfixCatalogClient.GetArtifactsAsync(
                    cancellationToken);
                compatibleBugfixIds = bugfixes.Where(item =>
                        OfficeBugfixCommandHandler.IsCompatible(item, inventory,
                            Environment.OSVersion.Version))
                    // Catalog order is the administrator's try order.
                    .Select(item => item.ArtifactId)
                    .ToArray();
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
        }

        var mechanisms = probes.Select(probe => probe.Mechanism)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var report = new OfficeActivationReport(inventory.Installed, inventory.Version,
            inventory.Architecture, families, mechanisms, overallState, products, probes,
            recommendation.Action, [],
            compatibleBugfixIds,
            recommendation.RequiresUserInteraction);
        return new OfficeActivationStatusSnapshot(report, issues, []);
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
