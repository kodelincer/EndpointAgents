using EndpointAgents.Platform.Windows.Actions.Infrastructure;
using EndpointAgents.Shared.Actions;
using Microsoft.Win32;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Actions.SystemSide;

/// <summary>Installed applications as Apps &amp; features lists them (the Uninstall registry keys).</summary>
[SupportedOSPlatform("windows")]
internal static class InstalledApps
{
    public sealed record App(string Name, string? Version, string? Publisher, string? InstalledOn,
        string Scope, string? ProductCode);

    public static IReadOnlyList<App> Read(string? userSid, bool includeUser = true)
    {
        var apps = new List<App>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<(RegistryKey Root, string Scope)>
        {
            (RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64), "Machine"),
            (RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32), "Machine")
        };
        if (includeUser && userSid is not null && Registry.Users.OpenSubKey(userSid) is { } user)
            roots.Add((user, "User"));
        try
        {
            foreach (var (root, scope) in roots)
            {
                using var uninstall = root.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var keyName in uninstall.GetSubKeyNames())
                {
                    using var app = uninstall.OpenSubKey(keyName);
                    if (app?.GetValue("DisplayName") is not string name
                        || string.IsNullOrWhiteSpace(name)
                        || app.GetValue("SystemComponent") is 1
                        || app.GetValue("ParentKeyName") is not null
                        || app.GetValue("ReleaseType") is "Update" or "Hotfix" or "Security Update"
                        || !seen.Add(name.Trim() + "|" + scope)) continue;
                    var msi = app.GetValue("WindowsInstaller") is 1
                        && Guid.TryParse(keyName, out _) && keyName.StartsWith('{');
                    apps.Add(new App(name.Trim(), app.GetValue("DisplayVersion") as string,
                        app.GetValue("Publisher") as string, app.GetValue("InstallDate") as string,
                        scope, msi ? keyName.ToUpperInvariant() : null));
                }
            }
        }
        finally
        {
            foreach (var (root, _) in roots) root.Dispose();
        }
        return apps.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<App> Matching(ResolvedTarget target, string? userSid) =>
        Read(userSid).Where(app => EndpointActionValidator.MatchesAppPattern(target.Address, app.Name)
                && (target.Detail(EnvironmentTargetDetails.Publisher) is not { } publisher
                    || EndpointActionValidator.MatchesAppPattern(publisher, app.Publisher)))
            .ToArray();
}

[SupportedOSPlatform("windows")]
public sealed class SoftwareInventoryAction : IEndpointAction
{
    public string Id => "software.inventory";
    private const int MaxApps = 250;

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var filter = invocation.Argument("name");
        var apps = InstalledApps.Read(invocation.UserSid)
            .Where(app => filter is null || app.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var listed = apps.Take(MaxApps).Select(app => new
        {
            app.Name,
            app.Version,
            app.Publisher,
            app.InstalledOn,
            app.Scope,
            Msi = app.ProductCode is not null
        }).ToArray();
        return Task.FromResult(EndpointActionResult.Ok(
            apps.Length == 0 ? filter is null ? "No installed applications were found."
                : $"No installed application matches '{filter}'."
            : $"{apps.Length} installed application(s){(apps.Length > MaxApps ? $"; the first {MaxApps} are listed" : "")}.",
            new { Apps = listed, Count = apps.Length }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class SoftwareUninstallAction : IEndpointAction
{
    public string Id => "software.uninstall";
    private const int MaxEntries = 3;

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var target = invocation.Target();
        var matches = InstalledApps.Matching(target, invocation.UserSid);
        if (matches.Count == 0)
            return EndpointActionResult.NotNeeded($"{target.Label} is not installed on this PC.");
        if (matches.Count > MaxEntries)
            return EndpointActionResult.Blocked(
                $"The removable-software entry for {target.Label} matches {matches.Count} applications; the helpdesk must make it narrower.");
        var withoutMsi = matches.Where(app => app.ProductCode is null || app.Scope != "Machine").ToArray();
        if (withoutMsi.Length > 0)
            return EndpointActionResult.Blocked(
                target.Detail(EnvironmentTargetDetails.PackageId) is { } package
                    ? $"{target.Label} was not installed with Windows Installer; use its uninstall package '{package}'."
                    : $"{target.Label} was not installed with Windows Installer and has no uninstall package; the helpdesk must remove it.",
                new { UninstallPackageId = target.Detail(EnvironmentTargetDetails.PackageId) });

        var removed = new List<string>();
        var restart = false;
        foreach (var app in matches)
        {
            var result = await FixedTool.RunAsync("msiexec.exe",
                ["/x", app.ProductCode!, "/qn", "/norestart"],
                TimeSpan.FromSeconds(invocation.Definition.TimeoutSeconds - 30), cancellationToken);
            switch (result.TimedOut ? -1 : result.ExitCode)
            {
                case 0 or 1605:
                    removed.Add(app.Name);
                    break;
                case 3010 or 1641:
                    removed.Add(app.Name);
                    restart = true;
                    break;
                case 1618:
                    return EndpointActionResult.Blocked(
                        "Another installation is running on this PC; try again after it finishes.",
                        new { Removed = removed });
                default:
                    return EndpointActionResult.Failed(
                        $"Windows Installer could not remove {app.Name} (code {(result.TimedOut ? "timeout" : result.ExitCode.ToString())}).",
                        new { Removed = removed });
            }
        }
        return EndpointActionResult.Done(
            $"Removed {string.Join(", ", removed)}." + (restart ? " Windows needs a restart to finish." : ""),
            new { Removed = removed, RestartRequired = restart });
    }
}
