using EndpointAgents.Platform.Windows.Actions.Infrastructure;
using EndpointAgents.Shared.Actions;
using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Actions.UserSide;

[SupportedOSPlatform("windows")]
public sealed class SiteCheckAccessAction : IEndpointAction
{
    public string Id => "site.check_access";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var target = invocation.Target();
        var host = EndpointActionValidator.SiteHost(target.Address);
        var result = await WebProbe.ProbeAsync($"https://{host}/", useDefaultCredentials: false,
            cancellationToken);
        var blocked = result.StatusCode is 403 or 407 or 451;
        var summary = !result.Reachable ? $"{target.Label} did not answer ({result.Error})."
            : blocked ? $"{target.Label} was refused (HTTP {result.StatusCode}); a proxy or filter may block it."
            : result.CertificateValid == false ? $"{target.Label} answers, but with a certificate the PC does not trust (often a filter in between)."
            : $"{target.Label} opens normally ({result.ElapsedMs} ms).";
        return EndpointActionResult.Ok(summary, new
        {
            Host = host,
            result.Reachable,
            result.StatusCode,
            Milliseconds = result.ElapsedMs,
            ViaProxy = result.UsedProxy,
            LooksBlocked = blocked || result.CertificateValid == false,
            result.Error
        });
    }
}

/// <summary>The user's Internet Options zone map (Local intranet = 1, Trusted = 2).</summary>
[SupportedOSPlatform("windows")]
internal static class ZoneMap
{
    private const string UserDomains = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings\ZoneMap\Domains";
    private const string PolicyList = @"Software\Policies\Microsoft\Windows\CurrentVersion\Internet Settings\ZoneMapKey";

    public sealed record Entry(string Host, string Zone, bool SetByPolicy);

    public static bool PolicyManaged()
    {
        using var user = Registry.CurrentUser.OpenSubKey(PolicyList);
        using var machine = Registry.LocalMachine.OpenSubKey(PolicyList);
        return (user?.ValueCount ?? 0) + (machine?.ValueCount ?? 0) > 0;
    }

    public static IReadOnlyList<Entry> Read()
    {
        var entries = new List<Entry>();
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var policy = root.OpenSubKey(PolicyList);
            foreach (var name in policy?.GetValueNames() ?? [])
                entries.Add(new Entry(name, ZoneName(policy!.GetValue(name)), true));
        }
        using var domains = Registry.CurrentUser.OpenSubKey(UserDomains);
        foreach (var domain in domains?.GetSubKeyNames() ?? [])
        {
            using var key = domains!.OpenSubKey(domain);
            if (key is null) continue;
            if (FirstZone(key) is { } zone) entries.Add(new Entry(domain, zone, false));
            foreach (var sub in key.GetSubKeyNames().Take(50))
            {
                using var child = key.OpenSubKey(sub);
                if (child is not null && FirstZone(child) is { } childZone)
                    entries.Add(new Entry($"{sub}.{domain}", childZone, false));
            }
        }
        return entries.Take(100).ToArray();
    }

    public static void Add(string host, string scheme)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{UserDomains}\{host}", writable: true);
        key.SetValue(scheme, 1, RegistryValueKind.DWord);
    }

    public static bool Remove(string host)
    {
        using var domains = Registry.CurrentUser.OpenSubKey(UserDomains, writable: true);
        if (domains?.OpenSubKey(host) is not { } existing) return false;
        existing.Dispose();
        domains.DeleteSubKeyTree(host, throwOnMissingSubKey: false);
        return true;
    }

    private static string? FirstZone(RegistryKey key) =>
        new[] { "*", "https", "http" }.Select(name => key.GetValue(name)).OfType<int>().Select(value => ZoneName(value))
            .FirstOrDefault();

    private static string ZoneName(object? value) => value?.ToString()?.Trim() switch
    {
        "0" => "Computer", "1" => "Intranet", "2" => "Trusted", "3" => "Internet", "4" => "Restricted",
        _ => "Unknown"
    };
}

