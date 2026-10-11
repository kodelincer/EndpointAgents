using EndpointAgents.Shared.Office;

namespace EndpointAgents.Platform.Windows.Office;

public sealed record OfficeActivationRecommendationResult(
    string Action,
    IReadOnlyList<string> ArtifactIds,
    bool RequiresUserInteraction);

public static class OfficeActivationRecommendation
{
    public static OfficeActivationRecommendationResult Evaluate(
        OfficeActivationProbeResult? probe)
    {
        if (probe is not null && IsHealthyKms(probe))
            return new OfficeActivationRecommendationResult(
                OfficeActivationActions.NoAction, [], false);

        if (probe?.Products.Count > 0)
            return new OfficeActivationRecommendationResult(
                OfficeActivationActions.CheckVolumeActivationInfrastructure, [], false);

        return new OfficeActivationRecommendationResult(
            OfficeActivationActions.ManualReview, [], false);
    }

    /// <summary>
    /// 0xC004F014: no product key is installed for this edition. Volume installs ship
    /// license definitions for several editions and only one gets a key, so the others
    /// report this error without anything being wrong.
    /// </summary>
    public const string ProductKeyNotInstalled = "0XC004F014";

    /// <summary>Editions that actually have a product key installed.</summary>
    public static IEnumerable<OfficeActivationProduct> WithInstalledKey(
        IEnumerable<OfficeActivationProduct> products) =>
        products.Where(product => !string.Equals(product.ErrorCode?.Trim(),
            ProductKeyNotInstalled, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Healthy when every edition with an installed key is KMS-activated with no error,
    /// and at least one such edition exists.
    /// </summary>
    public static bool IsHealthyKms(OfficeActivationProbeResult probe) =>
        probe.Mechanism.Equals(OfficeActivationMechanisms.Ospp,
            StringComparison.OrdinalIgnoreCase)
        && probe.ToolAvailable
        && probe.Succeeded
        && WithInstalledKey(probe.Products).Any()
        && WithInstalledKey(probe.Products).All(product =>
            product.Source.Equals(OfficeActivationMechanisms.Ospp,
                StringComparison.OrdinalIgnoreCase)
            && product.LicenseType?.Equals(OfficeLicenseTypes.VolumeKms,
                StringComparison.OrdinalIgnoreCase) == true
            && product.State.Equals(OfficeActivationStates.Licensed,
                StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(product.ErrorCode));
}
