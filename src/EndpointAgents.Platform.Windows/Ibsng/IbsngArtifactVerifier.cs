using System.Security.Cryptography;

namespace EndpointAgents.Platform.Windows.Ibsng;

public sealed record IbsngArtifactInspection(
    string Path,
    bool Exists,
    long? SizeBytes,
    string? Sha256,
    bool IsPortableExecutable,
    bool IsTrusted)
{
    public string State => !Exists
        ? "Missing"
        : !IsPortableExecutable
            ? "InvalidExecutable"
            : IsTrusted ? "Approved" : "Untrusted";
}

public sealed class IbsngArtifactVerifier
{
    public async Task<IbsngArtifactInspection> InspectAsync(
        string path,
        string? trustedSha256,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return new IbsngArtifactInspection(path, false, null, null, false, false);

        var file = new FileInfo(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var signature = new byte[2];
        var signatureBytes = await stream.ReadAsync(signature, cancellationToken);
        var isPortableExecutable = signatureBytes == 2
            && signature[0] == (byte)'M'
            && signature[1] == (byte)'Z';

        stream.Position = 0;
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        var actualHash = Convert.ToHexString(hash);
        var trustedHash = NormalizeSha256(trustedSha256);

        return new IbsngArtifactInspection(path, true, file.Length, actualHash,
            isPortableExecutable, isPortableExecutable && trustedHash is not null
                && actualHash.Equals(trustedHash, StringComparison.OrdinalIgnoreCase));
    }

    public static string? NormalizeSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim().Replace("-", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
        return normalized.Length == 64 && normalized.All(Uri.IsHexDigit) ? normalized : null;
    }
}