[SupportedOSPlatform("windows")]
public sealed class ZonesStatusAction : IEndpointAction
{
    public string Id => "zones.status";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var entries = ZoneMap.Read();
        // The control plane matches these entries with the internal web apps (Sites).
        return Task.FromResult(EndpointActionResult.Ok(
            ZoneMap.PolicyManaged() ? "The intranet zone list is set by policy." : $"{entries.Count} zone entries read.",
            new { Entries = entries, PolicyManaged = ZoneMap.PolicyManaged() }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class ZonesAddCompanySiteAction : IEndpointAction
{
    public string Id => "zones.add_company_site";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var target = invocation.Target();
        if (!Uri.TryCreate(target.Address, UriKind.Absolute, out var uri))
            return Task.FromResult(EndpointActionResult.Failed("The web app address is not valid."));
        if (ZoneMap.PolicyManaged())
            return Task.FromResult(EndpointActionResult.Blocked(
                "Group Policy manages the intranet zone list; the helpdesk must add the site there."));
        if (ZoneMap.Read().Any(entry => entry.Zone == "Intranet"
                && string.Equals(entry.Host, uri.Host, StringComparison.OrdinalIgnoreCase)))
            return Task.FromResult(EndpointActionResult.NotNeeded($"{target.Label} is already in Local intranet."));
        ZoneMap.Add(uri.Host, uri.Scheme);
        return Task.FromResult(EndpointActionResult.Done(
            $"{target.Label} was added to Local intranet; the browser must be reopened.",
            new { Target = target.Id },
            new Dictionary<string, string> { ["host"] = uri.Host }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class ZonesRemoveCompanySiteAction : IEndpointAction
{
    public string Id => "zones.remove_company_site";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var host = invocation.Argument("host")!;
        if (!EndpointActionValidator.IsValidHost(host))
            return Task.FromResult(EndpointActionResult.Failed("The saved host is not valid."));
        return Task.FromResult(ZoneMap.Remove(host)
            ? EndpointActionResult.Done("The site was removed from Local intranet.")
            : EndpointActionResult.NotNeeded("The site is not in the list."));
    }
}

/// <summary>Programs that start at sign-in, with the Task Manager on/off state.</summary>
[SupportedOSPlatform("windows")]
internal static class StartupItems
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public sealed record Item(string Name, string Scope, bool Enabled, bool CanTurnOff, string? Publisher);

    public static IReadOnlyList<Item> Read()
    {
        var items = new List<Item>();
        using (var user = Registry.CurrentUser.OpenSubKey(Run))
            foreach (var name in user?.GetValueNames() ?? [])
                items.Add(new Item(name, "User", IsEnabled(Registry.CurrentUser, "Run", name), true,
                    Publisher(user!.GetValue(name) as string)));
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(Run);
            var approved = view == RegistryView.Registry64 ? "Run" : "Run32";
            foreach (var name in machine?.GetValueNames() ?? [])
                if (!items.Any(item => item.Name == name))
                    items.Add(new Item(name, "Machine", IsEnabled(Registry.LocalMachine, approved, name)
                        && IsEnabled(Registry.CurrentUser, approved, name), false,
                        Publisher(machine!.GetValue(name) as string)));
        }
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (Directory.Exists(folder))
            foreach (var file in Directory.EnumerateFiles(folder, "*.lnk").Take(30))
                items.Add(new Item(Path.GetFileName(file), "User",
                    IsEnabled(Registry.CurrentUser, "StartupFolder", Path.GetFileName(file)), true, null));
        return items.Take(60).ToArray();
    }

    public static string? FolderOf(Item item) =>
        item.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? "StartupFolder" : "Run";

    /// <summary>Sets the Task Manager state for one of the user's own items.</summary>
    public static void SetEnabled(Item item, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Approved + FolderOf(item), writable: true);
        var value = new byte[12];
        value[0] = (byte)(enabled ? 2 : 3);
        if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(value, 4);
        key.SetValue(item.Name, value, RegistryValueKind.Binary);
    }

    private static bool IsEnabled(RegistryKey root, string folder, string name)
    {
        using var key = root.OpenSubKey(Approved + folder);
        return key?.GetValue(name) is not byte[] { Length: > 0 } value || value[0] % 2 == 0;
    }

    private static string? Publisher(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var text = Environment.ExpandEnvironmentVariables(command.Trim());
        var path = text.StartsWith('"') ? text[1..].Split('"')[0]
            : text[..(text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) is var end and > 0 ? end + 4 : text.Length)];
        try { return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).CompanyName?.Trim() : null; }
        catch { return null; }
    }
}

