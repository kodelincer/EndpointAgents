using ControlPlane.Api.Files;
using EndpointAgents.Shared.Office;
using EndpointAgents.Shared.Packages;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// The administrator's package signing key: a non-exportable ECDSA P-256 key kept by
/// Windows in the current user's profile on the admin PC. Only its public key leaves
/// the PC (into the control plane and endpoint configuration).
/// </summary>
static class PackageSigningKey
{
    public const string DefaultName = "EndpointAgents.PackageSigning";

    public static string? TryGetPublicKey(string name)
    {
        if (!CngKey.Exists(name)) return null;
        using var key = new ECDsaCng(CngKey.Open(name));
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    public static int Create(string name)
    {
        if (CngKey.Exists(name))
        {
            Console.WriteLine($"A signing key named '{name}' already exists on this PC.");
            PrintPublicKey(TryGetPublicKey(name)!);
            return 1;
        }
        using var key = CngKey.Create(CngAlgorithm.ECDsaP256, name,
            new CngKeyCreationParameters
            {
                ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.Signing
            });
        Console.WriteLine($"Created the package signing key '{name}' for {Environment.UserName}.");
        Console.WriteLine("The private key cannot be copied off this PC. If it is lost, create a new");
        Console.WriteLine("key, trust its public key too, and sign the packages again.");
        PrintPublicKey(TryGetPublicKey(name)!);
        return 0;
    }

    public static void PrintPublicKey(string publicKey)
    {
        Console.WriteLine();
        Console.WriteLine($"Key id:     {PackageSignaturePolicy.GetKeyId(publicKey)}");
        Console.WriteLine($"Public key: {publicKey}");
        Console.WriteLine();
        Console.WriteLine("Trust it by adding the public key to:");
        Console.WriteLine("  control plane appsettings.json  FileRepository:TrustedPackageSigningKeys");
        Console.WriteLine("  endpoint appsettings.json       PackageSigning:TrustedPublicKeys");
    }

    /// <summary>Signs the revision package.json describes now and writes "signature".</summary>
    public static async Task<bool> SignAsync(string folder, string manifestPath, string name,
        JsonSerializerOptions json)
    {
        var publicKey = TryGetPublicKey(name);
        if (publicKey is null)
        {
            Console.WriteLine($"No signing key named '{name}' on this PC. Create one with scripts\\new-package-signing-key.ps1.");
            return false;
        }
        var node = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        var manifest = node.Deserialize<BatPackageManifest>(json)!;
        var id = Path.GetFileName(folder);
        string fileName, stagedName;
        string? publisher = null;
        if (manifest.SchemaVersion == 2)
        {
            var type = manifest.File?.Type?.Trim().ToLowerInvariant() ?? string.Empty;
            fileName = manifest.File?.FileName ?? string.Empty;
            if (!PackageFileTypes.IsKnown(type))
            {
                Console.WriteLine("Cannot sign: file.type must be bat or exe.");
                return false;
            }
            stagedName = PackageDescriptorPolicy.StagedFileName(id, type);
            publisher = string.IsNullOrWhiteSpace(manifest.File?.Publisher) ? null
                : manifest.File.Publisher.Trim();
        }
        else
        {
            fileName = manifest.Bat?.FileName ?? string.Empty;
            stagedName = $"{id}.bat";
        }
        var filePath = Path.Combine(folder, fileName);
        if (fileName.Length == 0 || !File.Exists(filePath)
            || !BatchPlaybookPolicy.TryNormalize(manifest.Playbook, out var playbook, out _))
        {
            Console.WriteLine("Cannot sign: the file is missing or the playbook is invalid.");
            return false;
        }
        var sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(filePath)));
        var supportFiles = (manifest.SupportFiles ?? [])
            .Select(entry => new BatchSupportFile(entry.FileName, entry.Sha256, string.Empty))
            .ToArray();
        var acceptedRisks = PackageSignaturePolicy.NormalizeRisks(
            manifest.Review?.AcceptedRisks);
        var unknownRisk = acceptedRisks.FirstOrDefault(risk => !BatRisks.IsAcceptable(risk));
        if (unknownRisk is not null)
        {
            Console.WriteLine($"Cannot sign: review.acceptedRisks may only contain "
                + $"{string.Join(" or ", BatRisks.Acceptable)}; '{unknownRisk}' is not allowed.");
            return false;
        }
        if (acceptedRisks.Count > 0)
            Console.WriteLine($"Signing with accepted risks: {string.Join(", ", acceptedRisks)}.");
        var statement = PackageSignaturePolicy.BuildStatement(id, stagedName, sha256,
            BatchPlaybookPolicy.ComputeSha256(playbook!),
            supportFiles.Length == 0 ? null
                : BatchSupportFilePolicy.ComputeBundleSha256(sha256, supportFiles),
            publisher, acceptedRisks);

        using var key = new ECDsaCng(CngKey.Open(name));
        var signature = PackageSignaturePolicy.Sign(statement, key, publicKey);
        node["signature"] = JsonSerializer.SerializeToNode(signature, json);
        await File.WriteAllTextAsync(manifestPath, node.ToJsonString(json) + Environment.NewLine);
        Console.WriteLine($"Signed {id} with key {signature.KeyId}.");
        return true;
    }
}
