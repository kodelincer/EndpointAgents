using EndpointAgents.Platform.Windows.Actions.Infrastructure;
using EndpointAgents.Shared.Actions;
using Microsoft.Win32;
using System.Management;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;

namespace EndpointAgents.Platform.Windows.Actions.UserSide;

[SupportedOSPlatform("windows")]
internal static class Printers
{
    public sealed record Printer(string Name, bool IsDefault, bool IsNetwork, string Status,
        bool Offline);

    public static IReadOnlyList<Printer> Read() => Wmi.Cimv2(
            "SELECT Name, Default, Network, PrinterStatus, WorkOffline, DetectedErrorState FROM Win32_Printer")
        .Select(row => new Printer(Wmi.String(row, "Name") ?? "", Wmi.Bool(row, "Default"),
            Wmi.Bool(row, "Network"), StatusText(Wmi.Long(row, "PrinterStatus"),
                Wmi.Long(row, "DetectedErrorState")), Wmi.Bool(row, "WorkOffline")))
        .ToArray();

    public static Printer? Find(string name) => Read().FirstOrDefault(item =>
        item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static string WqlFor(string name) =>
        $"SELECT * FROM Win32_Printer WHERE Name='{Wmi.Literal(name)}'";

    private static string StatusText(long? status, long? error) => error switch
    {
        3 => "LowPaper",
        4 => "NoPaper",
        5 => "LowToner",
        6 => "NoToner",
        7 => "DoorOpen",
        8 => "Jammed",
        9 => "Offline",
        10 => "ServiceRequested",
        _ => status switch
        {
            3 => "Idle",
            4 => "Printing",
            5 => "WarmingUp",
            6 => "Stopped",
            7 => "Offline",
            _ => "Unknown"
        }
    };

    public static string Describe(uint code) => code switch
    {
        0 => "Success",
        5 => "Access denied (the driver may need an administrator)",
        1801 => "Printer name not valid",
        1930 => "Driver not compatible with this Windows",
        2 or 3 => "Printer not found on the server",
        1722 or 1726 => "Print server unreachable",
        _ => $"Windows error {code}"
    };
}

[SupportedOSPlatform("windows")]
public sealed class PrinterListAction : IEndpointAction
{
    public string Id => "printer.list";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var jobs = Wmi.Cimv2("SELECT Name, JobStatus, Status FROM Win32_PrintJob")
            .Select(row => (Printer: (Wmi.String(row, "Name") ?? "").Split(',')[0].Trim(),
                Error: (Wmi.String(row, "JobStatus") ?? "").Contains("Error",
                    StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(Wmi.String(row, "Status"), "OK",
                        StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var printers = Printers.Read()
            .Where(item => !IsVirtual(item.Name))
            .Select(item => new
            {
                item.Name,
                item.IsDefault,
                item.IsNetwork,
                item.Status,
                item.Offline,
                JobCount = jobs.Count(job => job.Printer.Equals(item.Name, StringComparison.OrdinalIgnoreCase)),
                ErrorJobs = jobs.Count(job => job.Error && job.Printer.Equals(item.Name, StringComparison.OrdinalIgnoreCase))
            })
            .ToArray();
        var defaultPrinter = printers.FirstOrDefault(item => item.IsDefault);
        var stuck = printers.Sum(item => item.ErrorJobs);
        return Task.FromResult(EndpointActionResult.Ok(
            printers.Length == 0 ? "No printers are installed for the user."
            : stuck > 0 ? $"{printers.Length} printers; {stuck} print job(s) are stuck."
            : $"{printers.Length} printers; default is {defaultPrinter?.Name ?? "not set"}.",
            new { Printers = printers, VirtualPrintersHidden = true }));
    }

    private static bool IsVirtual(string name) =>
        name.Contains("PDF", StringComparison.OrdinalIgnoreCase)
        || name.Contains("XPS", StringComparison.OrdinalIgnoreCase)
        || name.Contains("OneNote", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Fax", StringComparison.OrdinalIgnoreCase);
}

[SupportedOSPlatform("windows")]
public sealed class PrinterSetDefaultAction : IEndpointAction
{
    public string Id => "printer.set_default";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken) =>
        Task.FromResult(SetDefault(invocation.Argument("printer")!, withUndo: true));

    internal static EndpointActionResult SetDefault(string name, bool withUndo)
    {
        var printers = Printers.Read();
        var printer = printers.FirstOrDefault(item =>
            item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (printer is null)
            return EndpointActionResult.Blocked("That printer is not installed for the user.");
        if (printer.IsDefault)
            return EndpointActionResult.NotNeeded($"{printer.Name} is already the default printer.");
        var previous = printers.FirstOrDefault(item => item.IsDefault)?.Name;
        var code = Wmi.Invoke(@"root\cimv2", Printers.WqlFor(printer.Name), "SetDefaultPrinter");
        if (code != 0)
            return EndpointActionResult.Failed($"Setting the default printer failed ({Printers.Describe(code ?? 1)}).");
        // Windows can switch the default to the last used printer; turn that off for this user.
        using (var key = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows NT\CurrentVersion\Windows", writable: true))
            key.SetValue("LegacyDefaultPrinterMode", 1, RegistryValueKind.DWord);
        return EndpointActionResult.Done($"{printer.Name} is now the default printer.", null,
            withUndo && previous is not null
                ? new Dictionary<string, string> { ["printer"] = previous } : null);
    }
}

[SupportedOSPlatform("windows")]
public sealed class PrinterRestoreDefaultAction : IEndpointAction
{
    public string Id => "printer.restore_default";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken) =>
        Task.FromResult(PrinterSetDefaultAction.SetDefault(invocation.Argument("printer")!,
            withUndo: false));
}

[SupportedOSPlatform("windows")]
public sealed class PrinterAddAction : IEndpointAction
{
    public string Id => "printer.add";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var target = invocation.Target();
        if (Printers.Find(target.Address) is not null)
            return Task.FromResult(EndpointActionResult.NotNeeded($"{target.Label} is already connected."));
        using var printers = new ManagementClass(@"root\cimv2", "Win32_Printer", null);
        var result = printers.InvokeMethod("AddPrinterConnection", [target.Address]);
        var code = result is null ? 0u : Convert.ToUInt32(result);
        return Task.FromResult(code == 0
            ? EndpointActionResult.Done($"{target.Label} was connected.")
            : EndpointActionResult.Failed($"Connecting {target.Label} failed: {Printers.Describe(code)}.",
                new { WindowsError = code }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class PrinterRemoveAction : IEndpointAction
{
    public string Id => "printer.remove";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var printer = Printers.Find(invocation.Argument("printer")!);
        if (printer is null)
            return Task.FromResult(EndpointActionResult.NotNeeded("That printer is not installed."));
        if (!printer.IsNetwork)
            return Task.FromResult(EndpointActionResult.Blocked(
                "Only network printer connections can be removed."));
        using var searcher = new ManagementObjectSearcher(@"root\cimv2", Printers.WqlFor(printer.Name));
        foreach (ManagementObject item in searcher.Get())
            using (item) item.Delete();
        return Task.FromResult(EndpointActionResult.Done($"{printer.Name} was removed."));
    }
}

[SupportedOSPlatform("windows")]
public sealed class PrinterTestPageAction : IEndpointAction
{
    public string Id => "printer.test_page";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var printer = Printers.Find(invocation.Argument("printer")!);
        if (printer is null)
            return Task.FromResult(EndpointActionResult.Blocked("That printer is not installed."));
        var code = Wmi.Invoke(@"root\cimv2", Printers.WqlFor(printer.Name), "PrintTestPage");
        return Task.FromResult(code == 0
            ? EndpointActionResult.Done($"A test page was sent to {printer.Name}.")
            : EndpointActionResult.Failed($"Sending the test page failed ({Printers.Describe(code ?? 1)})."));
    }
}

[SupportedOSPlatform("windows")]
internal static class MappedDrives
{
    public sealed record Drive(string Letter, string Remote, bool Connected);

    public static IReadOnlyList<Drive> Read() => Wmi.Cimv2(
            "SELECT LocalName, RemoteName, ConnectionState FROM Win32_NetworkConnection")
        .Where(row => !string.IsNullOrEmpty(Wmi.String(row, "LocalName")))
        .Select(row => new Drive(Wmi.String(row, "LocalName")!.ToUpperInvariant(),
            Wmi.String(row, "RemoteName") ?? "",
            string.Equals(Wmi.String(row, "ConnectionState"), "Connected",
                StringComparison.OrdinalIgnoreCase)))
        .ToArray();

    public static string? RemoteOf(string letter)
    {
        var buffer = new StringBuilder(512);
        var length = buffer.Capacity;
        return ActionNative.WNetGetConnection(letter, buffer, ref length) == 0
            ? buffer.ToString() : null;
    }

    public static string Describe(int code) => code switch
    {
        5 => "AccessDenied",
        53 or 67 => "NotFound",
        85 => "LetterInUse",
        86 => "WrongPassword",
        1219 => "ConflictingCredentials",
        1203 or 1222 => "Unreachable",
        1326 => "SignInFailed",
        _ => $"WindowsError{code}"
    };
}

[SupportedOSPlatform("windows")]
public sealed class DrivesListAction : IEndpointAction
{
    public string Id => "drives.list";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var drives = MappedDrives.Read();
        var disconnected = drives.Count(item => !item.Connected);
        return Task.FromResult(EndpointActionResult.Ok(
            drives.Count == 0 ? "The user has no mapped network drives."
            : disconnected > 0 ? $"{drives.Count} mapped drives; {disconnected} disconnected."
            : $"{drives.Count} mapped drives, all connected.",
            new { Drives = drives.Select(item => new { item.Letter, item.Connected, Share = item.Remote }) }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class ShareTestAction : IEndpointAction
{
    public string Id => "share.test";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var target = invocation.Target();
        var host = EndpointActionValidator.HostOf(target)!;
        var port = await NetworkProbes.TestTcpAsync(host, 445, TimeSpan.FromSeconds(4),
            cancellationToken);
        if (!port.Reachable)
            return EndpointActionResult.Ok($"The server for {target.Label} is not reachable.",
                new { ServerReachable = false, Accessible = false, Error = "Unreachable" });
        var error = await Task.Run(() => Probe(target.Address), cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        return EndpointActionResult.Ok(error is null
                ? $"The user can open {target.Label}."
                : $"The user cannot open {target.Label} ({error}).",
            new { ServerReachable = true, Accessible = error is null, Error = error });
    }

    private static string? Probe(string unc)
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(unc).GetEnumerator();
            entries.MoveNext();
            return null;
        }
        catch (UnauthorizedAccessException) { return "AccessDenied"; }
        catch (DirectoryNotFoundException) { return "NotFound"; }
        catch (IOException ex) { return ex.HResult == unchecked((int)0x80070035) ? "NotFound" : "Unreachable"; }
    }
}

[SupportedOSPlatform("windows")]
public sealed class DriveRemapAction : IEndpointAction
{
    public string Id => "drive.remap";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var target = invocation.Target();
        if (target.DriveLetter is not { } letter)
            return EndpointActionResult.Blocked($"{target.Label} has no standard drive letter.");
        letter = letter.ToUpperInvariant();

        var local = DriveInfo.GetDrives().FirstOrDefault(drive =>
            drive.Name.StartsWith(letter, StringComparison.OrdinalIgnoreCase));
        if (local is not null && local.DriveType != DriveType.Network)
            return EndpointActionResult.Blocked($"{letter} is used by a local disk on this PC.");

        var current = MappedDrives.RemoteOf(letter);
        if (current is not null && current.Equals(target.Address, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(letter + "\\"))
            return EndpointActionResult.NotNeeded($"{letter} is already connected to {target.Label}.");
        if (current is not null)
            ActionNative.WNetCancelConnection2(letter, ActionNative.CONNECT_UPDATE_PROFILE, false);

        var resource = new ActionNative.NETRESOURCE
        {
            dwType = ActionNative.RESOURCETYPE_DISK,
            lpLocalName = letter,
            lpRemoteName = target.Address
        };
        var code = await Task.Run(() => ActionNative.WNetAddConnection2(ref resource, null, null,
            ActionNative.CONNECT_UPDATE_PROFILE), cancellationToken);
        return code == 0
            ? EndpointActionResult.Done($"{letter} is now connected to {target.Label}.")
            : EndpointActionResult.Failed($"Mapping {letter} failed ({MappedDrives.Describe(code)}).",
                new { Error = MappedDrives.Describe(code) });
    }
}

[SupportedOSPlatform("windows")]
public sealed class ProfileStatusAction : IEndpointAction
{
    public string Id => "profile.status";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        var backup = sid is not null && Registry.LocalMachine.OpenSubKey(
            $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}.bak") is { } key
            && Close(key);
        var temporary = backup
            || Path.GetFileName(profile).StartsWith("TEMP", StringComparison.OrdinalIgnoreCase);

        var redirected = new List<string>();
        var onOneDrive = new List<string>();
        using (var folders = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"))
            foreach (var (name, value) in new[] { ("Desktop", "Desktop"), ("Documents", "Personal"),
                         ("Pictures", "My Pictures"), ("Downloads", "{374DE290-123F-4565-9164-39C4925E467B}") })
            {
                var path = Environment.ExpandEnvironmentVariables(
                    folders?.GetValue(value, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "");
                if (path.StartsWith(@"\\", StringComparison.Ordinal)) redirected.Add(name);
                else if (path.Contains("OneDrive", StringComparison.OrdinalIgnoreCase)) onOneDrive.Add(name);
            }

        return Task.FromResult(EndpointActionResult.Ok(temporary
                ? "Windows signed the user in with a temporary profile; their files and settings are not loaded."
                : "The user profile loaded normally.",
            new
            {
                TemporaryProfile = temporary,
                RedirectedFolders = redirected,
                OneDriveFolders = onOneDrive,
                OneDriveRunning = System.Diagnostics.Process.GetProcessesByName("OneDrive").Length > 0
            }));
    }

    private static bool Close(RegistryKey key)
    {
        key.Dispose();
        return true;
    }
}