[SupportedOSPlatform("windows")]
public sealed class StartupListAction : IEndpointAction
{
    public string Id => "startup.list";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var items = StartupItems.Read();
        var enabled = items.Count(item => item.Enabled);
        return Task.FromResult(EndpointActionResult.Ok(
            $"{enabled} of {items.Count} startup apps are turned on.",
            new { Items = items, Count = items.Count, EnabledCount = enabled }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class StartupDisableAction : IEndpointAction
{
    public string Id => "startup.disable";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken) => Task.FromResult(Set(invocation.Argument("item")!, false));

    internal static EndpointActionResult Set(string name, bool enabled)
    {
        var item = StartupItems.Read().FirstOrDefault(entry => entry.Name == name);
        if (item is null)
            return EndpointActionResult.Failed("That startup app is not on this PC. Use startup.list.");
        if (!item.CanTurnOff)
            return EndpointActionResult.Blocked($"{name} starts for every user; the helpdesk manages it.");
        if (item.Enabled == enabled)
            return EndpointActionResult.NotNeeded($"{name} is already {(enabled ? "on" : "off")}.");
        StartupItems.SetEnabled(item, enabled);
        return EndpointActionResult.Done(
            $"{name} will {(enabled ? "" : "no longer ")}start when the user signs in.",
            null, enabled ? null : new Dictionary<string, string> { ["item"] = name });
    }
}

[SupportedOSPlatform("windows")]
public sealed class StartupEnableAction : IEndpointAction
{
    public string Id => "startup.enable";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken) =>
        Task.FromResult(StartupDisableAction.Set(invocation.Argument("item")!, true));
}

[SupportedOSPlatform("windows")]
public sealed class RecycleBinEmptyAction : IEndpointAction
{
    public string Id => "recyclebin.empty";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var info = new RecycleBinInfo { Size = Marshal.SizeOf<RecycleBinInfo>() };
        SHQueryRecycleBin(null, ref info);
        if (info.Items == 0)
            return Task.FromResult(EndpointActionResult.NotNeeded("The recycle bin is already empty."));
        // SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND
        var result = SHEmptyRecycleBin(IntPtr.Zero, null, 0x7);
        var freed = Math.Round(info.Bytes / 1024d / 1024, 0);
        return Task.FromResult(result == 0
            ? EndpointActionResult.Done($"The recycle bin was emptied ({freed} MB).", new { FreedMb = freed })
            : EndpointActionResult.Failed($"Windows could not empty the recycle bin (code {result})."));
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct RecycleBinInfo
    {
        public int Size;
        public long Bytes;
        public long Items;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? rootPath, ref RecycleBinInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr window, string? rootPath, int flags);
}

/// <summary>COM add-ins of the Office desktop apps, from the registry.</summary>
[SupportedOSPlatform("windows")]
internal static class OfficeAddins
{
    public sealed record Addin(string Id, string Name, bool LoadsAtStart, string Scope, bool CanTurnOff,
        int? LoadBehavior);

    public static string Folder(string app) => app switch
    {
        "outlook" => "Outlook", "word" => "Word", "excel" => "Excel", "powerpoint" => "PowerPoint",
        _ => throw new ArgumentException("Unknown Office app.")
    };

    public static string Process(string app) => app switch
    {
        "outlook" => "OUTLOOK", "word" => "WINWORD", "excel" => "EXCEL", _ => "POWERPNT"
    };

    public static string UserKey(string app) => $@"Software\Microsoft\Office\{Folder(app)}\Addins";

    public static IReadOnlyList<Addin> Read(string app)
    {
        var addins = new List<Addin>();
        using (var user = Registry.CurrentUser.OpenSubKey(UserKey(app)))
            foreach (var id in user?.GetSubKeyNames() ?? [])
                addins.Add(Make(user!, id, "User", true));
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(UserKey(app));
            foreach (var id in machine?.GetSubKeyNames() ?? [])
                if (!addins.Any(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)))
                    addins.Add(Make(machine!, id, "Machine", false));
        }
        return addins.Take(40).ToArray();
    }

    public static void SetLoadBehavior(string app, string id, int value)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{UserKey(app)}\{id}", writable: true)
            ?? throw new InvalidOperationException("The add-in is not the user's.");
        key.SetValue("LoadBehavior", value, RegistryValueKind.DWord);
    }

    private static Addin Make(RegistryKey parent, string id, string scope, bool canTurnOff)
    {
        using var key = parent.OpenSubKey(id);
        var behavior = key?.GetValue("LoadBehavior") as int?;
        return new Addin(id, key?.GetValue("FriendlyName") as string ?? id, behavior is 3 or 9 or 16,
            scope, canTurnOff, behavior);
    }
}

