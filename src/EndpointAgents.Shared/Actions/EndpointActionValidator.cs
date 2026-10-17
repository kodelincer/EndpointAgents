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
            _ => false
        }
        && (target.Ports ?? []).All(port => port is > 0 and <= 65535)
        && (target.ExpectedAddresses ?? []).All(address =>
            System.Net.IPAddress.TryParse(address, out _));

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
