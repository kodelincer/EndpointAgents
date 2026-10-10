using EndpointAgents.Platform.Windows.Diagnostics;

namespace EndpointAgents.Platform.Windows.SystemInfo;

/// <summary>Collects non-fatal collector failures so one bad value never hides the rest.</summary>
public sealed class SystemInfoWarnings
{
    private readonly List<DiagnosticWarning> _items = [];

    public IReadOnlyList<DiagnosticWarning> Items => _items;

    public void Add(string source, Exception ex) =>
        _items.Add(new DiagnosticWarning(source, "A non-critical collector failed.",
            ex.GetType().Name));

    /// <summary>Runs one collector and returns <c>default</c> (with a warning) when it throws.</summary>
    public T? Try<T>(string source, Func<T> collect)
    {
        try
        {
            return collect();
        }
        catch (Exception ex)
        {
            Add(source, ex);
            return default;
        }
    }
}
