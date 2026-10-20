using EndpointAgents.Platform.Windows.Actions.Infrastructure;
using EndpointAgents.Shared.Actions;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace EndpointAgents.Platform.Windows.Actions.UserSide;

[SupportedOSPlatform("windows")]
public sealed class PrinterStatusAction : IEndpointAction
{
    public string Id => "printer.status";

    private static readonly Dictionary<long, string> Errors = new()
    {
        [2] = "NoError", [3] = "LowPaper", [4] = "NoPaper", [5] = "LowToner", [6] = "NoToner",
        [7] = "DoorOpen", [8] = "Jammed", [9] = "Offline", [10] = "ServiceRequested",
        [11] = "OutputBinFull"
    };

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var name = invocation.Argument("printer")!;
        var row = Wmi.Cimv2(
                $"SELECT Name, DriverName, PortName, WorkOffline, DetectedErrorState, Network FROM Win32_Printer WHERE Name='{Wmi.Literal(name)}'")
            .FirstOrDefault();
        if (row is null)
            return Task.FromResult(EndpointActionResult.Failed("That printer is not installed for the user. Use printer.list."));
        var jobs = Wmi.Cimv2("SELECT Name, JobStatus, Status FROM Win32_PrintJob")
            .Where(job => (Wmi.String(job, "Name") ?? "").Split(',')[0].Trim()
                .Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var errorJobs = jobs.Count(job => (Wmi.String(job, "JobStatus") ?? "").Contains("Error",
            StringComparison.OrdinalIgnoreCase));
        var error = Errors.GetValueOrDefault(Wmi.Long(row, "DetectedErrorState") ?? 0, "Unknown");
        var offline = Wmi.Bool(row, "WorkOffline") || error == "Offline";
        var port = Wmi.String(row, "PortName") ?? string.Empty;
        return Task.FromResult(EndpointActionResult.Ok(
            offline ? $"{name} is offline."
            : error is not ("NoError" or "Unknown") ? $"{name} reports {error}."
            : errorJobs > 0 ? $"{name} has {errorJobs} stuck job(s)."
            : $"{name} reports no problem.",
            new
            {
                Name = Wmi.String(row, "Name"),
                DriverName = Wmi.String(row, "DriverName"),
                PortKind = PortKind(port),
                Offline = offline,
                ErrorState = error,
                Jobs = jobs.Length,
                ErrorJobs = errorJobs,
                IsNetwork = Wmi.Bool(row, "Network")
            }));
    }

    private static string PortKind(string port) =>
        port.StartsWith("USB", StringComparison.OrdinalIgnoreCase) ? "USB"
        : port.StartsWith("WSD", StringComparison.OrdinalIgnoreCase) ? "WSD"
        : port.StartsWith(@"\\", StringComparison.Ordinal) ? "PrintServer"
        : port.StartsWith("IP_", StringComparison.OrdinalIgnoreCase)
          || System.Net.IPAddress.TryParse(port.Split(':')[0], out _) ? "TcpIp"
        : port.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)
          || port.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ? "Local"
        : port is "PORTPROMPT:" or "FILE:" or "nul:" ? "Virtual"
        : "Other";
}

[SupportedOSPlatform("windows")]
public sealed class WhoAmIGroupsAction : IEndpointAction
{
    public string Id => "whoami.groups";
    private const int MaxGroups = 100;

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var groups = (identity.Groups ?? [])
            .Where(group => group.Value.StartsWith("S-1-5-21-", StringComparison.Ordinal)
                || group.Value.StartsWith("S-1-5-32-", StringComparison.Ordinal))
            .Select(group =>
            {
                try { return group.Translate(typeof(NTAccount)).Value; }
                catch (IdentityNotMappedException) { return null; }
                catch (SystemException) { return null; }
            })
            .OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(MaxGroups)
            .ToArray();
        return Task.FromResult(EndpointActionResult.Ok(
            $"{identity.Name} signed in with {groups.Length} group(s).",
            new { User = identity.Name, Groups = groups, GroupCount = groups.Length }));
    }
}
