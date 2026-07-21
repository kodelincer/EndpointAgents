using System.IO.Enumeration;

namespace EndpointAgents.Platform.Windows.Ibsng;

public sealed record IbsngDesktopCandidate(
    string Path,
    bool IsCanonical,
    bool NameMatches,
    IbsngArtifactInspection Artifact);

public sealed record IbsngDesktopInventory(
    string DesktopPath,
    string CanonicalPath,
    IbsngArtifactInspection CanonicalArtifact,
    IReadOnlyList<IbsngDesktopCandidate> Candidates,
    IReadOnlyList<IbsngDesktopCandidate> Duplicates);

public sealed record IbsngDesktopReconciliation(
    IbsngArtifactInspection CanonicalArtifact,
    IReadOnlyList<string> QuarantinedPaths);

/// <summary>Discovers and removes duplicate IBSng executables from one desktop.</summary>
public sealed class IbsngDesktopReconciler
{
    private readonly IbsngArtifactVerifier _verifier;
    private readonly IbsngArtifactQuarantine _quarantine;

    public IbsngDesktopReconciler(IbsngArtifactVerifier verifier,
        IbsngArtifactQuarantine quarantine)
    {
        _verifier = verifier;
        _quarantine = quarantine;
    }

    public async Task<IbsngDesktopInventory> InspectAsync(string desktopPath,
        IbsngOptions options, CancellationToken cancellationToken)
    {
        var canonicalPath = Path.Combine(desktopPath, options.DialerFileName);
        var canonicalFullPath = Path.GetFullPath(canonicalPath);
        var canonical = await _verifier.InspectAsync(canonicalFullPath,
            options.TrustedSha256, cancellationToken);
        var candidates = new List<IbsngDesktopCandidate>();

        foreach (var path in Directory.EnumerateFiles(desktopPath, "*.exe",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(path);
            var isCanonical = fullPath.Equals(canonicalFullPath,
                StringComparison.OrdinalIgnoreCase);
            var nameMatches = FileSystemName.MatchesSimpleExpression(
                options.DialerSearchPattern, Path.GetFileName(fullPath), ignoreCase: true);
            var artifact = isCanonical
                ? canonical
                : await _verifier.InspectAsync(fullPath, options.TrustedSha256,
                    cancellationToken);

            if (isCanonical || nameMatches || artifact.IsTrusted)
                candidates.Add(new IbsngDesktopCandidate(fullPath, isCanonical,
                    nameMatches, artifact));
        }

        var duplicates = candidates.Where(candidate => !candidate.IsCanonical).ToArray();
        return new IbsngDesktopInventory(desktopPath, canonicalFullPath, canonical,
            candidates, duplicates);
    }

    public async Task<IbsngDesktopReconciliation> ReconcileAsync(string desktopPath,
        IbsngOptions options, CancellationToken cancellationToken)
    {
        var inventory = await InspectAsync(desktopPath, options, cancellationToken);
        var quarantined = new List<string>();

        foreach (var candidate in inventory.Candidates)
        {
            if (candidate.IsCanonical && candidate.Artifact.IsTrusted)
                continue;

            cancellationToken.ThrowIfCancellationRequested();
            quarantined.Add(_quarantine.Move(candidate.Path, options.QuarantineRoot));
        }

        var canonical = await _verifier.InspectAsync(inventory.CanonicalPath,
            options.TrustedSha256, cancellationToken);
        return new IbsngDesktopReconciliation(canonical, quarantined);
    }
}
