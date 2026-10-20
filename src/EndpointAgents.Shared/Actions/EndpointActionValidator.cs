using System.Text.RegularExpressions;

namespace EndpointAgents.Shared.Actions;

/// <summary>
/// Checks action arguments against the catalogue. Both the control plane (before sending)
/// and the endpoint (before running) call it, so nothing outside the catalogue's types and
/// allowed values ever reaches an action.
/// </summary>
public static partial class EndpointActionValidator
{
    public const int MaxLocalItemLength = 200;

    public static bool TryValidate(EndpointActionDefinition definition,
        IReadOnlyDictionary<string, string?>? arguments,
        out Dictionary<string, string> normalized, out string error)
    {
        normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        error = string.Empty;
        arguments ??= new Dictionary<string, string?>();

        var unknown = arguments.Keys.FirstOrDefault(key =>
            definition.Parameters.All(parameter => parameter.Name != key));
        if (unknown is not null)
        {
            error = $"Unknown argument '{unknown}'.";
            return false;
        }

        foreach (var parameter in definition.Parameters)
        {
            arguments.TryGetValue(parameter.Name, out var raw);
            var value = raw?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                if (parameter.Required)
                {
                    error = $"Argument '{parameter.Name}' is required.";
                    return false;
                }
                continue;
            }

            if (!TryValidateValue(parameter, value, out var clean))
            {
                error = $"Argument '{parameter.Name}' is not an allowed value.";
                return false;
            }
            normalized[parameter.Name] = clean;
        }
        return true;
    }

    private static bool TryValidateValue(ActionParameter parameter, string value,
        out string clean)
    {
        clean = value;
        switch (parameter.Kind)
        {
            case ActionParameterKind.Choice:
                var choice = parameter.Choices?.FirstOrDefault(item =>
                    item.Value.Equals(value, StringComparison.OrdinalIgnoreCase));
                if (choice is null) return false;
                clean = choice.Value;
                return true;
            case ActionParameterKind.Target:
                return TargetIdPattern().IsMatch(value);
            case ActionParameterKind.Integer:
                if (!int.TryParse(value, out var number)
                    || number < parameter.Min || number > parameter.Max) return false;
                clean = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return true;
            case ActionParameterKind.LocalItem:
                return value.Length <= MaxLocalItemLength && !value.Any(char.IsControl);
            case ActionParameterKind.SearchText:
                return SearchTextPattern().IsMatch(value);
            default:
                return false;
        }
    }

    /// <summary>Environment-map ids: lowercase letters, digits and dashes.</summary>
    public static bool IsValidTargetId(string? value) =>
        value is not null && TargetIdPattern().IsMatch(value);

    /// <summary>A DNS host name or IPv4 address, nothing else (no ports, paths or spaces).</summary>
    public static bool IsValidHost(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 253
        && (HostPattern().IsMatch(value)
            || System.Net.IPAddress.TryParse(value, out _) && !value.Contains(':'));

    /// <summary>An http(s) URL with a valid host.</summary>
    public static bool IsValidWebAddress(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && string.IsNullOrEmpty(uri.UserInfo) && IsValidHost(uri.Host);

    /// <summary>\\server\share or \\server\printer, optionally with sub-folders.</summary>
    public static bool IsValidUncPath(string? value) =>
        value is not null && value.Length <= 260 && UncPattern().IsMatch(value)
        && IsValidHost(value[2..].Split('\\')[0]);

    public static bool IsValidDriveLetter(string? value) =>
        value is not null && DriveLetterPattern().IsMatch(value);

    /// <summary>The host part of any target address.</summary>
    public static string? HostOf(ResolvedTarget target) => target.Kind switch
    {
        EnvironmentTargetKinds.WebApp => Uri.TryCreate(target.Address, UriKind.Absolute,
            out var uri) ? uri.Host : null,
        EnvironmentTargetKinds.Printer or EnvironmentTargetKinds.Share =>
            target.Address.StartsWith(@"\\", StringComparison.Ordinal)
                ? target.Address[2..].Split('\\')[0] : null,
        _ => target.Address
    };

    /// <summary>Checks a resolved target's address matches its kind.</summary>
    public static bool IsValidTarget(ResolvedTarget? target) => target is not null
        && IsValidTargetId(target.Id)
        && target.Kind switch
        {
            EnvironmentTargetKinds.Host => IsValidHost(target.Address),
            EnvironmentTargetKinds.WebApp => IsValidWebAddress(target.Address),
            EnvironmentTargetKinds.Printer => IsValidUncPath(target.Address),
            EnvironmentTargetKinds.Share => IsValidUncPath(target.Address)
                && (target.DriveLetter is null || IsValidDriveLetter(target.DriveLetter)),
            EnvironmentTargetKinds.App => IsValidAppPattern(target.Address)
                && (target.Detail(EnvironmentTargetDetails.Publisher) is not { } publisher
                    || IsValidAppPattern(publisher)),
            EnvironmentTargetKinds.Device => SplitHardwareIds(target.Address) is { Count: > 0 },
            EnvironmentTargetKinds.Vpn => IsValidHost(target.Address) && IsValidVpnDetails(target),
            _ => false
        }
        && (target.Detail(EnvironmentTargetDetails.PackageId) is not { } package
            || PackageIdPattern().IsMatch(package))
        && (target.Ports ?? []).All(port => port is > 0 and <= 65535)
        && (target.ExpectedAddresses ?? []).All(address =>
            System.Net.IPAddress.TryParse(address, out _));

    /// <summary>An Apps &amp; features name or publisher, optionally with * wildcards.</summary>
    public static bool IsValidAppPattern(string? value) =>
        value is not null && AppPatternRegex().IsMatch(value) && value.Trim('*', ' ').Length >= 2;

    /// <summary>True when <paramref name="value"/> matches a pattern with * wildcards, ignoring case.</summary>
    public static bool MatchesAppPattern(string pattern, string? value) =>
        value is not null && Regex.IsMatch(value.Trim(),
            "^" + Regex.Escape(pattern.Trim()).Replace(@"\*", ".*", StringComparison.Ordinal) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Hardware IDs such as <c>USB\VID_04F9&amp;PID_0042</c>, separated by semicolons.</summary>
    public static IReadOnlyList<string> SplitHardwareIds(string? value)
    {
        var ids = (value ?? string.Empty).Split(';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return ids.Length is > 0 and <= 20 && ids.All(id => HardwareIdPattern().IsMatch(id))
            ? ids : [];
    }

    /// <summary>A Windows service name (letters, digits, dot, dash, underscore).</summary>
    public static bool IsValidServiceName(string? value) =>
        value is not null && ServiceNamePattern().IsMatch(value);

    private static bool IsValidVpnDetails(ResolvedTarget target) =>
        target.Detail(EnvironmentTargetDetails.VpnType) switch
        {
            EnvironmentTargetDetails.VpnBuiltin =>
                target.Detail(EnvironmentTargetDetails.Connection) is { } name
                && ConnectionNamePattern().IsMatch(name),
            EnvironmentTargetDetails.VpnClient =>
                IsValidServiceName(target.Detail(EnvironmentTargetDetails.Service)),
            _ => false
        };

    [GeneratedRegex(@"^[\p{L}\p{N} .+\-_()&,'*]{2,100}$")]
    private static partial Regex AppPatternRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\\&_.\-]{2,199}$")]
    private static partial Regex HardwareIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,79}$")]
    private static partial Regex ServiceNamePattern();

    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N} .\-_()]{0,63}$")]
    private static partial Regex ConnectionNamePattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,99}$")]
    private static partial Regex PackageIdPattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,47}$")]
    private static partial Regex TargetIdPattern();

    [GeneratedRegex(@"^[\p{L}\p{N} .+\-_]{2,60}$")]
    private static partial Regex SearchTextPattern();

    [GeneratedRegex(@"^(?=.{1,253}$)([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$")]
    private static partial Regex HostPattern();

    [GeneratedRegex(@"^\\\\[A-Za-z0-9.\-]+(\\[^\\/:*?""<>|\r\n]+)+$")]
    private static partial Regex UncPattern();

    [GeneratedRegex(@"^[D-Zd-z]:$")]
    private static partial Regex DriveLetterPattern();
}
