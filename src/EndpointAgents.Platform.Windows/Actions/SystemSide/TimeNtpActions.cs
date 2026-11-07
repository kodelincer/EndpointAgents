using EndpointAgents.Platform.Windows.Actions.Infrastructure;
using EndpointAgents.Shared.Actions;
using Microsoft.Win32;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace EndpointAgents.Platform.Windows.Actions.SystemSide;

/// <summary>
/// Corrects the clock and date from the right source: a PC in a domain takes its time from
/// Active Directory (a domain controller), a PC outside a domain from the organisation NTP
/// servers set in the control plane. Registry values, the Windows Time service and fixed
/// w32tm arguments only; the server names come from the control-plane setting, are validated
/// again here (plain host names or IPv4 addresses, so no option or port can ride along) and
/// never from the agent.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TimeCorrectAction : IEndpointAction
{
    public string Id => "time.correct";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var joined = DomainLookup.JoinedDomain();
        var inDomain = joined is not null;
        string source, reference = "";
        double? before = null;
        IReadOnlyList<string> servers = [];
        if (joined is not null)
        {
            var controller = await DomainLookup.LocateAsync(joined, false, cancellationToken);
            if (controller.Address is not { } address)
                return EndpointActionResult.Blocked(
                    "No domain controller was found, so the clock cannot be corrected from Active Directory.");
            source = "domain";
            reference = address;
            before = await SntpClient.ClockOffsetSecondsAsync(reference, TimeSpan.FromSeconds(4),
                cancellationToken);
        }
        else
        {
            if (!NtpSettings.TryRead(invocation.Settings, out servers))
                return EndpointActionResult.Blocked(
                    "The organisation NTP servers are not set in the control plane settings.");
            source = "ntp";
            if (await TimeSetup.FirstAnsweringAsync(servers, cancellationToken) is var (host, offset))
            {
                reference = host;
                before = offset;
            }
        }

        if (before is null)
            return EndpointActionResult.Blocked(inDomain
                ? "The domain controller does not answer time requests from this PC."
                : "None of the organisation NTP servers answers from this PC.");

        var configured = inDomain ? TimeSetup.UsesDomainHierarchy() : TimeSetup.UsesNtpServers(servers);
        if (configured && Math.Abs(before.Value) < TimeSetup.OkSeconds
            && DomainFacts.ServiceState("W32Time") == "Running")
            return EndpointActionResult.NotNeeded("The clock is already in sync.",
                new { ClockSource = source, OffsetBeforeSeconds = before });

        if (inDomain) TimeSetup.ConfigureDomain();
        else TimeSetup.ConfigureManual(servers);
        await TimeSetup.RestartServiceAsync(cancellationToken);
        await TimeSetup.ResyncAsync(cancellationToken);
        var after = await SntpClient.ClockOffsetSecondsAsync(reference, TimeSpan.FromSeconds(4),
            cancellationToken);

        var largeStep = false;
        if (after is null || Math.Abs(after.Value) >= TimeSetup.OkSeconds)
        {
            // Windows refuses a very large jump unless the phase-correction limits allow it.
            largeStep = true;
            try
            {
                TimeSetup.LiftPhaseLimits();
                await TimeSetup.RestartServiceAsync(cancellationToken);
                await TimeSetup.ResyncAsync(cancellationToken);
                after = await SntpClient.ClockOffsetSecondsAsync(reference, TimeSpan.FromSeconds(4),
                    cancellationToken);
            }
            finally
            {
                TimeSetup.RestorePhaseLimits();
                await FixedTool.RunAsync("w32tm.exe", ["/config", "/update"],
                    TimeSpan.FromSeconds(15), CancellationToken.None);
            }
        }

        var data = new
        {
            ClockSource = source,
            OffsetBeforeSeconds = before,
            OffsetAfterSeconds = after,
            LargeStepNeeded = largeStep,
            LocalTime = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz")
        };
        return after is { } final && Math.Abs(final) < TimeSetup.OkSeconds
            ? EndpointActionResult.Done("The clock and date were corrected.", data)
            : EndpointActionResult.Failed(
                "The time service was set up, but the clock is still wrong.", data);
    }
}

/// <summary>Windows Time service settings (the same on Windows 10 and 11).</summary>
[SupportedOSPlatform("windows")]
internal static class TimeSetup
{
    /// <summary>A clock this close to the reference counts as right.</summary>
    public const double OkSeconds = 120;

