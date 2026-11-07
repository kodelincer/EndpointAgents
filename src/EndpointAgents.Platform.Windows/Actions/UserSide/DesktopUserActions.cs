using EndpointAgents.Platform.Windows.Actions.Infrastructure;
using EndpointAgents.Shared.Actions;
using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Actions.UserSide;

/// <summary>Processes of the signed-in user's own Windows session only.</summary>
[SupportedOSPlatform("windows")]
internal static class SessionProcesses
{
    public static Process[] Named(params string[] names)
    {
        var session = Process.GetCurrentProcess().SessionId;
        return names.SelectMany(Process.GetProcessesByName)
            .Where(process => SafeSession(process) == session)
            .ToArray();
    }

    private static int SafeSession(Process process)
    {
        try { return process.SessionId; }
        catch { return -1; }
    }

    public static bool Responding(Process process)
    {
        try { return process.Responding; }
        catch { return false; }
    }

    public static void DisposeAll(IEnumerable<Process> processes)
    {
        foreach (var process in processes) process.Dispose();
    }
}

[SupportedOSPlatform("windows")]
internal static class AppFolders
{
    public static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>Deletes everything inside the folders (not the folders), skipping locked files.</summary>
    public static long Empty(IEnumerable<string> folders, CancellationToken cancellationToken)
    {
        long freed = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        foreach (var folder in folders.Where(Directory.Exists))
            foreach (var file in Directory.EnumerateFiles(folder, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(file);
                    var length = info.Length;
                    info.Delete();
                    freed += length;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        return freed;
    }

    public static IEnumerable<string> ChromiumProfiles(string userData) =>
        Directory.Exists(userData)
            ? Directory.EnumerateDirectories(userData).Where(path =>
                Path.GetFileName(path) is "Default" or "Guest Profile"
                || Path.GetFileName(path).StartsWith("Profile ", StringComparison.Ordinal))
            : [];
}

[SupportedOSPlatform("windows")]
public sealed class BrowserInventoryAction : IEndpointAction
{
    public string Id => "browser.inventory";

    private static readonly (string Name, string Exe, string Process)[] Browsers =
    [
        ("Google Chrome", "chrome.exe", "chrome"),
        ("Microsoft Edge", "msedge.exe", "msedge"),
        ("Firefox", "firefox.exe", "firefox")
    ];

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var browsers = Browsers.Select(browser =>
        {
            var path = AppPath(browser.Exe);
            var running = SessionProcesses.Named(browser.Process);
            var result = new
            {
                browser.Name,
                Installed = path is not null,
                Version = path is null ? null : FileVersionInfo.GetVersionInfo(path).ProductVersion,
                Running = running.Length > 0
            };
            SessionProcesses.DisposeAll(running);
            return result;
        }).Where(item => item.Installed).ToArray();
        using var choice = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
        var progId = choice?.GetValue("ProgId") as string ?? "";
        var defaultBrowser = progId.StartsWith("Chrome", StringComparison.OrdinalIgnoreCase) ? "Google Chrome"
            : progId.StartsWith("MSEdge", StringComparison.OrdinalIgnoreCase) ? "Microsoft Edge"
            : progId.StartsWith("Firefox", StringComparison.OrdinalIgnoreCase) ? "Firefox"
            : progId.Length == 0 ? null : "Other";
        return Task.FromResult(EndpointActionResult.Ok(
            $"{browsers.Length} browser(s) installed; default is {defaultBrowser ?? "not set"}.",
            new { Browsers = browsers, DefaultBrowser = defaultBrowser }));
    }

    private static string? AppPath(string exe)
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
            if (key?.GetValue(null) is string path && File.Exists(path.Trim('"'))) return path.Trim('"');
        }
        return null;
    }
}

