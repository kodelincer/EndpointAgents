using System.Security.Cryptography;
using System.Text;

namespace EndpointAgents.Shared.Packages;

/// <summary>An administrator's signature over one package revision.</summary>
/// <param name="KeyId">First 16 hex characters of the SHA-256 of the signing public key.</param>
/// <param name="Value">Base64 ECDSA P-256 / SHA-256 signature (IEEE P1363 format).</param>
public sealed record PackageSignature(string KeyId, string Value);

/// <summary>
/// Signs and verifies the statement that identifies one package revision: its id, the
/// name it is staged under, the file, playbook and supporting-file hashes, and the EXE
/// publisher pin. The private key stays on the administrator's PC, so neither the
/// control plane nor anything between it and the endpoint can approve a changed file.
/// </summary>
public static class PackageSignaturePolicy
{
    private const string StatementHeader = "EndpointAgents.Package.v1";

    /// <param name="acceptedRisks">
    /// Lint risks the reviewer accepted for this package. Appended to the signed text only
    /// when non-empty, so a package that accepts none keeps the original statement (and its
    /// signature stays valid). A reviewer cannot change which risks a package accepts
    /// without signing again.
    /// </param>
    public static string BuildStatement(string packageId, string fileName, string sha256,
        string? playbookSha256, string? bundleSha256, string? publisher,
        IReadOnlyCollection<string>? acceptedRisks = null, string? softwareSha256 = null)
    {
        var statement = string.Join('\n', StatementHeader, packageId,
            fileName.ToLowerInvariant(), sha256.ToUpperInvariant(),
            (playbookSha256 ?? "-").ToUpperInvariant(),
            (bundleSha256 ?? "-").ToUpperInvariant(), publisher ?? "-");
        var risks = NormalizeRisks(acceptedRisks);
        if (risks.Count > 0) statement += "\nrisks:" + string.Join(',', risks);
        // Like risks, only added when present, so older packages keep their signatures.
        if (softwareSha256 is not null) statement += "\nsoftware:" + softwareSha256.ToUpperInvariant();
        return statement;
    }

    /// <summary>Accepted risks, lower-cased, de-duplicated and ordered, for a stable statement.</summary>
    public static IReadOnlyList<string> NormalizeRisks(IReadOnlyCollection<string>? risks) =>
        risks is null ? []
            : risks.Select(risk => risk.Trim().ToLowerInvariant())
                .Where(risk => risk.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(risk => risk, StringComparer.Ordinal)
                .ToArray();

    /// <summary>The key id of a base64 SubjectPublicKeyInfo public key.</summary>
    public static string GetKeyId(string publicKeyBase64) =>
        Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(publicKeyBase64.Trim())))[..16];

    public static PackageSignature Sign(string statement, ECDsa privateKey,
        string publicKeyBase64) =>
        new(GetKeyId(publicKeyBase64), Convert.ToBase64String(privateKey.SignData(
            Encoding.UTF8.GetBytes(statement), HashAlgorithmName.SHA256)));

    /// <summary>
    /// True only when <paramref name="signature"/> was made over <paramref name="statement"/>
    /// by one of <paramref name="trustedPublicKeys"/> (base64 SubjectPublicKeyInfo).
    /// </summary>
    public static bool Verify(string statement, PackageSignature? signature,
        IEnumerable<string>? trustedPublicKeys)
    {
        if (signature is null || string.IsNullOrWhiteSpace(signature.Value)
            || trustedPublicKeys is null)
            return false;
        byte[] signatureBytes;
        try { signatureBytes = Convert.FromBase64String(signature.Value.Trim()); }
        catch (FormatException) { return false; }
        var data = Encoding.UTF8.GetBytes(statement);
        foreach (var publicKey in trustedPublicKeys)
        {
            if (string.IsNullOrWhiteSpace(publicKey)) continue;
            try
            {
                if (!GetKeyId(publicKey).Equals(signature.KeyId?.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                using var key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey.Trim()), out _);
                if (key.VerifyData(data, signatureBytes, HashAlgorithmName.SHA256))
                    return true;
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException) { }
        }
        return false;
    }

    public static bool Verify(PackageDescriptor descriptor,
        IEnumerable<string>? trustedPublicKeys) =>
        Verify(BuildStatement(descriptor.PackageId, descriptor.FileName, descriptor.Sha256,
                descriptor.PlaybookSha256, descriptor.BundleSha256, descriptor.Publisher,
                descriptor.AcceptedRisks, descriptor.SoftwareSha256),
            descriptor.Signature, trustedPublicKeys);
}
