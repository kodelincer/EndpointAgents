using EndpointAgents.Shared.Windows;

namespace EndpointAgents.Platform.Windows.Windows;

public static class WindowsBugfixCompatibility
{
    public static IReadOnlyList<string> SelectCompatibleArtifactIds(
        IReadOnlyList<WindowsBugfixArtifactDescriptor> catalog,
        WindowsActivationReport report, Version windowsVersion)
    {
        var versioned = catalog.Where(item => item.MinWindowsVersion is not null)
            .ToArray();
        return versioned.Length > 0
            ? versioned.Where(item => IsCompatible(item, report, windowsVersion))
                .Select(item => item.ArtifactId).ToArray()
            : catalog.Where(item => item.ArtifactId
                    == WindowsRemediationPaths.ArtifactId)
                .Select(item => item.ArtifactId).ToArray();
    }

    public static bool IsCompatible(WindowsBugfixArtifactDescriptor artifact,
        WindowsActivationReport report, Version windowsVersion)
    {
        if (artifact.ArtifactId == WindowsHelloSamplePaths.ArtifactId)
            return false;
        if (artifact.MinWindowsVersion is null
            && artifact.MaxWindowsVersionExclusive is null)
            return artifact.ArtifactId == WindowsRemediationPaths.ArtifactId;
        var primary = new WindowsBugfixCompatibilityRange(
            artifact.MinWindowsVersion!, artifact.MaxWindowsVersionExclusive!,
            artifact.EditionIds ?? []);
        return IsCompatible(primary, report, windowsVersion)
            || artifact.AdditionalCompatibility?.Any(rule =>
                rule is not null && IsCompatible(rule, report,
                    windowsVersion)) == true;
    }

    private static bool IsCompatible(WindowsBugfixCompatibilityRange rule,
        WindowsActivationReport report, Version windowsVersion)
    {
        if (!Version.TryParse(rule.MinWindowsVersion, out var minimum)
            || !Version.TryParse(rule.MaxWindowsVersionExclusive,
                out var maximum)
            || windowsVersion < minimum || windowsVersion >= maximum)
            return false;
        return rule.EditionIds is not { Count: > 0 } editions
            || report.EditionId is not null
            && editions.Contains(report.EditionId,
                StringComparer.OrdinalIgnoreCase);
    }
}
