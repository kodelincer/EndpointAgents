using EndpointAgents.Platform.Windows.Actions.Infrastructure;
using EndpointAgents.Shared.Actions;
using Microsoft.Win32;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text;

namespace EndpointAgents.Platform.Windows.Actions.SystemSide;

/// <summary>Physical adapters as Windows' device list sees them, including disabled ones.</summary>
[SupportedOSPlatform("windows")]
internal static class PhysicalAdapters
{
    public sealed record Adapter(string DeviceId, string Name, string Description, string Kind,
        bool Enabled, bool Connected, string Status, long? SpeedMbps, string? PnpDeviceId,
        string? SettingId);

    public static IReadOnlyList<Adapter> Read() => Wmi.Cimv2(
            "SELECT DeviceID, Name, NetConnectionID, NetEnabled, NetConnectionStatus, Speed, PNPDeviceID, GUID FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE")
        .Where(row => Wmi.String(row, "NetConnectionID") is { Length: > 0 }
            && !IsSoftwareDevice(Wmi.String(row, "PNPDeviceID")))
        .Select(row =>
        {
            var description = Wmi.String(row, "Name") ?? string.Empty;
            var status = Wmi.Long(row, "NetConnectionStatus");
            return new Adapter(Wmi.String(row, "DeviceID") ?? string.Empty,
                Wmi.String(row, "NetConnectionID")!, description, KindOf(description),
                Wmi.Bool(row, "NetEnabled"), status == 2, StatusText(status),
                Wmi.Long(row, "Speed") is { } bits and > 0 ? bits / 1_000_000 : null,
                Wmi.String(row, "PNPDeviceID"), Wmi.String(row, "GUID"));
        })
        .ToArray();

    public static Adapter? Find(string name) => Read().FirstOrDefault(item =>
        item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static uint? Invoke(Adapter adapter, string method) => Wmi.Invoke(@"root\cimv2",
        $"SELECT * FROM Win32_NetworkAdapter WHERE DeviceID = '{Wmi.Literal(adapter.DeviceId)}'",
        method);

    private static bool IsSoftwareDevice(string? pnpDeviceId) => pnpDeviceId is not null
        && (pnpDeviceId.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)
            || pnpDeviceId.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase));

    private static string KindOf(string description) =>
        new[] { "Wireless", "Wi-Fi", "WiFi", "WLAN", "802.11" }.Any(word =>
            description.Contains(word, StringComparison.OrdinalIgnoreCase)) ? "WiFi"
        : description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ? "Other"
        : "Wired";

    private static string StatusText(long? status) => status switch
    {
        0 => "Disconnected",
        1 => "Connecting",
        2 => "Connected",
        3 => "Disconnecting",
        4 => "HardwareNotPresent",
        5 => "HardwareDisabled",
        6 => "HardwareMalfunction",
        7 => "MediaDisconnected",
        8 => "Authenticating",
        9 => "AuthenticationSucceeded",
        10 => "AuthenticationFailed",
        11 => "InvalidAddress",
        12 => "CredentialsRequired",
        _ => "Disabled"
    };
}

