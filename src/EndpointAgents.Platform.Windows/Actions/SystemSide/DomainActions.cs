using EndpointAgents.Platform.Windows.Actions.Infrastructure;
using EndpointAgents.Shared.Actions;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace EndpointAgents.Platform.Windows.Actions.SystemSide;

[SupportedOSPlatform("windows")]
internal static class DomainFacts
{
    public static bool? SecureChannelOk(string domain)
    {
        var name = Marshal.StringToHGlobalUni(domain);
        try
        {
            var status = ActionNative.I_NetLogonControl2(null,
                ActionNative.NETLOGON_CONTROL_TC_QUERY, 2, ref name, out var buffer);
            if (status != 0) return status == 1787 ? false : null; // no trust secret
            try
            {
                var info = Marshal.PtrToStructure<ActionNative.NETLOGON_INFO_2>(buffer);
                return info.netlog2_tc_connection_status == 0;
            }
            finally
            {
                ActionNative.NetApiBufferFree(buffer);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }
    }

    public static DateTimeOffset? LastComputerPolicyRefreshUtc()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\State\Machine\Extension-List\{00000000-0000-0000-0000-000000000000}");
        if (key?.GetValue("EndTimeHi") is not int high || key.GetValue("EndTimeLo") is not int low)
            return null;
        var fileTime = ((long)(uint)high << 32) | (uint)low;
        try { return DateTimeOffset.FromFileTime(fileTime).ToUniversalTime(); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    public static int PolicyErrorsLastDay() => WindowsEvents.Read(
        "Microsoft-Windows-GroupPolicy/Operational",
        $"*[System[Level<=2 and {WindowsEvents.Since(1)}]]", 200, includeMessage: false).Count;

    public static string ServiceState(string name)
    {
        try
        {
            using var service = new ServiceController(name);
            return service.Status.ToString();
        }
        catch (InvalidOperationException)
        {
            return "NotInstalled";
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class DomainHealthAction : IEndpointAction
{
    public string Id => "domain.health";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var domain = DomainLookup.JoinedDomain();
        if (domain is null)
            return EndpointActionResult.Ok("This PC is not joined to a domain.",
                new { Joined = false });

        var controller = await DomainLookup.LocateAsync(domain, forceRediscovery: false,
            cancellationToken);
        var secureChannel = await Task.Run(() => DomainFacts.SecureChannelOk(domain),
            cancellationToken).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        var ports = new Dictionary<string, bool>();
        double? offset = null;
        if (controller.Found && controller.Address is { } address)
        {
            foreach (var port in new[] { 88, 389, 445, 135 })
                ports[port.ToString()] = (await NetworkProbes.TestTcpAsync(address, port,
                    TimeSpan.FromSeconds(3), cancellationToken)).Reachable;
            offset = await NetworkProbes.ClockOffsetSecondsAsync(address,
                TimeSpan.FromSeconds(3), cancellationToken);
        }
        var clockOk = offset is null ? (bool?)null : Math.Abs(offset.Value) < 300;
        var lastRefresh = DomainFacts.LastComputerPolicyRefreshUtc();
        var errors = DomainFacts.PolicyErrorsLastDay();

        var problems = new List<string>();
        if (!controller.Found) problems.Add($"no domain controller found ({controller.Error})");
        if (secureChannel == false) problems.Add("the computer's trust with the domain is broken");
        if (ports.Any(item => !item.Value))
            problems.Add("some domain controller ports are blocked");
        if (clockOk == false) problems.Add("the clock differs from the domain by more than 5 minutes");
        return EndpointActionResult.Ok(problems.Count == 0
                ? "The domain connection is healthy."
                : "Domain problems: " + string.Join("; ", problems) + ".",
            new
            {
                Joined = true,
                SecureChannelOk = secureChannel,
                DomainControllerFound = controller.Found,
                DomainControllerError = controller.Error,
                Ports = ports,
                ClockOffsetSeconds = offset,
                ClockOk = clockOk,
                LastPolicyRefreshUtc = lastRefresh,
                PolicyErrorsLast24h = errors
            });
    }
}

[SupportedOSPlatform("windows")]
public sealed class TimeStatusAction : IEndpointAction
{
    public string Id => "time.status";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var standard = invocation.Setting(ActionSettings.StandardTimeZone);
        var zone = TimeZoneInfo.Local.Id;
        double? offset = null;
        if (DomainLookup.JoinedDomain() is { } domain)
        {
            var controller = await DomainLookup.LocateAsync(domain, false, cancellationToken);
            if (controller.Address is { } address)
                offset = await NetworkProbes.ClockOffsetSecondsAsync(address,
                    TimeSpan.FromSeconds(3), cancellationToken);
        }
        var service = DomainFacts.ServiceState("W32Time");
        bool? matches = standard is null ? null
            : zone.Equals(standard, StringComparison.OrdinalIgnoreCase);
        bool? clockOk = offset is null ? null : Math.Abs(offset.Value) < 120;
        return EndpointActionResult.Ok(
            matches == false ? "The time zone is not the organisation standard."
            : clockOk == false ? "The clock is off compared with the domain."
            : "The clock and time zone look right.",
            new
            {
                LocalTime = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
                TimeZone = zone,
                MatchesStandardZone = matches,
                TimeServiceState = service,
                ClockOffsetSeconds = offset,
                ClockOk = clockOk
            });
    }
}

[SupportedOSPlatform("windows")]
public sealed class TimeResyncAction : IEndpointAction
{
    public string Id => "time.resync";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        await ServiceTools.EnsureRunningAsync("W32Time", TimeSpan.FromSeconds(15),
            cancellationToken);
        var result = await FixedTool.RunAsync("w32tm.exe", ["/resync", "/force"],
            TimeSpan.FromSeconds(30), cancellationToken);
        return result.Succeeded
            ? EndpointActionResult.Done("The clock was synchronised.")
            : EndpointActionResult.Failed("The time server did not answer.");
    }
}

[SupportedOSPlatform("windows")]
public sealed class ComputerPolicyRefreshAction : IEndpointAction
{
    public string Id => "gpo.refresh_computer";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (DomainLookup.JoinedDomain() is null)
            return EndpointActionResult.NotNeeded("This PC is not in a domain.");
        // "N" answers any sign-out or restart question, so nothing interrupts the user.
        var result = await FixedTool.RunAsync("gpupdate.exe",
            ["/target:computer", "/force", "/wait:120"], TimeSpan.FromSeconds(140),
            cancellationToken, standardInput: "N\r\nN\r\n");
        return result.Succeeded
            ? EndpointActionResult.Done("Computer policy was applied.",
                new { LastPolicyRefreshUtc = DomainFacts.LastComputerPolicyRefreshUtc() })
            : EndpointActionResult.Failed(result.TimedOut
                ? "Applying computer policy took too long."
                : "Applying computer policy reported errors.",
                new { ErrorsLast24h = DomainFacts.PolicyErrorsLastDay() });
    }
}
