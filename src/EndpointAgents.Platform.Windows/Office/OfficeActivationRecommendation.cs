using EndpointAgents.Shared.Office;

namespace EndpointAgents.Platform.Windows.Office;

public sealed record OfficeActivationRecommendationResult(
    string Action,
    IReadOnlyList<string> ArtifactIds,
    bool RequiresUserInteraction);

public static class OfficeActivationRecommendation
{
    public static OfficeActivationRecommendationResult Evaluate(string overallState,
        string? officeVersion, IReadOnlyList<string> families,
        IReadOnlyList<OfficeActivationProduct> products,
        IReadOnlyList<OfficeRemediationArtifactDescriptor> artifacts)
    {
        if (overallState == OfficeActivationStates.Licensed)
            return new OfficeActivationRecommendationResult(
                OfficeActivationActions.NoAction, [], false);

        var states = products.Select(product => product.State)
            .Append(overallState).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var errors = products.Select(product => product.ErrorCode)
            .Where(error => !string.IsNullOrWhiteSpace(error)).Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var compatible = artifacts.Where(artifact =>
                MatchesFamily(artifact, families)
                && MatchesVersion(artifact, officeVersion)
                && artifact.ActivationStates.Any(states.Contains)
                && (artifact.ErrorCodes.Count == 0 || artifact.ErrorCodes.Any(errors.Contains)))
            .OrderBy(artifact => artifact.ArtifactId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (compatible.Length == 1)
            return new OfficeActivationRecommendationResult(
                OfficeActivationActions.ApprovedArtifactAvailable,
                [compatible[0].ArtifactId], compatible[0].RequiresInteractiveUser);
        if (compatible.Length > 1)
            return new OfficeActivationRecommendationResult(
                OfficeActivationActions.OperatorSelectionRequired,
                compatible.Select(item => item.ArtifactId).ToArray(),
                compatible.Any(item => item.RequiresInteractiveUser));

        var vNextProblem = products.Any(product =>
            product.Source == OfficeActivationMechanisms.VNext
            && product.State != OfficeActivationStates.Licensed);
        if (vNextProblem)
            return new OfficeActivationRecommendationResult(
                OfficeActivationActions.UserSignInOrLicenseRequired, [], true);

        var volumeProblem = products.Any(product =>
            product.Source == OfficeActivationMechanisms.Ospp
            && product.State != OfficeActivationStates.Licensed);
        if (volumeProblem)
            return new OfficeActivationRecommendationResult(
                OfficeActivationActions.CheckVolumeActivationInfrastructure, [], false);

        return new OfficeActivationRecommendationResult(
            OfficeActivationActions.ManualReview, [], false);
    }

    private static bool MatchesFamily(OfficeRemediationArtifactDescriptor artifact,
        IReadOnlyList<string> families) => artifact.SupportedProductFamilies
            .Any(candidate => families.Contains(candidate, StringComparer.OrdinalIgnoreCase));

    private static bool MatchesVersion(OfficeRemediationArtifactDescriptor artifact,
        string? versionText)
    {
        if (artifact.MinOfficeVersion is null && artifact.MaxOfficeVersion is null)
            return true;
        if (!Version.TryParse(versionText, out var version))
            return false;
        if (Version.TryParse(artifact.MinOfficeVersion, out var minimum) && version < minimum)
            return false;
        if (Version.TryParse(artifact.MaxOfficeVersion, out var maximum) && version > maximum)
            return false;
        return true;
    }
}
