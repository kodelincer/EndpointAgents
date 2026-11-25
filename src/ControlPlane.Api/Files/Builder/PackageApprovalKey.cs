using EndpointAgents.Shared.Packages;
using Microsoft.Extensions.Options;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace ControlPlane.Api.Files.Builder;

/// <summary>Configuration section <c>PackageApproval</c>.</summary>
public sealed class PackageApprovalOptions
{
    public const string SectionName = "PackageApproval";

    /// <summary>Let an admin approve and sign packages in the dashboard.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Name of the control plane's non-exportable Windows (CNG) signing key.</summary>
    public string KeyName { get; set; } = "EndpointAgents.ControlPlane.PackageApproval";
}

/// <summary>The control plane's own key for packages an admin approves in the dashboard.</summary>
public interface IPackageApprovalKey
{
    /// <summary>Base64 SubjectPublicKeyInfo, or null when approvals are not available.</summary>
    string? PublicKey { get; }

    /// <summary>Why approvals are not available, in plain words.</summary>
    string? Problem { get; }

    PackageSignature Sign(string statement);
}

/// <summary>
/// A non-exportable ECDSA P-256 key kept by Windows in the profile of the account the
/// control plane runs as (LocalSystem for the installed service). It is created on first
/// start. Endpoints trust it only for packages (<c>PackageSigning:ApprovalPublicKeys</c>),
/// never for agent updates.
/// </summary>
public sealed class CngPackageApprovalKey : IPackageApprovalKey
{
    private readonly Lazy<(string? PublicKey, string? Problem)> _state;
    private readonly string _keyName;

    public CngPackageApprovalKey(IOptions<PackageApprovalOptions> options,
        ILogger<CngPackageApprovalKey> logger)
    {
        _keyName = options.Value.KeyName;
        _state = new Lazy<(string?, string?)>(() =>
        {
            if (!options.Value.Enabled)
                return (null, "Approving in the dashboard is switched off (PackageApproval:Enabled).");
            if (!OperatingSystem.IsWindows())
                return (null, "Approving in the dashboard needs the control plane to run on Windows.");
            try
            {
                return (OpenOrCreate(_keyName), null);
            }
            catch (CryptographicException ex)
            {
                logger.LogWarning(ex, "The package approval signing key could not be opened or created.");
                return (null, "The control plane could not open or create its signing key. See its log.");
            }
        });
    }

    public string? PublicKey => _state.Value.PublicKey;
    public string? Problem => _state.Value.Problem;

    public PackageSignature Sign(string statement)
    {
        var publicKey = PublicKey ?? throw new InvalidOperationException(Problem);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var key = new ECDsaCng(CngKey.Open(_keyName));
        return PackageSignaturePolicy.Sign(statement, key, publicKey);
    }

    [SupportedOSPlatform("windows")]
    private static string OpenOrCreate(string name)
    {
        if (!CngKey.Exists(name))
            CngKey.Create(CngAlgorithm.ECDsaP256, name, new CngKeyCreationParameters
            {
                ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.Signing
            }).Dispose();
        using var key = new ECDsaCng(CngKey.Open(name));
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }
}
