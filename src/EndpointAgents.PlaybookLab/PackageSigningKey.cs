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
        string? softwareSha256 = null;
        if (manifest.SchemaVersion == 2 && (manifest.Software is not null
                || stagedName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                || stagedName.EndsWith(".msix", StringComparison.OrdinalIgnoreCase)
                || stagedName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
        {
            if (!SoftwareSpecPolicy.TryNormalize(manifest.Software,
                    Path.GetExtension(stagedName).TrimStart('.').ToLowerInvariant(),
                    out var software, out var softwareError))
            {
                Console.WriteLine($"Cannot sign: software: {softwareError}");
                return false;
            }
            softwareSha256 = SoftwareSpecPolicy.ComputeSha256(software!);
            Console.WriteLine($"Signing the software section ({software!.Install?.Kind ?? "requirements only"}).");
        }
        var statement = PackageSignaturePolicy.BuildStatement(id, stagedName, sha256,
            BatchPlaybookPolicy.ComputeSha256(playbook!),
            supportFiles.Length == 0 ? null
                : BatchSupportFilePolicy.ComputeBundleSha256(sha256, supportFiles),
            publisher, acceptedRisks, softwareSha256);

        using var key = new ECDsaCng(CngKey.Open(name));
        var signature = PackageSignaturePolicy.Sign(statement, key, publicKey);
        node["signature"] = JsonSerializer.SerializeToNode(signature, json);
        await File.WriteAllTextAsync(manifestPath, node.ToJsonString(json) + Environment.NewLine);
        Console.WriteLine($"Signed {id} with key {signature.KeyId}.");
        return true;
    }

    /// <summary>
    /// Signs an agent update manifest written by scripts\new-agent-update.ps1, after checking
    /// that the zip next to it is exactly the one the manifest describes.
    /// </summary>
    public static async Task<bool> SignAgentUpdateAsync(string manifestPath, string name)
    {
        var publicKey = TryGetPublicKey(name);
        if (publicKey is null)
        {
            Console.WriteLine($"No signing key named '{name}' on this PC. Create one with scripts\\new-package-signing-key.ps1.");
            return false;
        }
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var manifest = JsonSerializer.Deserialize<EndpointAgents.Shared.Updates.AgentUpdateManifest>(
            await File.ReadAllTextAsync(manifestPath), json);
        if (!EndpointAgents.Shared.Updates.AgentUpdatePolicy.TryValidate(manifest, out var error))
        {
            Console.WriteLine($"Cannot sign: {error}");
            return false;
        }
        var zip = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, manifest!.FileName);
        if (!File.Exists(zip) || new FileInfo(zip).Length != manifest.SizeBytes)
        {
            Console.WriteLine($"Cannot sign: {manifest.FileName} is missing or its size differs from the manifest.");
            return false;
        }
        await using (var stream = File.OpenRead(zip))
            if (!Convert.ToHexString(await SHA256.HashDataAsync(stream)).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Cannot sign: the zip does not match the manifest's SHA-256.");
                return false;
            }
        using var key = new ECDsaCng(CngKey.Open(name));
        var signature = PackageSignaturePolicy.Sign(
            EndpointAgents.Shared.Updates.AgentUpdatePolicy.BuildStatement(manifest), key, publicKey);
        await File.WriteAllTextAsync(manifestPath,
            JsonSerializer.Serialize(manifest with { Signature = signature }, json) + Environment.NewLine);
        Console.WriteLine($"Signed agent update {manifest.Version} with key {signature.KeyId}.");
        return true;
    }
}
