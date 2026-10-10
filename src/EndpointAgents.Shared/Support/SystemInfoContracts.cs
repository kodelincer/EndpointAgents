namespace EndpointAgents.Shared.Support;

/// <summary>
/// The read-only <c>system.get_info</c> endpoint command and the section names both the
/// control plane (allow-list) and the endpoint (collectors) understand. Add a name here
/// when a new read-only collector is added to the endpoint.
/// </summary>
public static class SystemInfoContracts
{
    public const string Method = "system.get_info";

    public const string Network = "network";
    public const string Windows = "windows";
    public const string Domain = "domain";
    public const string Hardware = "hardware";
    public const string Storage = "storage";

    public static IReadOnlyList<string> Sections { get; } =
        [Network, Windows, Domain, Hardware, Storage];

    /// <summary>
    /// Normalizes requested section names. Null or empty means every section; any unknown
    /// name makes the whole request invalid.
    /// </summary>
    public static bool TryNormalizeSections(IEnumerable<string?>? requested,
        out IReadOnlyList<string> sections)
    {
        var names = requested?
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
        if (names.Length == 0)
        {
            sections = Sections;
            return true;
        }
        if (names.Any(name => !Sections.Contains(name, StringComparer.Ordinal)))
        {
            sections = [];
            return false;
        }
        sections = Sections.Where(names.Contains).ToArray();
        return true;
    }
}
