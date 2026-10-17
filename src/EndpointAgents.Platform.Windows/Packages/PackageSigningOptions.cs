using EndpointAgents.Shared.Packages;

namespace EndpointAgents.Platform.Windows.Packages;

/// <summary>
/// Administrator public keys the endpoint trusts for package signatures
/// (configuration section <c>PackageSigning</c>).
/// </summary>
public sealed class PackageSigningOptions
{
    public const string SectionName = "PackageSigning";

    /// <summary>Base64 SubjectPublicKeyInfo keys from scripts\new-package-signing-key.ps1.</summary>
    public List<string> TrustedPublicKeys { get; set; } = [];

    /// <summary>
    /// Also require signatures on Office and Windows activation repairs. Turn it on once
    /// every activation repair is a signed package (legacy manifests cannot be signed).
    /// General repair packages always require a signature.
    /// </summary>
    public bool RequireForActivationRepairs { get; set; }

    public bool IsTrusted(string packageId, string fileName, string sha256,
        string? playbookSha256, string? bundleSha256, string? publisher,
        PackageSignature? signature, IReadOnlyCollection<string>? acceptedRisks = null) =>
        PackageSignaturePolicy.Verify(PackageSignaturePolicy.BuildStatement(packageId,
            fileName, sha256, playbookSha256, bundleSha256, publisher, acceptedRisks),
            signature, TrustedPublicKeys);
}
