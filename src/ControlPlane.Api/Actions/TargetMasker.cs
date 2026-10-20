using EndpointAgents.Shared.Actions;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ControlPlane.Api.Actions;

/// <summary>
/// Hides internal names before an action result reaches the cloud model: environment-map
/// addresses become their labels ("[HR portal]"), other names under the organisation's
/// internal DNS suffixes and UNC paths become placeholders. IP addresses are removed later by
/// <see cref="Tools.ToolResultSanitizer"/>.
/// </summary>
public sealed partial class TargetMasker
{
    private readonly List<(string Value, string Replacement)> _known = [];
    private readonly Regex? _internalNames;

    public TargetMasker(IEnumerable<EnvironmentTarget> targets,
        IReadOnlyDictionary<string, string> settings)
    {
        // Software and device entries hold name patterns and hardware IDs, not internal names.
        foreach (var target in targets.Where(target =>
                     target.Kind is not (EnvironmentTargetKinds.App or EnvironmentTargetKinds.Device)))
        {
            var label = $"[{target.Label}]";
            _known.Add((target.Address, label));
            if (EndpointActionValidator.HostOf(target.ToResolved()) is { } host)
                _known.Add((host, label));
            foreach (var address in target.ExpectedAddresses)
                _known.Add((address, $"[{target.Label} address]"));
        }
        _known.Sort((left, right) => right.Value.Length.CompareTo(left.Value.Length));

        var suffixes = (settings.GetValueOrDefault(ActionSettings.InternalDnsSuffixes) ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.TrimStart('.'))
            .Where(item => EndpointActionValidator.IsValidHost(item))
            .ToArray();
        if (suffixes.Length > 0)
            _internalNames = new Regex(
                @"(?i)\b(?:[a-z0-9-]+\.)*(?:" + string.Join('|', suffixes.Select(Regex.Escape)) + @")\b",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    public string Mask(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        foreach (var (known, replacement) in _known)
            value = value.Replace(known, replacement, StringComparison.OrdinalIgnoreCase);
        value = UncPattern().Replace(value, "[internal-share]");
        if (_internalNames is not null)
            value = _internalNames.Replace(value, "[internal-host]");
        return value;
    }

    public JsonNode? Mask(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject item:
                foreach (var property in item.ToArray())
                    item[property.Key] = Mask(property.Value?.DeepClone());
                return item;
            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                    array[index] = Mask(array[index]?.DeepClone());
                return array;
            case JsonValue value when value.TryGetValue<string>(out var text):
                return JsonValue.Create(Mask(text));
            default:
                return node;
        }
    }

    /// <summary>The environment-map target whose address matches a value the PC reported.</summary>
    public static EnvironmentTarget? Match(IEnumerable<EnvironmentTarget> targets, string? value) =>
        string.IsNullOrWhiteSpace(value) ? null
            : targets.FirstOrDefault(target =>
                target.Address.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"\\\\[A-Za-z0-9.\-]+(\\[^\s""',;\]]+)*")]
    private static partial Regex UncPattern();
}