[SupportedOSPlatform("windows")]
public sealed class NetworkAdaptersAction : IEndpointAction
{
    public string Id => "network.adapters";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var drivers = Wmi.Cimv2(
                "SELECT DeviceID, DriverVersion, DriverDate FROM Win32_PnPSignedDriver WHERE DeviceClass = 'NET'")
            .Where(row => Wmi.String(row, "DeviceID") is not null)
            .GroupBy(row => Wmi.String(row, "DeviceID")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var adapters = PhysicalAdapters.Read().Select(item =>
        {
            drivers.TryGetValue(item.PnpDeviceId ?? string.Empty, out var driver);
            return new
            {
                item.Name,
                item.Description,
                item.Kind,
                item.Enabled,
                item.Connected,
                item.Status,
                item.SpeedMbps,
                DriverVersion = driver is null ? null : Wmi.String(driver, "DriverVersion"),
                DriverDate = driver is null ? null : Wmi.Date(driver, "DriverDate")?.ToString("yyyy-MM-dd")
            };
        }).ToArray();
        var disabled = adapters.Count(item => !item.Enabled);
        var unplugged = adapters.Count(item => item.Enabled && item.Status == "MediaDisconnected");
        var connected = adapters.Count(item => item.Connected);
        return Task.FromResult(EndpointActionResult.Ok(
            adapters.Length == 0 ? "No physical network adapter was found."
            : $"{adapters.Length} adapter(s): {connected} connected, {disabled} turned off, {unplugged} without cable or signal.",
            new
            {
                Adapters = adapters,
                Summary = new { Connected = connected, Disabled = disabled, Unplugged = unplugged }
            }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class NetworkAdapterEnableAction : IEndpointAction
{
    public string Id => "network.adapter_enable";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var adapter = PhysicalAdapters.Find(invocation.Argument("adapter")!);
        if (adapter is null)
            return EndpointActionResult.Failed("That adapter is not on this PC. Use network.adapters.");
        if (adapter.Enabled)
            return EndpointActionResult.NotNeeded($"{adapter.Name} is already on.");
        var code = PhysicalAdapters.Invoke(adapter, "Enable");
        if (code is not 0)
            return EndpointActionResult.Failed($"Windows could not turn {adapter.Name} on (code {code}).");
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        return EndpointActionResult.Done($"{adapter.Name} was turned on.");
    }
}

[SupportedOSPlatform("windows")]
public sealed class NetworkAdapterRestartAction : IEndpointAction
{
    public string Id => "network.adapter_restart";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var adapter = PhysicalAdapters.Find(invocation.Argument("adapter")!);
        if (adapter is null)
            return EndpointActionResult.Failed("That adapter is not on this PC. Use network.adapters.");
        if (adapter.Enabled && PhysicalAdapters.Invoke(adapter, "Disable") is not 0)
            return EndpointActionResult.Failed($"Windows could not turn {adapter.Name} off.");
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        var code = PhysicalAdapters.Invoke(adapter, "Enable");
        if (code is not 0)
            return EndpointActionResult.Failed($"{adapter.Name} was turned off but did not come back on (code {code}); the helpdesk must check it.");
        await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken);
        return EndpointActionResult.Done($"{adapter.Name} was restarted.");
    }
}

/// <summary>DNS servers typed in by hand on DHCP adapters.</summary>
[SupportedOSPlatform("windows")]
internal static class StaticDns
{
    public sealed record Entry(string SettingId, long InterfaceIndex, string Name,
        IReadOnlyList<string> Servers);

    public static IReadOnlyList<Entry> OnDhcpAdapters() => Wmi.Cimv2(
            "SELECT SettingID, InterfaceIndex, Description, DHCPEnabled FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE")
        .Where(row => Wmi.Bool(row, "DHCPEnabled") && Wmi.String(row, "SettingID") is not null)
        .Select(row => new Entry(Wmi.String(row, "SettingID")!, Wmi.Long(row, "InterfaceIndex") ?? 0,
            Wmi.String(row, "Description") ?? string.Empty, ManualServers(Wmi.String(row, "SettingID")!)))
        .Where(entry => entry.Servers.Count > 0 && entry.InterfaceIndex > 0)
        .ToArray();

    public static long? InterfaceIndexOf(string settingId) => Wmi.Cimv2(
            $"SELECT InterfaceIndex FROM Win32_NetworkAdapterConfiguration WHERE SettingID = '{Wmi.Literal(settingId)}'")
        .Select(row => Wmi.Long(row, "InterfaceIndex")).FirstOrDefault();

    private static IReadOnlyList<string> ManualServers(string settingId)
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{settingId}");
        return ((key?.GetValue("NameServer") as string) ?? string.Empty)
            .Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => IPAddress.TryParse(item, out var address)
                && address.AddressFamily == AddressFamily.InterNetwork)
            .ToArray();
    }
}

[SupportedOSPlatform("windows")]
public sealed class DnsResetToDhcpAction : IEndpointAction
{
    public string Id => "dns.reset_to_dhcp";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(invocation.Setting(ActionSettings.DnsFromDhcp), "true",
                StringComparison.OrdinalIgnoreCase))
            return EndpointActionResult.Blocked(
                "The environment standards do not say DHCP gives the right DNS servers, so hand-set servers may be intended.");
        var entries = StaticDns.OnDhcpAdapters().Take(3).ToArray();
        if (entries.Length == 0)
            return EndpointActionResult.NotNeeded("No adapter has hand-set DNS servers.");
        var changed = new List<string>();
        foreach (var entry in entries)
        {
            var result = await FixedTool.RunAsync("netsh.exe",
                ["interface", "ipv4", "set", "dnsservers",
                    $"name={entry.InterfaceIndex}", "source=dhcp"],
                TimeSpan.FromSeconds(20), cancellationToken);
            if (result.Succeeded) changed.Add(entry.Name);
        }
        ActionNative.DnsFlushResolverCache();
        if (changed.Count == 0)
            return EndpointActionResult.Failed("Windows did not accept the change.");
        var undo = string.Join(';', entries.Select(entry =>
            $"{entry.SettingId}={string.Join(',', entry.Servers)}"));
        return EndpointActionResult.Done(
            $"DNS servers now come from DHCP on {string.Join(", ", changed)}.",
            new { Adapters = changed },
            undo.Length <= EndpointActionValidator.MaxLocalItemLength
                ? new Dictionary<string, string> { ["servers"] = undo } : null);
    }
}

