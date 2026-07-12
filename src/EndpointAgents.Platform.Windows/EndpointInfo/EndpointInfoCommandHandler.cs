using EndpointAgents.Core.Commands;
using EndpointAgents.Platform.Windows.Diagnostics;
using EndpointAgents.Shared.Rpc;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.EndpointInfo;

[SupportedOSPlatform("windows")]
public sealed class EndpointInfoCommandHandler : IEndpointCommandHandler
{
    private readonly ILogger<EndpointInfoCommandHandler> _logger;

    public EndpointInfoCommandHandler(ILogger<EndpointInfoCommandHandler> logger)
    {
        _logger = logger;
    }

    public string Method => "endpoint.get_info";

    public Task<RpcResponse> HandleAsync(
        RpcRequest request,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var warnings = new List<DiagnosticWarning>();
        var result = new
        {
            User = CollectUserInfo(),
            Machine = CollectMachineInfo(),
            Adapters = CollectAdapters(warnings),
            Proxy = CollectProxySettings(warnings),
            Time = new
            {
                LocalNow = DateTimeOffset.Now,
                UtcNow = DateTimeOffset.UtcNow,
                TimeZone = TimeZoneInfo.Local.DisplayName
            }
        };

        return Task.FromResult(RpcResponse.Success(
            request.Id,
            DiagnosticResponse.Create(context, result, warnings)));
    }

    private static object CollectUserInfo()
    {
        return new
        {
            UserName = Environment.UserName,
            DomainName = Environment.UserDomainName,
            IsInteractiveUser = Environment.UserInteractive
        };
    }

    private static object CollectMachineInfo()
    {
        return new
        {
            Hostname = SafeValue(() => Dns.GetHostName(), Environment.MachineName),
            MachineName = Environment.MachineName,
            OSVersion = Environment.OSVersion.ToString(),
            Environment.Is64BitOperatingSystem,
            ProcessorCount = Environment.ProcessorCount,
            SystemDirectory = Environment.SystemDirectory,
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64).ToString()
        };
    }

    private object[] CollectAdapters(List<DiagnosticWarning> warnings)
    {
        try
        {
            return NetworkInterface
                .GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                .Select(adapter => CollectAdapter(adapter, warnings))
                .ToArray();
        }
        catch (Exception ex)
        {
            AddWarning(warnings, "network.adapters", "Failed to collect network adapters.", ex);
            return Array.Empty<object>();
        }
    }

    private object CollectAdapter(NetworkInterface adapter, List<DiagnosticWarning> warnings)
    {
        try
        {
            var ipProperties = adapter.GetIPProperties();
            var ipv4Properties = Safe("adapter.ipv4_properties", () => ipProperties.GetIPv4Properties(), warnings);

            return new
            {
                adapter.Name,
                adapter.Description,
                Id = adapter.Id,
                Type = adapter.NetworkInterfaceType.ToString(),
                OperationalStatus = adapter.OperationalStatus.ToString(),
                adapter.Speed,
                MacAddress = FormatMacAddress(adapter.GetPhysicalAddress()),
                IsLikelyVirtual = IsLikelyVirtual(adapter),
                IsLikelyVpn = IsLikelyVpn(adapter),
                DnsSuffix = ipProperties.DnsSuffix,
                DhcpEnabled = ipv4Properties?.IsDhcpEnabled,
                IpAddresses = ipProperties.UnicastAddresses
                    .Select(address => new
                    {
                        Address = address.Address.ToString(),
                        Family = address.Address.AddressFamily.ToString(),
                        address.PrefixLength,
                        SuffixOrigin = address.SuffixOrigin.ToString(),
                        PrefixOrigin = address.PrefixOrigin.ToString()
                    })
                    .ToArray(),
                IPv4Addresses = ipProperties.UnicastAddresses
                    .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(address => address.Address.ToString())
                    .ToArray(),
                IPv6Addresses = ipProperties.UnicastAddresses
                    .Where(address => address.Address.AddressFamily == AddressFamily.InterNetworkV6)
                    .Select(address => address.Address.ToString())
                    .ToArray(),
                Gateways = ipProperties.GatewayAddresses
                    .Select(gateway => gateway.Address.ToString())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToArray(),
                DnsServers = ipProperties.DnsAddresses
                    .Select(dns => dns.ToString())
                    .ToArray(),
                WinsServers = ipProperties.WinsServersAddresses
                    .Select(wins => wins.ToString())
                    .ToArray()
            };
        }
        catch (Exception ex)
        {
            AddWarning(warnings, $"network.adapter.{adapter.Name}", "Failed to collect one network adapter.", ex);

            return new
            {
                adapter.Name,
                adapter.Description,
                Type = adapter.NetworkInterfaceType.ToString(),
                Error = DiagnosticResponse.Sanitize(ex)
            };
        }
    }

    private object CollectProxySettings(List<DiagnosticWarning> warnings)
    {
        try
        {
            var proxy = WebRequest.DefaultWebProxy;
            var testUri = new Uri("http://example.com/");
            var proxyUri = proxy?.GetProxy(testUri);

            return new
            {
                ProxyConfigured = proxyUri is not null && proxyUri != testUri,
                ProxyUri = proxyUri is null || proxyUri == testUri ? null : proxyUri.ToString()
            };
        }
        catch (Exception ex)
        {
            AddWarning(warnings, "network.proxy", "Failed to collect proxy settings.", ex);
            return new
            {
                ProxyConfigured = (bool?)null
            };
        }
    }

    private T? Safe<T>(string source, Func<T> action, List<DiagnosticWarning> warnings)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            AddWarning(warnings, source, "A non-critical collector failed.", ex);
            return default;
        }
    }

    private void AddWarning(
        List<DiagnosticWarning> warnings,
        string source,
        string message,
        Exception ex)
    {
        _logger.LogWarning(ex, "{Source}: {Message}", source, message);
        warnings.Add(new DiagnosticWarning(source, message, ex.GetType().Name));
    }

    private static string? FormatMacAddress(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 0 ? null : string.Join("-", bytes.Select(x => x.ToString("X2")));
    }

    private static bool IsLikelyVirtual(NetworkInterface adapter)
    {
        var text = $"{adapter.Name} {adapter.Description}".ToLowerInvariant();
        return text.Contains("virtual", StringComparison.Ordinal)
            || text.Contains("hyper-v", StringComparison.Ordinal)
            || text.Contains("vmware", StringComparison.Ordinal)
            || text.Contains("virtualbox", StringComparison.Ordinal)
            || text.Contains("loopback", StringComparison.Ordinal);
    }

    private static bool IsLikelyVpn(NetworkInterface adapter)
    {
        var text = $"{adapter.Name} {adapter.Description}".ToLowerInvariant();
        return adapter.NetworkInterfaceType == NetworkInterfaceType.Ppp
            || text.Contains("vpn", StringComparison.Ordinal)
            || text.Contains("tap", StringComparison.Ordinal)
            || text.Contains("tun", StringComparison.Ordinal)
            || text.Contains("pppoe", StringComparison.Ordinal)
            || text.Contains("wan miniport", StringComparison.Ordinal);
    }

    private static T SafeValue<T>(Func<T> getValue, T fallback)
    {
        try
        {
            return getValue();
        }
        catch
        {
            return fallback;
        }
    }
}