[SupportedOSPlatform("windows")]
public sealed class OfficeAddinsListAction : IEndpointAction
{
    public string Id => "office.addins_list";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var app = invocation.Argument("app")!;
        var addins = OfficeAddins.Read(app);
        var running = SessionProcesses.Named(OfficeAddins.Process(app));
        var isRunning = running.Length > 0;
        SessionProcesses.DisposeAll(running);
        using var disabled = Registry.CurrentUser.OpenSubKey(
            $@"Software\Microsoft\Office\16.0\{OfficeAddins.Folder(app)}\Resiliency\DisabledItems");
        return Task.FromResult(EndpointActionResult.Ok(
            $"{addins.Count(item => item.LoadsAtStart)} of {addins.Count} {OfficeAddins.Folder(app)} add-ins load at start.",
            new { Addins = addins, DisabledByOfficeCount = disabled?.ValueCount ?? 0, Running = isRunning }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class OfficeAddinDisableAction : IEndpointAction
{
    public string Id => "office.addin_disable";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var app = invocation.Argument("app")!;
        var id = invocation.Argument("addin")!;
        var addin = OfficeAddins.Read(app).FirstOrDefault(item =>
            string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        if (addin is null)
            return Task.FromResult(EndpointActionResult.Failed("That add-in is not installed. Use office.addins_list."));
        if (!addin.CanTurnOff)
            return Task.FromResult(EndpointActionResult.Blocked($"{addin.Name} is installed for every user; the helpdesk manages it."));
        if (!addin.LoadsAtStart)
            return Task.FromResult(EndpointActionResult.NotNeeded($"{addin.Name} does not load at start."));
        OfficeAddins.SetLoadBehavior(app, addin.Id, 2);
        return Task.FromResult(EndpointActionResult.Done(
            $"{addin.Name} will not load the next time {OfficeAddins.Folder(app)} opens.",
            null, new Dictionary<string, string>
            {
                ["app"] = app, ["addin"] = addin.Id, ["loadBehavior"] = (addin.LoadBehavior ?? 3).ToString()
            }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class OfficeAddinEnableAction : IEndpointAction
{
    public string Id => "office.addin_enable";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var app = invocation.Argument("app")!;
        var behavior = int.TryParse(invocation.Argument("loadBehavior"), out var value)
            && value is 3 or 9 or 16 ? value : 3;
        var addin = OfficeAddins.Read(app).FirstOrDefault(item =>
            string.Equals(item.Id, invocation.Argument("addin"), StringComparison.OrdinalIgnoreCase));
        if (addin is not { CanTurnOff: true })
            return Task.FromResult(EndpointActionResult.Failed("That add-in is not the user's any more."));
        OfficeAddins.SetLoadBehavior(app, addin.Id, behavior);
        return Task.FromResult(EndpointActionResult.Done($"{addin.Name} loads at start again."));
    }
}

[SupportedOSPlatform("windows")]
public sealed class OutlookRebuildOstAction : IEndpointAction
{
    public string Id => "outlook.rebuild_ost";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var name = invocation.Argument("file")!;
        if (!name.EndsWith(".ost", StringComparison.OrdinalIgnoreCase)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(".."))
            return Task.FromResult(EndpointActionResult.Failed("Only an Outlook .ost file name from outlook.status can be rebuilt."));
        var running = SessionProcesses.Named("OUTLOOK");
        var open = running.Length > 0;
        SessionProcesses.DisposeAll(running);
        if (open)
            return Task.FromResult(EndpointActionResult.Blocked("Outlook is open; the user must close it first."));
        var path = Path.Combine(AppFolders.Local, "Microsoft", "Outlook", name);
        if (!File.Exists(path))
            return Task.FromResult(EndpointActionResult.Failed("That file is not in the user's Outlook folder."));
        var kept = $"{path}.old-{DateTime.Now:yyyyMMddHHmm}";
        try { File.Move(path, kept); }
        catch (IOException)
        {
            return Task.FromResult(EndpointActionResult.Blocked("The file is still in use; Outlook may still be closing."));
        }
        return Task.FromResult(EndpointActionResult.Done(
            "The Outlook mailbox file was set aside. Outlook downloads the mailbox again when it opens; that can take a while.",
            new { Kept = Path.GetFileName(kept) }));
    }
}
