using EndpointAgents.Platform.Windows.Actions.Infrastructure;
using EndpointAgents.Shared.Actions;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Actions.SystemSide;

/// <summary>One Windows or Office license as the Software Protection service reports it.</summary>
public sealed record KmsProductState(string Id, string Name, bool VolumeClient, bool Licensed,
    int LicenseStatus, bool UsesOrganisationKms, string? ErrorCode);

/// <summary>
/// Windows and Office (2013 and later, Click-to-Run included) licenses through the Software
/// Protection WMI classes. No slmgr, ospp.vbs or shell: the KMS host and port come only from
/// the organisation setting, checked again here, and are passed as typed WMI arguments.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class KmsLicensing
{
    public const string WindowsApplicationId = "55c92734-d682-4d71-983e-d6ec3f16059f";
    public const string OfficeApplicationId = "0ff1ce15-a989-479d-af46-f275c6370663";
    private const string Scope = @"root\cimv2";

    public static IReadOnlyList<KmsProductState> Products(string applicationId, string? host,
        int port)
    {
        var rows = Wmi.Query(Scope,
            "SELECT ID, Name, Description, LicenseStatus, LicenseStatusReason, " +
            "KeyManagementServiceMachine, KeyManagementServicePort FROM SoftwareLicensingProduct " +
            $"WHERE ApplicationID='{applicationId}' AND PartialProductKey IS NOT NULL", 50);
        return rows.Select(row => FromRow(row, host, port))
            .Where(product => product.Id.Length > 0).ToList();
    }

    internal static KmsProductState FromRow(IReadOnlyDictionary<string, object?> row,
        string? host, int port)
    {
        var status = (int)(Wmi.Long(row, "LicenseStatus") ?? -1);
        var reason = Wmi.Long(row, "LicenseStatusReason");
        var machine = Wmi.String(row, "KeyManagementServiceMachine")?.Trim();
        var machinePort = (int)(Wmi.Long(row, "KeyManagementServicePort") ?? 0);
        var usesOrganisation = host is not null && string.Equals(machine, host,
                StringComparison.OrdinalIgnoreCase)
            && (machinePort == port || machinePort == 0 && port == KmsSettings.DefaultPort);
        return new KmsProductState(Wmi.String(row, "ID") ?? "", Wmi.String(row, "Name") ?? "",
            (Wmi.String(row, "Description") ?? "").Contains("VOLUME_KMSCLIENT",
                StringComparison.OrdinalIgnoreCase),
            status == 1, status, usesOrganisation,
            status != 1 && reason is { } code and not 0 ? Hex(code) : null);
    }

    /// <summary>Points one product at the organisation KMS and activates it; returns the error code or null.</summary>
    public static string? Activate(string productId, string host, int port)
    {
        try
        {
            using var product = new ManagementObject(Scope,
                $"SoftwareLicensingProduct.ID='{Wmi.Literal(productId)}'", null);
            product.InvokeMethod("SetKeyManagementServiceMachine", [host]);
            product.InvokeMethod("SetKeyManagementServicePort", [(uint)port]);
            product.InvokeMethod("Activate", null);
            return null;
        }
        catch (ManagementException exception)
        {
            return Hex((uint)exception.ErrorCode);
        }
        catch (COMException exception)
        {
            return Hex(exception.HResult);
        }
        finally
        {
            RefreshLicenseStatus();
        }
    }

    private static void RefreshLicenseStatus()
    {
        try { Wmi.Invoke(Scope, "SELECT Version FROM SoftwareLicensingService", "RefreshLicenseStatus"); }
        catch (ManagementException) { }
        catch (COMException) { }
    }

    internal static string Hex(long value) => $"0x{unchecked((uint)value):X8}";
}

