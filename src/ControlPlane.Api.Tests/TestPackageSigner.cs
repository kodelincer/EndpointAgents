using ControlPlane.Api.Files;
using EndpointAgents.Shared.Office;
using EndpointAgents.Shared.Packages;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ControlPlane.Api.Tests;

/// <summary>Signs test package.json files the way test-bat-playbook.ps1 -Sign does.</summary>
internal static class TestPackageSigner
{
    private static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public static readonly string PublicKey =
        Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());

    /// <summary>Adds a signature over what the folder and package.json describe now.</summary>
    public static void Sign(JsonObject package, string folder, ECDsa? key = null,
        string? publicKey = null)
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var manifest = package.Deserialize<BatPackageManifest>(web)!;
        var id = Path.GetFileName(folder);
        var schema2 = manifest.SchemaVersion == 2;
        var fileName = schema2 ? manifest.File?.FileName : manifest.Bat?.FileName;
        var type = schema2 ? manifest.File?.Type ?? "" : PackageFileTypes.Bat;
        var path = Path.Combine(folder, fileName ?? "");
        if (string.IsNullOrEmpty(fileName) || !File.Exists(path)
            || !PackageFileTypes.IsKnown(type)
            || !BatchPlaybookPolicy.TryNormalize(manifest.Playbook, out var playbook, out _))
            return;
        var sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var support = (manifest.SupportFiles ?? [])
            .Select(entry => new BatchSupportFile(entry.FileName, entry.Sha256, "")).ToArray();
        var publisher = schema2 && !string.IsNullOrWhiteSpace(manifest.File?.Publisher)
            ? manifest.File.Publisher.Trim() : null;
        string statement;
        try
        {
            statement = PackageSignaturePolicy.BuildStatement(id,
                PackageDescriptorPolicy.StagedFileName(id, type), sha256,
                BatchPlaybookPolicy.ComputeSha256(playbook!),
                support.Length == 0 ? null
                    : BatchSupportFilePolicy.ComputeBundleSha256(sha256, support),
                publisher, PackageSignaturePolicy.NormalizeRisks(manifest.Review?.AcceptedRisks));
        }
        catch (ArgumentException) { return; } // Invalid support files are reported first.
        package["signature"] = JsonSerializer.SerializeToNode(
            PackageSignaturePolicy.Sign(statement, key ?? Key, publicKey ?? PublicKey), web);
    }
}
