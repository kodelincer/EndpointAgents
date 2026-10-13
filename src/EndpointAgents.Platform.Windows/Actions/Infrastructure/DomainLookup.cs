using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Actions.Infrastructure;

public sealed record DomainControllerFacts(bool Found, string? Name, string? Address,
    string? DnsDomain, string? Error);

/// <summary>Domain membership and domain-controller discovery for the domain actions.</summary>
[SupportedOSPlatform("windows")]
public static class DomainLookup
{
    /// <summary>The joined AD domain name, or null for a workgroup or unjoined PC.</summary>
    public static string? JoinedDomain()
    {
        if (NetGetJoinInformation(null, out var buffer, out var status) != 0) return null;
        try
        {
            return status == 3 ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally
        {
            NetApiBufferFree(buffer);
        }
    }

    /// <summary>Finds a domain controller, bounded because the call can block for a long time.</summary>
    public static async Task<DomainControllerFacts> LocateAsync(string domain,
        bool forceRediscovery, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() => Locate(domain, forceRediscovery), cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (TimeoutException)
        {
            return new DomainControllerFacts(false, null, null, null, "Timed out");
        }
    }

    private static DomainControllerFacts Locate(string domain, bool forceRediscovery)
    {
        const uint ReturnDnsName = 0x40000000;
        const uint ForceRediscovery = 0x00000001;
        var status = DsGetDcName(null, domain, IntPtr.Zero, null,
            ReturnDnsName | (forceRediscovery ? ForceRediscovery : 0), out var info);
        if (status != 0)
            return new DomainControllerFacts(false, null, null, null, status switch
            {
                1355 => "NoSuchDomain",
                1311 => "NoLogonServers",
                _ => $"Win32Error{status}"
            });
        try
        {
            var dc = Marshal.PtrToStructure<DomainControllerInfo>(info);
            return new DomainControllerFacts(true, dc.DomainControllerName?.TrimStart('\\'),
                dc.DomainControllerAddress?.TrimStart('\\'), dc.DomainName, null);
        }
        finally
        {
            NetApiBufferFree(info);
        }
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

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetGetJoinInformation(string? server, out IntPtr nameBuffer,
        out int status);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int DsGetDcName(string? computerName, string? domainName,
        IntPtr domainGuid, string? siteName, uint flags, out IntPtr info);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