/// <summary>What both KMS fixes check first: the setting is valid and the server answers.</summary>
[SupportedOSPlatform("windows")]
internal static class KmsPreflight
{
    public static async Task<(string Host, int Port, EndpointActionResult? Stop)> CheckAsync(
        ActionInvocation invocation, CancellationToken cancellationToken)
    {
        if (!KmsSettings.TryRead(invocation.Settings, out var host, out var port))
            return ("", 0, EndpointActionResult.Blocked(
                "The organisation KMS server is not set in the control plane settings."));
        var probe = await NetworkProbes.TestTcpAsync(host, port, TimeSpan.FromSeconds(5),
            cancellationToken);
        return probe.Reachable ? (host, port, null)
            : (host, port, EndpointActionResult.Blocked(
                $"The organisation KMS server does not answer from this PC ({probe.Error}).",
                new { Reachable = false, probe.Error }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class KmsCheckAction : IEndpointAction
{
    public string Id => "kms.check";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var configured = KmsSettings.TryRead(invocation.Settings, out var host, out var port);
        PortProbe? probe = configured
            ? await NetworkProbes.TestTcpAsync(host, port, TimeSpan.FromSeconds(5), cancellationToken)
            : null;
        var orgHost = configured ? host : null;
        var windows = KmsLicensing.Products(KmsLicensing.WindowsApplicationId, orgHost, port)
            .OrderBy(product => product.Licensed ? 0 : 1).FirstOrDefault();
        var office = KmsLicensing.Products(KmsLicensing.OfficeApplicationId, orgHost, port);

        var summary = !configured ? "The organisation KMS server is not set."
            : probe!.Reachable ? "The organisation KMS server answers."
            : $"The organisation KMS server does not answer ({probe.Error}).";
        summary += windows is null ? " Windows license not found."
            : windows.Licensed ? " Windows is activated." : " Windows is not activated.";
        if (office.Count > 0)
            summary += office.All(product => product.Licensed) ? " Office is activated."
                : " Office is not activated.";
        return EndpointActionResult.Ok(summary, new
        {
            Configured = configured,
            Reachable = probe?.Reachable ?? false,
            ElapsedMs = probe?.ElapsedMs,
            probe?.Error,
            Windows = windows is null ? (object)new { Found = false }
                : new { Found = true, windows.VolumeClient, windows.Licensed, windows.LicenseStatus,
                    windows.UsesOrganisationKms, windows.ErrorCode },
            Office = new
            {
                Found = office.Count > 0,
                Products = office.Select(product => new { product.Name, product.VolumeClient,
                    product.Licensed, product.LicenseStatus, product.UsesOrganisationKms,
                    product.ErrorCode })
            }
        });
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsKmsActivateAction : IEndpointAction
{
    public string Id => "windows.kms_activate";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (!KmsSettings.TryRead(invocation.Settings, out var configuredHost, out var configuredPort))
            return EndpointActionResult.Blocked(
                "The organisation KMS server is not set in the control plane settings.");
        var windows = KmsLicensing.Products(KmsLicensing.WindowsApplicationId, configuredHost,
                configuredPort)
            .OrderBy(product => product.Licensed ? 0 : 1).FirstOrDefault();
        if (windows is null)
            return EndpointActionResult.Blocked("No Windows license was found on this PC.");
        if (!windows.VolumeClient)
            return EndpointActionResult.Blocked(
                "This Windows is not a volume (KMS) edition, so the organisation KMS cannot activate it.");
        if (windows.Licensed && windows.UsesOrganisationKms)
            return EndpointActionResult.NotNeeded("Windows is already activated by the organisation KMS.");

        var (host, port, stop) = await KmsPreflight.CheckAsync(invocation, cancellationToken);
        if (stop is not null) return stop;
        var error = KmsLicensing.Activate(windows.Id, host, port);
        var after = KmsLicensing.Products(KmsLicensing.WindowsApplicationId, host, port)
            .FirstOrDefault(product => product.Id == windows.Id);
        return after?.Licensed == true
            ? EndpointActionResult.Done("Windows is activated with the organisation KMS.",
                new { after.LicenseStatus })
            : EndpointActionResult.Failed(
                $"Windows activation with the organisation KMS failed ({error ?? after?.ErrorCode ?? "unknown"}).",
                new { ErrorCode = error ?? after?.ErrorCode, after?.LicenseStatus });
    }
}

[SupportedOSPlatform("windows")]
public sealed class OfficeKmsActivateAction : IEndpointAction
{
    public string Id => "office.kms_activate";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (!KmsSettings.TryRead(invocation.Settings, out var configuredHost, out var configuredPort))
            return EndpointActionResult.Blocked(
                "The organisation KMS server is not set in the control plane settings.");
        var volume = KmsLicensing.Products(KmsLicensing.OfficeApplicationId, configuredHost,
                configuredPort)
            .Where(product => product.VolumeClient).ToList();
        if (volume.Count == 0)
            return EndpointActionResult.Blocked(
                "No volume (KMS) Office license was found on this PC.");
        var pending = volume.Where(product => !product.Licensed || !product.UsesOrganisationKms)
            .ToList();
        if (pending.Count == 0)
            return EndpointActionResult.NotNeeded("Office is already activated by the organisation KMS.");

        var (host, port, stop) = await KmsPreflight.CheckAsync(invocation, cancellationToken);
        if (stop is not null) return stop;
        var errors = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var product in pending)
            errors[product.Id] = KmsLicensing.Activate(product.Id, host, port);
        var after = KmsLicensing.Products(KmsLicensing.OfficeApplicationId, host, port)
            .Where(product => errors.ContainsKey(product.Id)).ToList();
        var results = after.Select(product => new
        {
            product.Name, product.Licensed, product.LicenseStatus,
            ErrorCode = errors[product.Id] ?? product.ErrorCode
        }).ToList();
        return after.Count == pending.Count && after.All(product => product.Licensed)
            ? EndpointActionResult.Done("Office is activated with the organisation KMS.",
                new { Products = results })
            : EndpointActionResult.Failed("Office activation with the organisation KMS failed for "
                + $"{results.Count(item => !item.Licensed)} of {pending.Count} products.",
                new { Products = results });
    }
}
