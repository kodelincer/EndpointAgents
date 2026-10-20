using EndpointAgents.Platform.Windows.Actions.Infrastructure;
using EndpointAgents.Shared.Actions;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Actions.SystemSide;

/// <summary>Windows' network profiles (Domain, Private, Public), from the NetConnectionProfile class.</summary>
[SupportedOSPlatform("windows")]
internal static class NetworkProfiles
{
    public const string Scope = @"root\StandardCimv2";

    public sealed record Profile(string Name, string Adapter, string Category, bool Internet);

    public static IReadOnlyList<Profile> Read() => Wmi.Query(Scope,
            "SELECT Name, InterfaceAlias, NetworkCategory, IPv4Connectivity FROM MSFT_NetConnectionProfile")
        .Select(row => new Profile(Wmi.String(row, "Name") ?? "", Wmi.String(row, "InterfaceAlias") ?? "",
            Wmi.Long(row, "NetworkCategory") switch { 0 => "Public", 1 => "Private", 2 => "Domain", _ => "Unknown" },
            Wmi.Long(row, "IPv4Connectivity") == 4))
        .ToArray();

    public static int SetCategory(string name, int category) => Wmi.Update(Scope,
        $"SELECT * FROM MSFT_NetConnectionProfile WHERE Name = '{Wmi.Literal(name)}'",
        "NetworkCategory", (uint)category);
}

[SupportedOSPlatform("windows")]
public sealed class NetworkCategoryAction : IEndpointAction
{
    public string Id => "network.category";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var profiles = NetworkProfiles.Read();
        var onDomain = profiles.Any(profile => profile.Category == "Domain");
        var publicOnes = profiles.Where(profile => profile.Category == "Public").ToArray();
        return Task.FromResult(EndpointActionResult.Ok(
            profiles.Count == 0 ? "No network is connected."
            : publicOnes.Length > 0 ? $"Windows treats {string.Join(", ", publicOnes.Select(item => item.Adapter))} as a Public network."
            : $"Networks: {string.Join(", ", profiles.Select(item => $"{item.Adapter} {item.Category}"))}.",
            new { Networks = profiles, OnDomainNetwork = onDomain }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class NetworkSetPrivateAction : IEndpointAction
{
    public string Id => "network.set_private";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var publicOnes = NetworkProfiles.Read().Where(profile => profile.Category == "Public")
            .Take(3).ToArray();
        if (publicOnes.Length == 0)
            return Task.FromResult(EndpointActionResult.NotNeeded("No network is marked Public."));
        var changed = publicOnes.Where(profile => NetworkProfiles.SetCategory(profile.Name, 1) > 0)
            .ToArray();
        if (changed.Length == 0)
            return Task.FromResult(EndpointActionResult.Failed("Windows did not accept the change (it may be set by policy)."));
        var undo = string.Join('|', changed.Select(profile => profile.Name));
        return Task.FromResult(EndpointActionResult.Done(
            $"{string.Join(", ", changed.Select(item => item.Adapter))} is now a Private network.",
            new { Changed = changed.Select(item => item.Adapter) },
            undo.Length <= EndpointActionValidator.MaxLocalItemLength
                ? new Dictionary<string, string> { ["networks"] = undo } : null));
    }
}

[SupportedOSPlatform("windows")]
public sealed class NetworkSetPublicAction : IEndpointAction
{
    public string Id => "network.set_public";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var names = invocation.Argument("networks")!.Split('|', StringSplitOptions.RemoveEmptyEntries);
        var changed = names.Take(3).Sum(name => NetworkProfiles.SetCategory(name, 0));
        return Task.FromResult(changed > 0
            ? EndpointActionResult.Done("The networks are Public again.")
            : EndpointActionResult.NotNeeded("Those networks are not connected now."));
    }
}

/// <summary>Physical adapters with a hand-typed IPv4 address.</summary>
[SupportedOSPlatform("windows")]
internal static class StaticAddresses
{
    public sealed record Entry(string SettingId, long InterfaceIndex, string Name, string Address,
        string Mask, string? Gateway);