[SupportedOSPlatform("windows")]
public sealed class BrowserClearCacheAction : IEndpointAction
{
    public string Id => "browser.clear_cache";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var browser = invocation.Argument("browser")!;
        var (process, folders) = browser switch
        {
            "chrome" => ("chrome", CacheFolders(Path.Combine(AppFolders.Local, "Google", "Chrome", "User Data"))),
            "edge" => ("msedge", CacheFolders(Path.Combine(AppFolders.Local, "Microsoft", "Edge", "User Data"))),
            _ => ("firefox", FirefoxCaches())
        };
        var running = SessionProcesses.Named(process);
        var open = running.Length > 0;
        SessionProcesses.DisposeAll(running);
        if (open)
            return Task.FromResult(EndpointActionResult.Blocked(
                "The browser is open; it must be closed before its cache can be cleared."));
        var freed = AppFolders.Empty(folders, cancellationToken);
        return Task.FromResult(EndpointActionResult.Done(
            $"The browser cache was cleared ({freed / 1024d / 1024:0} MB).",
            new { FreedMb = Math.Round(freed / 1024d / 1024, 0) }));
    }

    private static IEnumerable<string> CacheFolders(string userData) =>
        AppFolders.ChromiumProfiles(userData).SelectMany(profile => new[]
        {
            Path.Combine(profile, "Cache"),
            Path.Combine(profile, "Code Cache"),
            Path.Combine(profile, "GPUCache"),
            Path.Combine(profile, "Service Worker", "CacheStorage")
        });

    private static IEnumerable<string> FirefoxCaches()
    {
        var profiles = Path.Combine(AppFolders.Local, "Mozilla", "Firefox", "Profiles");
        return Directory.Exists(profiles)
            ? Directory.EnumerateDirectories(profiles).Select(path => Path.Combine(path, "cache2"))
            : [];
    }
}

[SupportedOSPlatform("windows")]
public sealed class OutlookStatusAction : IEndpointAction
{
    public string Id => "outlook.status";

    private sealed record DataFile(string Name, string Kind, double SizeGb, bool NearLimit);

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var running = SessionProcesses.Named("OUTLOOK");
        var isRunning = running.Length > 0;
        var responding = running.All(SessionProcesses.Responding);
        SessionProcesses.DisposeAll(running);

        using var profiles = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Office\16.0\Outlook\Profiles");
        var dataFiles = new List<DataFile>();
        foreach (var (folder, pattern, kind) in new[]
                 {
                     (Path.Combine(AppFolders.Local, "Microsoft", "Outlook"), "*.ost", "OST"),
                     (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Outlook Files"), "*.pst", "PST")
                 })
            if (Directory.Exists(folder))
                foreach (var file in Directory.EnumerateFiles(folder, pattern))
                {
                    var sizeGb = Math.Round(new FileInfo(file).Length / 1024d / 1024 / 1024, 1);
                    dataFiles.Add(new DataFile(Path.GetFileName(file), kind, sizeGb, sizeGb >= 45));
                }

        var addins = new List<object>();
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = root.OpenSubKey(@"Software\Microsoft\Office\Outlook\Addins");
            foreach (var name in key?.GetSubKeyNames() ?? [])
            {
                using var addin = key!.OpenSubKey(name);
                var behavior = addin?.GetValue("LoadBehavior") as int?;
                addins.Add(new
                {
                    Name = addin?.GetValue("FriendlyName") as string ?? name,
                    LoadBehavior = behavior,
                    Active = behavior is 3 or 9 or 16
                });
            }
        }
        using var disabled = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Office\16.0\Outlook\Resiliency\DisabledItems");

