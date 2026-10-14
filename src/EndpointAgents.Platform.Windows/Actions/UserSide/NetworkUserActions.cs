using EndpointAgents.Platform.Windows.Actions.Infrastructure;
using EndpointAgents.Shared.Actions;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Actions.UserSide;

[SupportedOSPlatform("windows")]
public sealed class WebProbeAction : IEndpointAction
{
    public string Id => "web.probe";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var target = invocation.Target();
        var result = await WebProbe.ProbeAsync(target.Address, useDefaultCredentials: true,
            cancellationToken);
        var summary = !result.Reachable ? $"{target.Label} did not answer ({result.Error})."
            : result.CertificateValid == false ? $"{target.Label} answers but its certificate is not trusted."
            : result.AuthenticationRequired ? $"{target.Label} answers but refuses the user's Windows sign-in (HTTP {result.StatusCode})."
            : result.StatusCode >= 500 ? $"{target.Label} answers with a server error (HTTP {result.StatusCode})."
            : $"{target.Label} answers normally (HTTP {result.StatusCode}).";
        return EndpointActionResult.Ok(summary, new { Target = target.Id, Result = result });
    }
}

/// <summary>The user's WinINET proxy settings (Internet Options).</summary>
[SupportedOSPlatform("windows")]
internal static class ProxySettings
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    public sealed record Values(bool Enabled, string? Server, string? AutoConfigUrl,
        string? Bypass);

    public static Values Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return new Values(key?.GetValue("ProxyEnable") is 1,
            key?.GetValue("ProxyServer") as string,
            key?.GetValue("AutoConfigURL") as string,
            key?.GetValue("ProxyOverride") as string);
    }

    public static void Write(Values values)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key.SetValue("ProxyEnable", values.Enabled ? 1 : 0, RegistryValueKind.DWord);
        SetOrDelete(key, "ProxyServer", values.Server);
        SetOrDelete(key, "AutoConfigURL", values.AutoConfigUrl);
        SetOrDelete(key, "ProxyOverride", values.Bypass);
        ActionNative.InternetSetOption(IntPtr.Zero, ActionNative.INTERNET_OPTION_SETTINGS_CHANGED,
            IntPtr.Zero, 0);
        ActionNative.InternetSetOption(IntPtr.Zero, ActionNative.INTERNET_OPTION_REFRESH,
            IntPtr.Zero, 0);
    }

    private static void SetOrDelete(RegistryKey key, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, value, RegistryValueKind.String);
    }

    /// <summary>The organisation standard from the environment map, or null when none is set.</summary>
    public static Values? Standard(ActionInvocation invocation)
    {
        var mode = invocation.Setting(ActionSettings.ProxyMode)?.ToLowerInvariant();
        var bypass = invocation.Setting(ActionSettings.ProxyBypass);
        return mode switch
        {
            "none" => new Values(false, null, null, null),
            "pac" when EndpointActionValidator.IsValidWebAddress(
                invocation.Setting(ActionSettings.ProxyPacUrl)) =>
                new Values(false, null, invocation.Setting(ActionSettings.ProxyPacUrl), null),
            "server" when invocation.Setting(ActionSettings.ProxyServer) is { } server
                && IsHostPort(server) => new Values(true, server, null, bypass),
            _ => null
        };
    }

    public static bool Matches(Values current, Values standard) =>
        current.Enabled == standard.Enabled
        && Same(current.AutoConfigUrl, standard.AutoConfigUrl)
        && (!standard.Enabled || Same(current.Server, standard.Server));

    private static bool Same(string? left, string? right) =>
        string.Equals(left?.Trim() ?? "", right?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);

    internal static bool IsHostPort(string value)
    {
        var parts = value.Split(':');
        return parts.Length == 2 && EndpointActionValidator.IsValidHost(parts[0])
            && int.TryParse(parts[1], out var port) && port is > 0 and <= 65535;
    }
}

[SupportedOSPlatform("windows")]
public sealed class ProxyGetAction : IEndpointAction
{
    public string Id => "proxy.get";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var current = ProxySettings.Read();
        var standard = ProxySettings.Standard(invocation);
        bool? matches = standard is null ? null : ProxySettings.Matches(current, standard);
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings\Connections");
        var autoDetect = key?.GetValue("DefaultConnectionSettings") is byte[] { Length: > 8 } bytes
            && (bytes[8] & 0x08) != 0;
        return Task.FromResult(EndpointActionResult.Ok(matches switch
            {
                true => "The proxy settings match the organisation standard.",
                false => "The proxy settings differ from the organisation standard.",
                null => "Proxy settings read (no standard is set)."
            },
            new
            {
                current.Enabled, current.Server, current.AutoConfigUrl, AutoDetect = autoDetect,
                current.Bypass, MatchesStandard = matches
            }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class ProxyResetAction : IEndpointAction
{
    public string Id => "proxy.reset_to_standard";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var standard = ProxySettings.Standard(invocation);
        if (standard is null)
            return Task.FromResult(EndpointActionResult.Blocked(
                "No proxy standard is set in the environment map."));
        var current = ProxySettings.Read();
        if (ProxySettings.Matches(current, standard))
            return Task.FromResult(EndpointActionResult.NotNeeded(
                "The proxy settings already match the standard."));
        ProxySettings.Write(standard);
        var undo = new Dictionary<string, string>
        {
            ["enabled"] = current.Enabled ? "1" : "0",
            ["server"] = current.Server ?? "",
            ["autoConfigUrl"] = current.AutoConfigUrl ?? "",
            ["bypass"] = current.Bypass ?? ""
        };
        return Task.FromResult(EndpointActionResult.Done(
            "The proxy settings were reset to the organisation standard.", null, undo));
    }
}

[SupportedOSPlatform("windows")]
public sealed class ProxyRestoreAction : IEndpointAction
{
    public string Id => "proxy.restore";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var server = invocation.Argument("server");
        var pac = invocation.Argument("autoConfigUrl");
        if (server is not null && !ProxySettings.IsHostPort(server)
            || pac is not null && !EndpointActionValidator.IsValidWebAddress(pac))
            return Task.FromResult(EndpointActionResult.Blocked("The saved proxy values are not valid."));
        ProxySettings.Write(new ProxySettings.Values(invocation.Argument("enabled") == "1",
            server, pac, invocation.Argument("bypass")));
        return Task.FromResult(EndpointActionResult.Done("The previous proxy settings were restored."));
    }
}

[SupportedOSPlatform("windows")]
public sealed class UserPolicyRefreshAction : IEndpointAction
{
    public string Id => "gpo.refresh_user";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var result = await FixedTool.RunAsync("gpupdate.exe",
            ["/target:user", "/force", "/wait:120"], TimeSpan.FromSeconds(140),
            cancellationToken, standardInput: "N\r\nN\r\n");
        return result.Succeeded
            ? EndpointActionResult.Done("User policy was applied.")
            : EndpointActionResult.Failed(result.TimedOut
                ? "Applying user policy took too long." : "Applying user policy reported errors.");
    }
}

