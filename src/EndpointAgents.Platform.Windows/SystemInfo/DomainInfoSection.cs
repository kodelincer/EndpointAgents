using EndpointAgents.Shared.Support;
using Microsoft.Win32;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.SystemInfo;

/// <summary>
/// Whether the PC is joined to an Active Directory domain, a workgroup or Microsoft Entra ID,
/// and (for domain PCs) whether a domain controller can be located right now.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DomainInfoSection : ISystemInfoSection
{
    private static readonly TimeSpan DomainControllerTimeout = TimeSpan.FromSeconds(8);

    public string Name => SystemInfoContracts.Domain;

    public async Task<object?> CollectAsync(SystemInfoWarnings warnings,
        CancellationToken cancellationToken)
    {
        var (joinType, joinedName) = warnings.Try("domain.join_information",
            ReadJoinInformation);
        var domainJoined = joinType == "Domain";
        var controller = domainJoined && joinedName is not null
            ? await LocateDomainControllerAsync(joinedName, warnings, cancellationToken)
            : null;

        return new
        {
            JoinType = joinType ?? "Unknown",
            DomainJoined = domainJoined,
            // The AD domain (NetBIOS) or workgroup name; not the signed-in user's domain.
            JoinedName = joinedName,
            PrimaryDnsSuffix = warnings.Try("domain.dns_suffix",
                () => IPGlobalProperties.GetIPGlobalProperties().DomainName),
            EntraIdJoined = warnings.Try("domain.entra_join", IsEntraJoined),
            DomainController = controller
        };
    }

    internal static (string? JoinType, string? Name) ReadJoinInformation()
    {
        var status = NativeMethods.NetGetJoinInformation(null, out var buffer, out var type);
        if (status != 0) throw new InvalidOperationException($"NetGetJoinInformation {status}");
        try
        {
            var name = Marshal.PtrToStringUni(buffer);
            return (type switch
            {
                NetJoinStatus.DomainName => "Domain",
                NetJoinStatus.WorkgroupName => "Workgroup",
                NetJoinStatus.Unjoined => "Unjoined",
                _ => "Unknown"
            }, string.IsNullOrWhiteSpace(name) ? null : name);
        }
        finally
        {
            NativeMethods.NetApiBufferFree(buffer);
        }
    }

    private static bool IsEntraJoined()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo");
        return key?.SubKeyCount > 0;
    }

    /// <summary>DsGetDcName can block for a long time when no DC answers, so it is bounded.</summary>
    private static async Task<object?> LocateDomainControllerAsync(string domain,
        SystemInfoWarnings warnings, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() => LocateDomainController(domain), cancellationToken)
                .WaitAsync(DomainControllerTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return new { Found = false, Error = "Timed out" };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            warnings.Add("domain.controller", ex);
            return null;
        }
    }

    private static object LocateDomainController(string domain)
    {
        const uint ReturnDnsName = 0x40000000;
        var status = NativeMethods.DsGetDcName(null, domain, IntPtr.Zero, null,
            ReturnDnsName, out var info);
        if (status != 0)
            return new
            {
                Found = false,
                Error = status switch
                {
                    1355 => "No domain controller could be found (ERROR_NO_SUCH_DOMAIN).",
                    1311 => "No logon servers are available (ERROR_NO_LOGON_SERVERS).",
                    _ => $"Domain controller lookup failed with Win32 error {status}."
                }
            };
        try
        {
            var dc = Marshal.PtrToStructure<DomainControllerInfo>(info);
            return new
            {
                Found = true,
                ControllerName = dc.DomainControllerName?.TrimStart('\\'),
                ControllerAddress = dc.DomainControllerAddress?.TrimStart('\\'),
                DnsDomain = dc.DomainName,
                Forest = dc.DnsForestName,
                Site = dc.DcSiteName,
                ClientSite = dc.ClientSiteName
            };
        }
        finally
        {
            NativeMethods.NetApiBufferFree(info);
        }
    }

    private enum NetJoinStatus
    {
        Unknown = 0,
        Unjoined = 1,
        WorkgroupName = 2,
        DomainName = 3
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DomainControllerInfo
    {
        public string? DomainControllerName;
        public string? DomainControllerAddress;
        public uint DomainControllerAddressType;
        public Guid DomainGuid;
        public string? DomainName;
        public string? DnsForestName;
        public uint Flags;
        public string? DcSiteName;
        public string? ClientSiteName;
    }

    private static class NativeMethods
    {
        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        public static extern int NetGetJoinInformation(string? server, out IntPtr nameBuffer,
            out NetJoinStatus status);

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        public static extern int DsGetDcName(string? computerName, string? domainName,
            IntPtr domainGuid, string? siteName, uint flags, out IntPtr info);

        [DllImport("netapi32.dll")]
        public static extern int NetApiBufferFree(IntPtr buffer);
    }
}