        return Task.FromResult(EndpointActionResult.Ok(
            isRunning && !responding ? "Outlook is open but not responding."
            : dataFiles.Any(item => item.NearLimit) ? "An Outlook data file is close to its size limit."
            : "Outlook status read.",
            new
            {
                Running = isRunning,
                Responding = !isRunning || responding,
                ProfileCount = profiles?.SubKeyCount ?? 0,
                DataFiles = dataFiles,
                Addins = addins.Take(20),
                CrashDisabledItems = disabled?.ValueCount ?? 0
            }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class ExplorerStatusAction : IEndpointAction
{
    public string Id => "explorer.status";

    private sealed record ShellPart(bool Running, bool Responding);

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        ShellPart Describe(params string[] names)
        {
            var processes = SessionProcesses.Named(names);
            var result = new ShellPart(processes.Length > 0,
                processes.Length > 0 && processes.All(SessionProcesses.Responding));
            SessionProcesses.DisposeAll(processes);
            return result;
        }
        var explorer = Describe("explorer");
        var start = Describe("StartMenuExperienceHost");
        var search = Describe("SearchHost", "SearchApp");
        var shellOk = explorer.Running && explorer.Responding;
        return Task.FromResult(EndpointActionResult.Ok(shellOk
                ? "The desktop and taskbar are running." : "The desktop (Explorer) is not running or not responding.",
            new { Explorer = explorer, StartMenu = start, Search = search }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class ExplorerRestartAction : IEndpointAction
{
    public string Id => "explorer.restart";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        foreach (var process in SessionProcesses.Named("explorer"))
            using (process)
            {
                try { process.Kill(); } catch { }
            }
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        var running = SessionProcesses.Named("explorer");
        var restarted = running.Length > 0;
        SessionProcesses.DisposeAll(running);
        if (!restarted)
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "explorer.exe"),
                UseShellExecute = false
            })?.Dispose();
        return EndpointActionResult.Done("The desktop and taskbar were restarted.");
    }
}

[SupportedOSPlatform("windows")]
public sealed class InputLanguagesAction : IEndpointAction
{
    public string Id => "input.languages";

    internal static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["00000429"] = "Persian",
        ["00050429"] = "Persian Standard",
        ["00000409"] = "English (US)",
        ["00000809"] = "English (UK)"
    };

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var layouts = new List<string>();
        using (var profile = Registry.CurrentUser.OpenSubKey(@"Control Panel\International\User Profile"))
            foreach (var language in profile?.GetSubKeyNames() ?? [])
            {
                using var key = profile!.OpenSubKey(language);
                foreach (var value in key?.GetValueNames() ?? [])
                    if (value.Contains(':'))
                        layouts.Add(value.Split(':')[1]);
            }
        if (layouts.Count == 0)
            using (var preload = Registry.CurrentUser.OpenSubKey(@"Keyboard Layout\Preload"))
                foreach (var value in preload?.GetValueNames() ?? [])
                    if (preload!.GetValue(value) is string id) layouts.Add(id);
        using var toggle = Registry.CurrentUser.OpenSubKey(@"Keyboard Layout\Toggle");
        var hotkey = ((toggle?.GetValue("Language Hotkey") ?? toggle?.GetValue("Hotkey")) as string) switch
        {
            "1" => "Alt+Shift",
            "2" => "Ctrl+Shift",
            "3" => "None",
            "4" => "Grave accent",
            _ => "Windows+Space"
        };
        var names = layouts.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => Known.GetValueOrDefault(id, id)).ToArray();
        var persian = layouts.Any(id => id.EndsWith("0429", StringComparison.OrdinalIgnoreCase));
        var english = layouts.Any(id => id.EndsWith("0409", StringComparison.OrdinalIgnoreCase)
            || id.EndsWith("0809", StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(EndpointActionResult.Ok(
            !persian ? "The Persian keyboard is not installed."
            : !english ? "The English keyboard is not installed."
            : $"Persian and English keyboards are installed; switch with {hotkey}.",
            new { Layouts = names, HasPersian = persian, HasEnglish = english, SwitchHotkey = hotkey }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class InputAddLayoutAction : IEndpointAction
{
    public string Id => "input.add_layout";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var profile = invocation.Argument("layout") switch
        {
            "persian" => "0429:00000429",
            "persian-standard" => "0429:00050429",
            _ => "0409:00000409"
        };
        return Task.FromResult(ActionNative.InstallLayoutOrTip(profile, 0)
            ? EndpointActionResult.Done("The keyboard layout was added.")
            : EndpointActionResult.Failed("Windows did not add the keyboard layout."));
    }
}

[SupportedOSPlatform("windows")]
public sealed class RegionStatusAction : IEndpointAction
{
    public string Id => "region.status";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\International");
        var calendar = (key?.GetValue("iCalendarType") as string) switch
        {
            "1" => "Gregorian",
            "22" => "Persian",
            "6" => "Hijri",
            null => null,
            var other => $"Other ({other})"
        };
        return Task.FromResult(EndpointActionResult.Ok(
            $"Region format {key?.GetValue("LocaleName")}, calendar {calendar ?? "default"}.",
            new
            {
                Locale = key?.GetValue("LocaleName") as string,
                Calendar = calendar,
                ShortDate = key?.GetValue("sShortDate") as string,
                DecimalSymbol = key?.GetValue("sDecimal") as string,
                DigitSubstitution = key?.GetValue("NumShape") as string
            }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class DisplayStatusAction : IEndpointAction
{
    public string Id => "display.status";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var adapters = Wmi.Cimv2(
                "SELECT Name, CurrentHorizontalResolution, CurrentVerticalResolution, DriverVersion, DriverDate, Status FROM Win32_VideoController")
            .Select(row => new
            {
                Name = Wmi.String(row, "Name"),
                Resolution = Wmi.Long(row, "CurrentHorizontalResolution") is { } width
                    ? $"{width}x{Wmi.Long(row, "CurrentVerticalResolution")}" : null,
                DriverVersion = Wmi.String(row, "DriverVersion"),
                DriverDate = Wmi.Date(row, "DriverDate")?.ToString("yyyy-MM-dd"),
                Status = Wmi.String(row, "Status")
            }).ToArray();
        int? monitors = null;
        try { monitors = Wmi.Query(@"root\wmi", "SELECT Active FROM WmiMonitorBasicDisplayParams").Count; }
        catch { }
        return Task.FromResult(EndpointActionResult.Ok(
            $"{monitors?.ToString() ?? "Unknown number of"} monitor(s) on {adapters.Length} graphics adapter(s).",
            new { Monitors = monitors, Adapters = adapters }));
    }
}

[SupportedOSPlatform("windows")]
internal static class Privacy
{
    private const string Store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    public sealed record State(bool AllowedForUser, bool AllowedOnDevice, bool BlockedByPolicy);

    public static State Read(string capability, string policyValue)
    {
        using var user = Registry.CurrentUser.OpenSubKey($@"{Store}\{capability}");
        using var device = Registry.LocalMachine.OpenSubKey($@"{Store}\{capability}");
        using var policy = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy");
        return new State(
            !string.Equals(user?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase),
            !string.Equals(device?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase),
            policy?.GetValue(policyValue) is 2);
    }

    public static void Allow(string capability)
    {
        foreach (var path in new[] { $@"{Store}\{capability}", $@"{Store}\{capability}\NonPackaged" })
        {
            using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
            key.SetValue("Value", "Allow", RegistryValueKind.String);
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class MediaStatusAction : IEndpointAction
{
    public string Id => "media.status";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var camera = Privacy.Read("webcam", "LetAppsAccessCamera");
        var microphone = Privacy.Read("microphone", "LetAppsAccessMicrophone");
        var audio = Wmi.Cimv2("SELECT Name, Status FROM Win32_SoundDevice")
            .Select(row => new { Name = Wmi.String(row, "Name"), Status = Wmi.String(row, "Status") })
            .ToArray();
        var cameraOk = camera.AllowedForUser && camera.AllowedOnDevice && !camera.BlockedByPolicy;
        var micOk = microphone.AllowedForUser && microphone.AllowedOnDevice && !microphone.BlockedByPolicy;
        return Task.FromResult(EndpointActionResult.Ok(
            cameraOk && micOk ? "Apps may use the camera and microphone."
            : $"Blocked by Windows privacy settings: {string.Join(" and ", new[] { cameraOk ? null : "camera", micOk ? null : "microphone" }.Where(item => item is not null))}.",
            new { Camera = camera, Microphone = microphone, AudioDevices = audio }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class AllowCameraMicAction : IEndpointAction
{
    public string Id => "privacy.allow_camera_mic";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var device = invocation.Argument("device")!;
        var targets = device switch
        {
            "camera" => new[] { ("webcam", "LetAppsAccessCamera") },
            "microphone" => [("microphone", "LetAppsAccessMicrophone")],
            _ => [("webcam", "LetAppsAccessCamera"), ("microphone", "LetAppsAccessMicrophone")]
        };
        var changed = false;
        foreach (var (capability, policy) in targets)
        {
            var state = Privacy.Read(capability, policy);
            if (state.BlockedByPolicy || !state.AllowedOnDevice)
                return Task.FromResult(EndpointActionResult.Blocked(
                    "The organisation or this PC blocks it; the helpdesk must change that."));
            if (state.AllowedForUser) continue;
            Privacy.Allow(capability);
            changed = true;
        }
        return Task.FromResult(changed
            ? EndpointActionResult.Done("Apps may now use it.")
            : EndpointActionResult.NotNeeded("It was already allowed."));
    }
}

[SupportedOSPlatform("windows")]
public sealed class AppCloseAction : IEndpointAction
{
    public string Id => "app.close";

    private static readonly Dictionary<string, string[]> Processes = new()
    {
        ["outlook"] = ["OUTLOOK"],
        ["word"] = ["WINWORD"],
        ["excel"] = ["EXCEL"],
        ["powerpoint"] = ["POWERPNT"],
        ["chrome"] = ["chrome"],
        ["edge"] = ["msedge"],
        ["firefox"] = ["firefox"]
    };

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var names = Processes[invocation.Argument("app")!];
        var running = SessionProcesses.Named(names);
        if (running.Length == 0)
            return EndpointActionResult.NotNeeded("The app is not open.");
        foreach (var process in running)
        {
            try { if (process.MainWindowHandle != IntPtr.Zero) process.CloseMainWindow(); }
            catch { }
        }
        SessionProcesses.DisposeAll(running);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000, cancellationToken);
            var left = SessionProcesses.Named(names);
            var open = left.Any(process => process.MainWindowHandle != IntPtr.Zero);
            SessionProcesses.DisposeAll(left);
            if (!open) return EndpointActionResult.Done("The app was closed.");
        }
        return EndpointActionResult.Blocked(
            "The app is still open, maybe waiting to save a document; the user must close it.");
    }
}
