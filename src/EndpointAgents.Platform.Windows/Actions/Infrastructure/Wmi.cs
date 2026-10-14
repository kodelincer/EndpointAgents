using System.Management;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Actions.Infrastructure;

/// <summary>Small read helper over WMI queries written in code (never agent text).</summary>
[SupportedOSPlatform("windows")]
public static class Wmi
{
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string scope,
        string query, int maxRows = 200)
    {
        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope),
            new ObjectQuery(query), new System.Management.EnumerationOptions
            {
                Timeout = TimeSpan.FromSeconds(20),
                ReturnImmediately = true,
                Rewindable = false
            });
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var item in searcher.Get())
        {
            using (item)
            {
                if (rows.Count >= maxRows) continue;
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in item.Properties)
                    row[property.Name] = property.Value;
                rows.Add(row);
            }
        }
        return rows;
    }

    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> Cimv2(string query,
        int maxRows = 200) => Query(@"root\cimv2", query, maxRows);

    /// <summary>Calls a method on each instance matched by a query; returns the first ReturnValue.</summary>
    public static uint? Invoke(string scope, string query, string method,
        params object[] arguments)
    {
        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope),
            new ObjectQuery(query));
        foreach (ManagementObject item in searcher.Get())
        {
            using (item)
            {
                var result = item.InvokeMethod(method, arguments);
                return result is null ? 0u : Convert.ToUInt32(result);
            }
        }
        return null;
    }

    /// <summary>Escapes a value for a WQL string literal in single quotes.</summary>
    public static string Literal(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);

    public static string? String(IReadOnlyDictionary<string, object?> row, string name) =>
        row.TryGetValue(name, out var value) ? value?.ToString() : null;

    public static long? Long(IReadOnlyDictionary<string, object?> row, string name) =>
        row.TryGetValue(name, out var value) && value is not null
            && long.TryParse(value.ToString(), out var number) ? number : null;

    public static bool Bool(IReadOnlyDictionary<string, object?> row, string name) =>
        row.TryGetValue(name, out var value) && value is bool flag && flag;

    public static DateTimeOffset? Date(IReadOnlyDictionary<string, object?> row, string name)
    {
        if (!row.TryGetValue(name, out var value) || value is not string text
            || string.IsNullOrWhiteSpace(text)) return null;
        try { return new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(text)).ToUniversalTime(); }
        catch { return null; }
    }
}