    public static IReadOnlyList<Entry> Read()
    {
        var physical = PhysicalAdapters.Read().Select(adapter => adapter.SettingId)
            .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Wmi.Cimv2(
                "SELECT SettingID, InterfaceIndex, Description, DHCPEnabled, IPAddress, IPSubnet, DefaultIPGateway FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE")
            .Where(row => !Wmi.Bool(row, "DHCPEnabled") && Wmi.String(row, "SettingID") is { } id
                && physical.Contains(id))
            .Select(row =>
            {
                var addresses = row.GetValueOrDefault("IPAddress") as string[] ?? [];
                var masks = row.GetValueOrDefault("IPSubnet") as string[] ?? [];
                var gateways = row.GetValueOrDefault("DefaultIPGateway") as string[] ?? [];
                var index = Array.FindIndex(addresses, IsIpv4);
                return index < 0 || index >= masks.Length ? null
                    : new Entry(Wmi.String(row, "SettingID")!, Wmi.Long(row, "InterfaceIndex") ?? 0,
                        Wmi.String(row, "Description") ?? "", addresses[index], masks[index],
                        gateways.FirstOrDefault(IsIpv4));
            })
            .OfType<Entry>().Where(entry => entry.InterfaceIndex > 0).ToArray();
    }

    public static bool IsIpv4(string? value) =>
        IPAddress.TryParse(value, out var address) && address.AddressFamily == AddressFamily.InterNetwork;
}

[SupportedOSPlatform("windows")]
public sealed class IpResetToDhcpAction : IEndpointAction
{
    public string Id => "ip.reset_to_dhcp";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(invocation.Setting(ActionSettings.IpFromDhcp), "true",
                StringComparison.OrdinalIgnoreCase))
            return EndpointActionResult.Blocked(
                "The environment standards do not say office PCs use DHCP, so a hand-set address may be intended.");
        var entry = StaticAddresses.Read().FirstOrDefault();
        if (entry is null)
            return EndpointActionResult.NotNeeded("No adapter has a hand-set address.");
        var result = await FixedTool.RunAsync("netsh.exe",
            ["interface", "ipv4", "set", "address", $"name={entry.InterfaceIndex}", "source=dhcp"],
            TimeSpan.FromSeconds(30), cancellationToken);
        if (!result.Succeeded)
            return EndpointActionResult.Failed("Windows did not accept the change.");
        await FixedTool.RunAsync("netsh.exe",
            ["interface", "ipv4", "set", "dnsservers", $"name={entry.InterfaceIndex}", "source=dhcp"],
            TimeSpan.FromSeconds(20), cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        return EndpointActionResult.Done($"{entry.Name} now gets its address from DHCP.",
            new { Adapter = entry.Name },
            new Dictionary<string, string>
            {
                ["address"] = $"{entry.SettingId}={entry.Address}/{entry.Mask}/{entry.Gateway}"
            });
    }
}

[SupportedOSPlatform("windows")]
public sealed class IpRestoreAction : IEndpointAction
{
    public string Id => "ip.restore";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var pair = invocation.Argument("address")!.Split('=', 2);
        var parts = pair.Length == 2 ? pair[1].Split('/') : [];
        if (parts.Length != 3 || !Guid.TryParse(pair[0].Trim('{', '}'), out _)
            || !StaticAddresses.IsIpv4(parts[0]) || !StaticAddresses.IsIpv4(parts[1])
            || parts[2].Length > 0 && !StaticAddresses.IsIpv4(parts[2]))
            return EndpointActionResult.Failed("The saved address is not valid.");
        var index = StaticDns.InterfaceIndexOf(pair[0]);
        if (index is null or <= 0)
            return EndpointActionResult.Failed("That adapter is not on this PC any more.");
        List<string> arguments = ["interface", "ipv4", "set", "address", $"name={index}", "source=static",
            $"address={parts[0]}", $"mask={parts[1]}"];
        if (parts[2].Length > 0) arguments.Add($"gateway={parts[2]}");
        var result = await FixedTool.RunAsync("netsh.exe", arguments, TimeSpan.FromSeconds(30),
            cancellationToken);
        return result.Succeeded ? EndpointActionResult.Done("The previous address was put back.")
            : EndpointActionResult.Failed("Windows did not accept the address.");
    }
}