    private const string Parameters = @"SYSTEM\CurrentControlSet\Services\W32Time\Parameters";
    private const string Config = @"SYSTEM\CurrentControlSet\Services\W32Time\Config";
    private const string NtpClient = @"SYSTEM\CurrentControlSet\Services\W32Time\TimeProviders\NtpClient";
    private const string PolicyParameters = @"SOFTWARE\Policies\Microsoft\W32time\Parameters";
    private const int DefaultPhaseLimit = 54000;
    private const int NoPhaseLimit = -1; // 0xFFFFFFFF as a DWORD

    /// <summary>"0x9" = use the special poll interval (0x1) and client mode (0x8).</summary>
    internal static string NtpServerValue(IEnumerable<string> hosts) =>
        string.Join(' ', hosts.Select(host => host + ",0x9"));

    /// <summary>The first server in the list (in list order) that answers a time request.</summary>
    public static async Task<(string Host, double Offset)?> FirstAnsweringAsync(IReadOnlyList<string> servers,
        CancellationToken cancellationToken)
    {
        var offsets = await Task.WhenAll(servers.Select(server => SntpClient.ClockOffsetSecondsAsync(
            server, TimeSpan.FromSeconds(4), cancellationToken)));
        for (var index = 0; index < servers.Count; index++)
            if (offsets[index] is { } offset) return (servers[index], offset);
        return null;
    }

    /// <summary>True when the registry names exactly these servers as the manual NTP source.</summary>
    public static bool UsesNtpServers(IReadOnlyList<string> hosts)
    {
        using var key = Registry.LocalMachine.OpenSubKey(Parameters);
        return Matches(key?.GetValue("Type") as string, key?.GetValue("NtpServer") as string, hosts);
    }

    internal static bool Matches(string? type, string? ntpServer, IReadOnlyList<string> hosts)
    {
        if (!string.Equals(type, "NTP", StringComparison.OrdinalIgnoreCase) || ntpServer is null
            || hosts.Count == 0)
            return false;
        var configured = ntpServer.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        return hosts.All(host => configured.Any(part =>
            string.Equals(part, host, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// A domain PC follows the domain hierarchy (Type NT5DS). Group Policy may set the time
    /// source itself; then it is left alone and only a resync is needed.
    /// </summary>
    public static bool UsesDomainHierarchy()
    {
        if (ManagedByPolicy()) return true;
        using var key = Registry.LocalMachine.OpenSubKey(Parameters);
        return string.Equals(key?.GetValue("Type") as string, "NT5DS",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool ManagedByPolicy()
    {
        using var key = Registry.LocalMachine.OpenSubKey(PolicyParameters);
        return key?.GetValue("Type") is string;
    }

    public static void ConfigureDomain()
    {
        if (ManagedByPolicy()) return;
        using var parameters = Registry.LocalMachine.CreateSubKey(Parameters, writable: true);
        parameters.SetValue("Type", "NT5DS", RegistryValueKind.String);
    }

    public static void ConfigureManual(IReadOnlyList<string> hosts)
    {
        using (var parameters = Registry.LocalMachine.CreateSubKey(Parameters, writable: true))
        {
            parameters.SetValue("Type", "NTP", RegistryValueKind.String);
            parameters.SetValue("NtpServer", NtpServerValue(hosts), RegistryValueKind.String);
        }
        using var client = Registry.LocalMachine.CreateSubKey(NtpClient, writable: true);
        client.SetValue("Enabled", 1, RegistryValueKind.DWord);
    }

    public static void LiftPhaseLimits() => SetPhaseLimits(NoPhaseLimit);

    /// <summary>Puts the phase-correction limits back to the Windows default.</summary>
    public static void RestorePhaseLimits() => SetPhaseLimits(DefaultPhaseLimit);

    private static void SetPhaseLimits(int value)
    {
        using var config = Registry.LocalMachine.CreateSubKey(Config, writable: true);
        config.SetValue("MaxPosPhaseCorrection", value, RegistryValueKind.DWord);
        config.SetValue("MaxNegPhaseCorrection", value, RegistryValueKind.DWord);
    }

    public static async Task RestartServiceAsync(CancellationToken cancellationToken)
    {
        using var service = new ServiceController("W32Time");
        if (service.Status != ServiceControllerStatus.Stopped)
            await ServiceTools.StopAsync(service, TimeSpan.FromSeconds(15), cancellationToken);
        await ServiceTools.EnsureRunningAsync("W32Time", TimeSpan.FromSeconds(15), cancellationToken);
    }

    public static async Task ResyncAsync(CancellationToken cancellationToken)
    {
        await FixedTool.RunAsync("w32tm.exe", ["/config", "/update"], TimeSpan.FromSeconds(15),
            cancellationToken);
        await FixedTool.RunAsync("w32tm.exe", ["/resync", "/rediscover"], TimeSpan.FromSeconds(25),
            cancellationToken);
    }
}
