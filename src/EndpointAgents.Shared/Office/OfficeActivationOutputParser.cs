using System.Text.RegularExpressions;

namespace EndpointAgents.Shared.Office;

/// <summary>Parses supported Office diagnostic output while discarding identity and key fields.</summary>
public static partial class OfficeActivationOutputParser
{
    public static IReadOnlyList<OfficeActivationProduct> ParseVNext(string output)
    {
        var products = new List<OfficeActivationProduct>();
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in SplitLines(output))
        {
            var match = KeyValueLine().Match(line);
            if (!match.Success)
                continue;

            var key = NormalizeKey(match.Groups[1].Value);
            var value = match.Groups[2].Value.Trim();
            if (key == "licenseid" && HasProductData(current))
            {
                AddVNextProduct(current, products);
                current.Clear();
            }

            current[key] = value;
        }

        AddVNextProduct(current, products);
        return products;
    }

    public static IReadOnlyList<OfficeActivationProduct> ParseOspp(string output)
    {
        var products = new List<OfficeActivationProduct>();
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in SplitLines(output))
        {
            var match = KeyValueLine().Match(line.Trim().Trim('-'));
            if (!match.Success)
                continue;

            var key = NormalizeKey(match.Groups[1].Value);
            var value = match.Groups[2].Value.Trim().Trim('-').Trim();
            if (key == "licensename" && current.ContainsKey("licensename"))
            {
                AddOsppProduct(current, products);
                current.Clear();
            }

            current[key] = value;
        }

        AddOsppProduct(current, products);
        return products;
    }

    public static string NormalizeState(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return OfficeActivationStates.Unknown;

        if (value.Contains("UNLICENSED", StringComparison.OrdinalIgnoreCase))
            return OfficeActivationStates.Unlicensed;
        if (value.Contains("NOTIFICATION", StringComparison.OrdinalIgnoreCase))
            return OfficeActivationStates.Notification;
        if (value.Contains("GRACE", StringComparison.OrdinalIgnoreCase))
            return OfficeActivationStates.Grace;
        if (value.Contains("LICENSED", StringComparison.OrdinalIgnoreCase))
            return OfficeActivationStates.Licensed;
        if (value.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
            return OfficeActivationStates.Error;
        return OfficeActivationStates.Unknown;
    }

    private static void AddVNextProduct(Dictionary<string, string> values,
        List<OfficeActivationProduct> products)
    {
        var name = First(values, "product", "productname", "name");
        var state = First(values, "state", "licensestate", "licensestatus", "status");
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(state))
            return;

        products.Add(new OfficeActivationProduct(
            string.IsNullOrWhiteSpace(name) ? "Microsoft 365 Apps" : name,
            First(values, "type", "licensetype"),
            NormalizeState(state),
            ExtractErrorCode(First(values, "errorcode", "error")),
            OfficeActivationMechanisms.VNext));
    }

    private static void AddOsppProduct(Dictionary<string, string> values,
        List<OfficeActivationProduct> products)
    {
        var name = First(values, "licensename");
        var description = First(values, "licensedescription");
        var state = First(values, "licensestatus");
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(state))
            return;
        var normalizedState = NormalizeState(state);

        products.Add(new OfficeActivationProduct(
            string.IsNullOrWhiteSpace(name) ? "Volume Office" : name,
            NormalizeLicenseType(description),
            normalizedState,
            normalizedState == OfficeActivationStates.Licensed
                ? null
                : ExtractErrorCode(First(values, "errorcode")),
            OfficeActivationMechanisms.Ospp));
    }

    private static string? NormalizeLicenseType(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        if (description.Contains("KMSCLIENT", StringComparison.OrdinalIgnoreCase))
            return OfficeLicenseTypes.VolumeKms;
        if (description.Contains("MAK", StringComparison.OrdinalIgnoreCase))
            return OfficeLicenseTypes.VolumeMak;
        if (description.Contains("RETAIL", StringComparison.OrdinalIgnoreCase))
            return OfficeLicenseTypes.Retail;
        if (description.Contains("VOLUME", StringComparison.OrdinalIgnoreCase))
            return OfficeLicenseTypes.Volume;
        return OfficeLicenseTypes.Other;
    }

    private static string? ExtractErrorCode(string? value) =>
        value is null ? null : ErrorCode().Match(value) is { Success: true } match
            ? match.Value.ToUpperInvariant()
            : null;

    private static bool HasProductData(Dictionary<string, string> values) =>
        First(values, "product", "productname", "name", "state", "licensestate") is not null;

    private static string? First(Dictionary<string, string> values, params string[] keys) =>
        keys.Select(key => values.TryGetValue(key, out var value) ? value : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string NormalizeKey(string key) =>
        new(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static IEnumerable<string> SplitLines(string value) =>
        value.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);

    [GeneratedRegex(@"^\s*([^:=]+?)\s*[:=]\s*(.*?)\s*$")]
    private static partial Regex KeyValueLine();

    [GeneratedRegex(@"0x[0-9a-fA-F]{8}")]
    private static partial Regex ErrorCode();
}
