using EndpointAgents.Platform.Windows.Actions.Infrastructure;
using EndpointAgents.Shared.Actions;
using Microsoft.Win32;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace EndpointAgents.Platform.Windows.Actions.SystemSide;

/// <summary>
/// Points the Windows Time service at the organisation NTP server on a PC outside a domain
/// and corrects the clock, even a long way off. Registry values and the time service only; the
/// server name comes from the control-plane setting and is validated again here (a plain host
/// name or IPv4 address, so it cannot carry options or a second server).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TimeSetNtpAction : IEndpointAction
{
    public string Id => "time.set_ntp";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (!NtpSettings.TryRead(invocation.Settings, out var host))
            return EndpointActionResult.Blocked(
                "The organisation NTP server is not set in the control plane settings.");
        if (DomainLookup.JoinedDomain() is not null)
            return EndpointActionResult.NotNeeded(
                "This PC is in a domain and takes its time from the domain controller.");
        var before = await SntpClient.ClockOffsetSecondsAsync(host, TimeSpan.FromSeconds(4),
            cancellationToken);
        if (before is null)
            return EndpointActionResult.Blocked(
                "The organisation time server does not answer from this PC.");
        if (TimeSetup.UsesNtpServer(host) && Math.Abs(before.Value) < TimeSetup.OkSeconds
            && DomainFacts.ServiceState("W32Time") == "Running")
            return EndpointActionResult.NotNeeded("The clock already follows the organisation time server.");

        TimeSetup.Configure(host);
        await TimeSetup.RestartServiceAsync(cancellationToken);
        await TimeSetup.ResyncAsync(cancellationToken);
        var after = await SntpClient.ClockOffsetSecondsAsync(host, TimeSpan.FromSeconds(4),
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
                after = await SntpClient.ClockOffsetSecondsAsync(host, TimeSpan.FromSeconds(4),
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
            OffsetBeforeSeconds = before,
            OffsetAfterSeconds = after,
            LargeStepNeeded = largeStep,
            LocalTime = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz")
        };
        return after is { } final && Math.Abs(final) < TimeSetup.OkSeconds
            ? EndpointActionResult.Done("The clock was set from the organisation time server.", data)
            : EndpointActionResult.Failed(
                "The time service is set to the organisation time server, but the clock is still wrong.", data);
    }
}

/// <summary>Windows Time service settings for a standalone PC (the same on Windows 10 and 11).</summary>
[SupportedOSPlatform("windows")]
internal static class TimeSetup
{
    /// <summary>A clock this close to the reference counts as right.</summary>
    public const double OkSeconds = 120;

    private const string Parameters = @"SYSTEM\CurrentControlSet\Services\W32Time\Parameters";
    private const string Config = @"SYSTEM\CurrentControlSet\Services\W32Time\Config";
    private const string NtpClient = @"SYSTEM\CurrentControlSet\Services\W32Time\TimeProviders\NtpClient";
    private const int DefaultPhaseLimit = 54000;
    private const int NoPhaseLimit = -1; // 0xFFFFFFFF as a DWORD

    /// <summary>"0x9" = use the special poll interval (0x1) and client mode (0x8).</summary>
    internal static string NtpServerValue(string host) => host + ",0x9";

    /// <summary>True when the registry names this server as the manual NTP source.</summary>
    public static bool UsesNtpServer(string host)
    {
        using var key = Registry.LocalMachine.OpenSubKey(Parameters);
        return Matches(key?.GetValue("Type") as string, key?.GetValue("NtpServer") as string, host);
    }

    internal static bool Matches(string? type, string? ntpServer, string host) =>
        string.Equals(type, "NTP", StringComparison.OrdinalIgnoreCase)
        && ntpServer is not null
        && ntpServer.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(part, host, StringComparison.OrdinalIgnoreCase));

    public static void Configure(string host)
    {
        using (var parameters = Registry.LocalMachine.CreateSubKey(Parameters, writable: true))
        {
            parameters.SetValue("Type", "NTP", RegistryValueKind.String);
            parameters.SetValue("NtpServer", NtpServerValue(host), RegistryValueKind.String);
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
