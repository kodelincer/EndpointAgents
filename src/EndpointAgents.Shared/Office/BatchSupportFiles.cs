using System.Security.Cryptography;
using System.Text;

namespace EndpointAgents.Shared.Office;

public sealed record BatchSupportFile(string FileName, string Sha256,
    string DownloadPath);

public static class BatchSupportFilePolicy
{
    public static bool IsValidName(string? name) => name is { Length: >= 1 and <= 128 }
        && name is not "." and not ".."
        && name.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '.' or '_' or '-');

    public static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(Uri.IsHexDigit);

    public static bool IsValidBundle(string batSha256,
        IReadOnlyList<BatchSupportFile>? files, string? bundleSha256,
        string downloadPrefix)
    {
        if (files is not { Count: > 0 }) return bundleSha256 is null;
        if (files.Any(file => file is null
                || file.DownloadPath != $"{downloadPrefix}/{file.FileName}"))
            return false;
        try
        {
            return IsSha256(bundleSha256)
                && ComputeBundleSha256(batSha256, files).Equals(
                    bundleSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    public static string ComputeBundleSha256(string batSha256,
        IReadOnlyList<BatchSupportFile> files)
    {
        if (!IsSha256(batSha256) || files is null
            || files.Count is < 1 or > 32
            || files.Any(file => file is null || !IsValidName(file.FileName)
                || !IsSha256(file.Sha256))
            || files.Select(file => file.FileName)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
            throw new ArgumentException("The batch support files are invalid.");

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false),
                   leaveOpen: true))
        {
            writer.Write(batSha256.ToUpperInvariant());
            foreach (var file in files.OrderBy(item => item.FileName,
                         StringComparer.OrdinalIgnoreCase))
            {
                writer.Write(file.FileName.ToLowerInvariant());
                writer.Write(file.Sha256.ToUpperInvariant());
            }
        }
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer()
            .AsSpan(0, checked((int)stream.Length))));
    }
}