[SupportedOSPlatform("windows")]
public sealed class KerberosPurgeAction : IEndpointAction
{
    public string Id => "kerberos.purge";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var result = await FixedTool.RunAsync("klist.exe", ["purge"], TimeSpan.FromSeconds(15),
            cancellationToken);
        return result.Succeeded
            ? EndpointActionResult.Done("Sign-in tickets were cleared; Windows will get new ones.")
            : EndpointActionResult.Failed("Clearing sign-in tickets failed.");
    }
}

[SupportedOSPlatform("windows")]
internal static class SavedCredentials
{
    public sealed record Entry(string Ref, string Target, uint Type, DateTimeOffset SavedUtc);

    public static IReadOnlyList<Entry> Read()
    {
        if (!ActionNative.CredEnumerate(null, 0, out var count, out var buffer))
            return [];
        try
        {
            var entries = new List<Entry>();
            for (var index = 0; index < count; index++)
            {
                var pointer = Marshal.ReadIntPtr(buffer, index * IntPtr.Size);
                var credential = Marshal.PtrToStructure<ActionNative.CREDENTIAL>(pointer);
                if (credential.Type != ActionNative.CRED_TYPE_DOMAIN_PASSWORD
                    && credential.Type != ActionNative.CRED_TYPE_GENERIC) continue;
                var target = Marshal.PtrToStringUni(credential.TargetName) ?? string.Empty;
                entries.Add(new Entry(RefOf(target, credential.Type), target, credential.Type,
                    DateTimeOffset.FromFileTime(credential.LastWritten).ToUniversalTime()));
            }
            return entries;
        }
        finally
        {
            ActionNative.CredFree(buffer);
        }
    }

    /// <summary>A short stable reference so the agent never needs the real target name.</summary>
    public static string RefOf(string target, uint type) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{type}:{target.ToLowerInvariant()}")))[..12]
            .ToLowerInvariant();

    /// <summary>Host part of a credential target such as "Domain:target=fs01.corp.local".</summary>
    public static string HostOf(string target)
    {
        var value = target.Contains("target=", StringComparison.OrdinalIgnoreCase)
            ? target[(target.IndexOf("target=", StringComparison.OrdinalIgnoreCase) + 7)..]
            : target;
        return value.Split('\\', '/', ':')[0].Trim().ToLowerInvariant();
    }
}

[SupportedOSPlatform("windows")]
public sealed class CredentialListAction : IEndpointAction
{
    public string Id => "credman.list";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var entries = SavedCredentials.Read();
        var windows = entries.Where(item => item.Type == ActionNative.CRED_TYPE_DOMAIN_PASSWORD)
            .Select(item => new
            {
                item.Ref,
                Kind = "WindowsServer",
                Server = SavedCredentials.HostOf(item.Target),
                item.SavedUtc
            })
            .OrderByDescending(item => item.SavedUtc).Take(30).ToArray();
        return Task.FromResult(EndpointActionResult.Ok(windows.Length == 0
                ? "No saved Windows passwords for servers."
                : $"{windows.Length} saved Windows password(s) for servers.",
            new { Credentials = windows, OtherSavedCount = entries.Count - windows.Length }));
    }
}

[SupportedOSPlatform("windows")]
public sealed class CredentialRemoveAction : IEndpointAction
{
    public string Id => "credman.remove";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var reference = invocation.Argument("ref")!;
        var entry = SavedCredentials.Read().FirstOrDefault(item => item.Ref == reference
            && item.Type == ActionNative.CRED_TYPE_DOMAIN_PASSWORD);
        if (entry is null)
            return Task.FromResult(EndpointActionResult.NotNeeded("That saved password is already gone."));
        return Task.FromResult(ActionNative.CredDelete(entry.Target, entry.Type, 0)
            ? EndpointActionResult.Done("The saved password was removed; Windows will ask for the current one.")
            : EndpointActionResult.Failed("Removing the saved password failed."));
    }
}
