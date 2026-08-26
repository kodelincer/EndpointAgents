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

    public static bool IsHealthyKms(OfficeActivationProbeResult probe) =>
        probe.Mechanism.Equals(OfficeActivationMechanisms.Ospp,
            StringComparison.OrdinalIgnoreCase)
        && probe.ToolAvailable
        && probe.Succeeded
        && probe.Products.Count > 0
        && probe.Products.All(product =>
            product.Source.Equals(OfficeActivationMechanisms.Ospp,
                StringComparison.OrdinalIgnoreCase)
            && product.LicenseType?.Equals(OfficeLicenseTypes.VolumeKms,
                StringComparison.OrdinalIgnoreCase) == true
            && product.State.Equals(OfficeActivationStates.Licensed,
                StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(product.ErrorCode));
}