[SupportedOSPlatform("windows")]
public sealed class DnsRestoreAction : IEndpointAction
{
    public string Id => "dns.restore";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var restored = 0;
        foreach (var part in invocation.Argument("servers")!.Split(';',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2 || !Guid.TryParse(pair[0].Trim('{', '}'), out _)) continue;
            var servers = pair[1].Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Where(item => IPAddress.TryParse(item, out var address)
                    && address.AddressFamily == AddressFamily.InterNetwork)
                .Take(4).ToArray();
            var index = StaticDns.InterfaceIndexOf(pair[0]);
            if (servers.Length == 0 || index is null or <= 0) continue;
            await FixedTool.RunAsync("netsh.exe",
                ["interface", "ipv4", "set", "dnsservers", $"name={index}", "source=static",
                    $"address={servers[0]}", "register=primary", "validate=no"],
                TimeSpan.FromSeconds(20), cancellationToken);
            for (var position = 1; position < servers.Length; position++)
                await FixedTool.RunAsync("netsh.exe",
                    ["interface", "ipv4", "add", "dnsservers", $"name={index}",
                        $"address={servers[position]}", $"index={position + 1}", "validate=no"],
                    TimeSpan.FromSeconds(20), cancellationToken);
            restored++;
        }
        return restored == 0 ? EndpointActionResult.Failed("Nothing could be restored.")
            : EndpointActionResult.Done($"The previous DNS servers were restored on {restored} adapter(s).");
    }
}

/// <summary>The machine-wide WinHTTP proxy (netsh winhttp), read from its registry value.</summary>
[SupportedOSPlatform("windows")]
internal static class WinHttpProxy
{
    public sealed record Settings(bool Direct, string? Server, string? Bypass);

    public static Settings Read()
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\Connections");
        return Parse(key?.GetValue("WinHttpSettings") as byte[]);
    }

    /// <summary>Layout: size, counter, flags (1 direct, 3 proxy), then length-prefixed proxy and bypass strings.</summary>
    internal static Settings Parse(byte[]? blob)
    {
        if (blob is null || blob.Length < 16) return new Settings(true, null, null);
        var flags = BitConverter.ToInt32(blob, 8);
        var offset = 12;
        var server = ReadString(blob, ref offset);
        var bypass = ReadString(blob, ref offset);
        return (flags & 2) == 0 || string.IsNullOrWhiteSpace(server)
            ? new Settings(true, null, null)
            : new Settings(false, server, string.IsNullOrWhiteSpace(bypass) ? null : bypass);
    }

    private static string? ReadString(byte[] blob, ref int offset)
    {
        if (offset + 4 > blob.Length) return null;
        var length = BitConverter.ToInt32(blob, offset);
        offset += 4;
        if (length <= 0 || offset + length > blob.Length) return null;
        var text = Encoding.ASCII.GetString(blob, offset, length);
        offset += length;
        return text;
    }

    public static Settings Standard(ActionInvocation invocation) =>
        invocation.Setting(ActionSettings.ProxyMode) == "server"
        && invocation.Setting(ActionSettings.ProxyServer) is { } server
            ? new Settings(false, server, invocation.Setting(ActionSettings.ProxyBypass))
            : new Settings(true, null, null);

    public static bool Same(Settings current, Settings standard) =>
        current.Direct == standard.Direct
        && (current.Direct || string.Equals(current.Server, standard.Server, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Normalize(current.Bypass), Normalize(standard.Bypass), StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string? bypass) => string.Join(';',
        (bypass ?? string.Empty).Split([';', ','], StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries).Order(StringComparer.OrdinalIgnoreCase));

    public static async Task<bool> ApplyAsync(Settings settings, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> arguments = settings.Direct || settings.Server is null
            ? ["winhttp", "reset", "proxy"]
            : settings.Bypass is null
                ? ["winhttp", "set", "proxy", $"proxy-server={settings.Server}"]
                : ["winhttp", "set", "proxy", $"proxy-server={settings.Server}", $"bypass-list={settings.Bypass}"];
        return (await FixedTool.RunAsync("netsh.exe", arguments, TimeSpan.FromSeconds(20),
            cancellationToken)).Succeeded;
    }

    /// <summary>Server values come from the admin's standards or the PC's own earlier value; still refuse odd text.</summary>
    public static bool IsSafeValue(string? value) =>
        value is null || value.Length <= 200 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || ".-_:;*<>,/=".Contains(character));
}