[SupportedOSPlatform("windows")]
public sealed class NetworkStackResetAction : IEndpointAction
{
    public string Id => "network.stack_reset";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var winsock = await FixedTool.RunAsync("netsh.exe", ["winsock", "reset"],
            TimeSpan.FromSeconds(45), cancellationToken);
        var ip = await FixedTool.RunAsync("netsh.exe", ["int", "ip", "reset"],
            TimeSpan.FromSeconds(45), cancellationToken);
        if (winsock.TimedOut || ip.TimedOut || winsock.ExitCode != 0)
            return EndpointActionResult.Failed("Windows could not reset the network settings.");
        return EndpointActionResult.Done(
            "The network settings were reset to Windows defaults. The PC must restart to finish.",
            new { RestartRequired = true });
    }
}

/// <summary>The Native Wifi API (wlanapi.dll), read and connect only.</summary>
[SupportedOSPlatform("windows")]
internal static class NativeWifi
{
    public sealed record Connection(string Ssid, string Profile, int SignalPercent, string RadioType,
        string Authentication);

    public sealed record State(bool HasWifi, bool RadioOn, Connection? Current, IReadOnlyList<string> Profiles,
        Guid? InterfaceId);

    public static State Read()
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var handle) != 0)
            return new State(false, false, null, [], null);
        try
        {
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out var list) != 0 || list == IntPtr.Zero)
                return new State(false, false, null, [], null);
            Guid id;
            try
            {
                if (Marshal.ReadInt32(list) == 0) return new State(false, false, null, [], null);
                id = Marshal.PtrToStructure<Guid>(list + 8);
            }
            finally { WlanFreeMemory(list); }

            var radioOn = true;
            if (WlanQueryInterface(handle, ref id, 4, IntPtr.Zero, out _, out var radio, out _) == 0)
            {
                try
                {
                    var phys = Marshal.ReadInt32(radio);
                    radioOn = Enumerable.Range(0, Math.Min(phys, 64)).Any(index =>
                        Marshal.ReadInt32(radio + 4 + index * 12 + 4) == 1
                        && Marshal.ReadInt32(radio + 4 + index * 12 + 8) == 1);
                }
                finally { WlanFreeMemory(radio); }
            }

            Connection? current = null;
            if (WlanQueryInterface(handle, ref id, 7, IntPtr.Zero, out _, out var attributes, out _) == 0)
            {
                try
                {
                    if (Marshal.ReadInt32(attributes) == 1) // wlan_interface_state_connected
                    {
                        var profile = Marshal.PtrToStringUni(attributes + 8) ?? "";
                        var ssidLength = Math.Min(Marshal.ReadInt32(attributes + 520), 32);
                        var ssid = new byte[ssidLength];
                        Marshal.Copy(attributes + 524, ssid, 0, ssidLength);
                        current = new Connection(System.Text.Encoding.UTF8.GetString(ssid), profile,
                            Marshal.ReadInt32(attributes + 576), PhyName(Marshal.ReadInt32(attributes + 568)),
                            AuthName(Marshal.ReadInt32(attributes + 596)));
                    }
                }
                finally { WlanFreeMemory(attributes); }
            }

            var profiles = new List<string>();
            if (WlanGetProfileList(handle, ref id, IntPtr.Zero, out var profileList) == 0)
            {
                try
                {
                    var count = Math.Min(Marshal.ReadInt32(profileList), 100);
                    for (var index = 0; index < count; index++)
                        profiles.Add(Marshal.PtrToStringUni(profileList + 8 + index * 516) ?? "");
                }
                finally { WlanFreeMemory(profileList); }
            }
            return new State(true, radioOn, current, profiles, id);
        }
        finally { WlanCloseHandle(handle, IntPtr.Zero); }
    }

    /// <summary>Connects with a profile already saved on the PC; returns the Win32 result.</summary>
    public static int Connect(Guid interfaceId, string profile)
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var handle) != 0) return -1;
        var name = Marshal.StringToHGlobalUni(profile);
        try
        {
            var parameters = new ConnectionParameters
            {
                Mode = 0, // wlan_connection_mode_profile
                Profile = name,
                BssType = 1 // dot11_BSS_type_infrastructure
            };
            return WlanConnect(handle, ref interfaceId, ref parameters, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(name);
            WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    private static string PhyName(int type) => type switch
    {
        4 => "802.11a", 5 => "802.11b", 6 => "802.11g", 7 => "Wi-Fi 4 (802.11n)",
        8 => "Wi-Fi 5 (802.11ac)", 10 => "Wi-Fi 6 (802.11ax)", 11 => "Wi-Fi 7 (802.11be)", _ => "Other"
    };

    private static string AuthName(int algorithm) => algorithm switch
    {
        1 => "Open", 2 => "Shared key", 3 => "WPA-Enterprise", 4 => "WPA-Personal",
        6 => "WPA2-Enterprise", 7 => "WPA2-Personal", 8 or 11 => "WPA3-Enterprise",
        9 => "WPA3-Personal", 10 => "OWE", _ => "Other"
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct ConnectionParameters
    {
        public int Mode;
        public IntPtr Profile;
        public IntPtr Ssid;
        public IntPtr DesiredBssids;
        public int BssType;
        public int Flags;
    }

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(int clientVersion, IntPtr reserved, out int negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")]
    private static extern int WlanQueryInterface(IntPtr handle, ref Guid interfaceId, int opcode,
        IntPtr reserved, out int size, out IntPtr data, out int valueType);
    [DllImport("wlanapi.dll")]
    private static extern int WlanGetProfileList(IntPtr handle, ref Guid interfaceId, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")]
    private static extern int WlanConnect(IntPtr handle, ref Guid interfaceId, ref ConnectionParameters parameters,
        IntPtr reserved);
    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);
}

[SupportedOSPlatform("windows")]
public sealed class WifiStatusAction : IEndpointAction
{
    public string Id => "wifi.status";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        NativeWifi.State state;
        try { state = NativeWifi.Read(); }
        catch (DllNotFoundException) { state = new NativeWifi.State(false, false, null, [], null); }
        var summary = !state.HasWifi ? "This PC has no Wi-Fi adapter (or it is turned off in Device Manager)."
            : !state.RadioOn ? "Wi-Fi is turned off (airplane mode or the Wi-Fi switch)."
            : state.Current is null ? "Wi-Fi is on but not connected."
            : $"Connected to {state.Current.Ssid}, signal {state.Current.SignalPercent}%.";
        return Task.FromResult(EndpointActionResult.Ok(summary, new
        {
            state.HasWifi,
            state.RadioOn,
            Connected = state.Current is not null,
            state.Current?.Ssid,
            state.Current?.SignalPercent,
            state.Current?.RadioType,
            state.Current?.Authentication,
            // The control plane marks the company networks among them (CompanyProfiles).
            Profiles = state.Profiles.Take(30)
        }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class WifiConnectCompanyAction : IEndpointAction
{
    public string Id => "wifi.connect_company";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var target = invocation.Target();
        var profile = target.Detail(EnvironmentTargetDetails.Profile) ?? target.Address;
        var state = NativeWifi.Read();
        if (!state.HasWifi || state.InterfaceId is not { } id)
            return EndpointActionResult.Blocked("This PC has no working Wi-Fi adapter.");
        if (!state.RadioOn)
            return EndpointActionResult.Blocked("Wi-Fi is turned off; the user must turn it on (airplane mode or the Wi-Fi switch).");
        if (string.Equals(state.Current?.Ssid, target.Address, StringComparison.Ordinal))
            return EndpointActionResult.NotNeeded($"Already connected to {target.Label}.");
        if (!state.Profiles.Contains(profile, StringComparer.OrdinalIgnoreCase))
            return EndpointActionResult.Blocked(
                $"The {target.Label} profile is not on this PC; the helpdesk must deploy it.");
        var result = NativeWifi.Connect(id, state.Profiles.First(item =>
            string.Equals(item, profile, StringComparison.OrdinalIgnoreCase)));
        if (result != 0)
            return EndpointActionResult.Failed($"Windows could not start the connection (code {result}).");
        await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        var after = NativeWifi.Read().Current;
        return string.Equals(after?.Ssid, target.Address, StringComparison.Ordinal)
            ? EndpointActionResult.Done($"Connected to {target.Label}.")
            : EndpointActionResult.Failed($"The connection to {target.Label} did not come up; the network may be out of range.");
    }
}