[SupportedOSPlatform("windows")]
public sealed class SystemProxyGetAction : IEndpointAction
{
    public string Id => "proxy.system_get";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var current = WinHttpProxy.Read();
        var matches = WinHttpProxy.Same(current, WinHttpProxy.Standard(invocation));
        return Task.FromResult(EndpointActionResult.Ok(
            (current.Direct ? "The system proxy is direct (no proxy)." : $"The system proxy is {current.Server}.")
            + (matches ? " It matches the standard." : " It differs from the standard."),
            new { current.Direct, current.Server, current.Bypass, MatchesStandard = matches }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class SystemProxyResetAction : IEndpointAction
{
    public string Id => "proxy.system_reset_to_standard";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var current = WinHttpProxy.Read();
        var standard = WinHttpProxy.Standard(invocation);
        if (WinHttpProxy.Same(current, standard))
            return EndpointActionResult.NotNeeded("The system proxy already matches the standard.");
        if (!WinHttpProxy.IsSafeValue(standard.Server) || !WinHttpProxy.IsSafeValue(standard.Bypass))
            return EndpointActionResult.Blocked("The proxy standard contains characters this action does not accept.");
        if (!await WinHttpProxy.ApplyAsync(standard, cancellationToken))
            return EndpointActionResult.Failed("Windows did not accept the proxy change.");
        var undo = new Dictionary<string, string>();
        if (!current.Direct && WinHttpProxy.IsSafeValue(current.Server) && WinHttpProxy.IsSafeValue(current.Bypass))
        {
            undo["server"] = current.Server!;
            if (current.Bypass is not null) undo["bypass"] = current.Bypass;
        }
        else undo["server"] = "";
        return EndpointActionResult.Done(standard.Direct ? "The system proxy is now direct."
            : $"The system proxy is now {standard.Server}.", null, undo);
    }
}

[SupportedOSPlatform("windows")]
public sealed class SystemProxyRestoreAction : IEndpointAction
{
    public string Id => "proxy.system_restore";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var server = invocation.Argument("server");
        var bypass = invocation.Argument("bypass");
        if (!WinHttpProxy.IsSafeValue(server) || !WinHttpProxy.IsSafeValue(bypass))
            return EndpointActionResult.Failed("The saved value is not usable.");
        var settings = string.IsNullOrWhiteSpace(server) ? new WinHttpProxy.Settings(true, null, null)
            : new WinHttpProxy.Settings(false, server, bypass);
        return await WinHttpProxy.ApplyAsync(settings, cancellationToken)
            ? EndpointActionResult.Done("The previous system proxy was restored.")
            : EndpointActionResult.Failed("Windows did not accept the proxy change.");
    }
}

/// <summary>Facts about company VPNs: Windows VPN profiles, vendor client services and tunnel adapters.</summary>
[SupportedOSPlatform("windows")]
internal static class VpnFacts
{
    private static readonly string[] TunnelWords =
        ["VPN", "TAP-", "Wintun", "WireGuard", "Fortinet", "AnyConnect", "PANGP",
         "GlobalProtect", "Juniper", "SonicWall", "Check Point", "OpenVPN", "Cisco Secure"];

    public static IEnumerable<NetworkInterface> TunnelAdapters() =>
        NetworkInterface.GetAllNetworkInterfaces().Where(item =>
            item.NetworkInterfaceType == NetworkInterfaceType.Ppp
            || TunnelWords.Any(word => item.Description.Contains(word, StringComparison.OrdinalIgnoreCase)
                || item.Name.Contains(word, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Is a Windows VPN profile with this name defined for all users or the signed-in user?</summary>
    public static bool ProfileExists(string connection, string? userSid)
    {
        var books = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                @"Microsoft\Network\Connections\Pbk\rasphone.pbk")
        };
        if (userSid is not null && UserProfiles.PathOf(userSid) is { } profile)
            books.Add(Path.Combine(profile, @"AppData\Roaming\Microsoft\Network\Connections\Pbk\rasphone.pbk"));
        var header = $"[{connection}]";
        return books.Any(book =>
        {
            try
            {
                return File.Exists(book) && File.ReadLines(book).Any(line =>
                    line.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        });
    }

    public static string ServiceState(string name)
    {
        try
        {
            using var service = new ServiceController(name);
            return service.StartType == ServiceStartMode.Disabled ? "Disabled" : service.Status.ToString();
        }
        catch (InvalidOperationException) { return "NotInstalled"; }
    }
}

[SupportedOSPlatform("windows")]
public sealed class VpnStatusAction : IEndpointAction
{
    public string Id => "vpn.status";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var target = invocation.Target();
        var type = target.Detail(EnvironmentTargetDetails.VpnType);
        var connection = target.Detail(EnvironmentTargetDetails.Connection);
        var serviceName = target.Detail(EnvironmentTargetDetails.Service);
        var tunnels = VpnFacts.TunnelAdapters().ToArray();
        var up = tunnels.Where(item => item.OperationalStatus == OperationalStatus.Up).ToArray();
        var connected = type == EnvironmentTargetDetails.VpnBuiltin
            ? up.Any(item => item.Name.Equals(connection, StringComparison.OrdinalIgnoreCase))
            : up.Length > 0;
        var port = target.Ports?.FirstOrDefault() is int listed and > 0 ? listed : 443;
        var probe = await NetworkProbes.TestTcpAsync(target.Address, port, TimeSpan.FromSeconds(5),
            cancellationToken);
        var errors = WindowsEvents.Read("Application",
                $"*[System[Provider[@Name='RasClient'] and (EventID=20227 or EventID=20271) and {WindowsEvents.Since(1)}]]",
                5)
            .Select(item => new { item.WhenUtc, item.Message }).ToArray();
        var adapters = up.Select(item =>
        {
            var properties = item.GetIPProperties();
            return new
            {
                item.Name,
                properties.DnsSuffix,
                DnsServerCount = properties.DnsAddresses.Count
            };
        }).ToArray();
        var profilePresent = type == EnvironmentTargetDetails.VpnBuiltin && connection is not null
            ? VpnFacts.ProfileExists(connection, invocation.UserSid) : (bool?)null;
        var serviceState = type == EnvironmentTargetDetails.VpnClient && serviceName is not null
            ? VpnFacts.ServiceState(serviceName) : null;

        var summary = new StringBuilder(connected ? "The VPN is connected." : "The VPN is not connected.");
        if (profilePresent == false) summary.Append(" The Windows VPN connection is not set up on this PC.");
        if (serviceState is not null and not "Running") summary.Append($" The VPN client service is {serviceState}.");
        summary.Append(probe.Reachable ? " The VPN server answers." : " The VPN server does not answer from this PC.");
        return EndpointActionResult.Ok(summary.ToString(), new
        {
            Type = type,
            ProfilePresent = profilePresent,
            ServiceState = serviceState,
            Connected = connected,
            ServerReachable = probe.Reachable,
            ServerError = probe.Error,
            VpnAdapters = adapters,
            RasManState = VpnFacts.ServiceState("RasMan"),
            RecentErrors = errors
        });
    }
}

[SupportedOSPlatform("windows")]
public sealed class VpnClientRestartAction : IEndpointAction
{
    public string Id => "vpn.client_restart";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var target = invocation.Target();
        var name = target.Detail(EnvironmentTargetDetails.Service);
        if (target.Detail(EnvironmentTargetDetails.VpnType) != EnvironmentTargetDetails.VpnClient
            || !EndpointActionValidator.IsValidServiceName(name))
            return EndpointActionResult.Blocked("This VPN has no client service to restart.");
        ServiceController service;
        try
        {
            service = new ServiceController(name!);
            _ = service.Status;
        }
        catch (InvalidOperationException)
        {
            return EndpointActionResult.Blocked("The VPN client is not installed on this PC.");
        }
        using (service)
        {
            if (service.StartType == ServiceStartMode.Disabled)
                return EndpointActionResult.Blocked("The VPN client service is disabled on this PC.");
            await ServiceTools.StopAsync(service, TimeSpan.FromSeconds(30), cancellationToken);
            service.Start();
            await ServiceTools.WaitAsync(service, ServiceControllerStatus.Running,
                TimeSpan.FromSeconds(30), cancellationToken);
        }
        return EndpointActionResult.Done("The VPN client service was restarted.", new { State = "Running" });
    }
}
