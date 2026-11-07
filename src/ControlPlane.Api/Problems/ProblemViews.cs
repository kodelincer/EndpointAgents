using EndpointAgents.Shared.Problems;

namespace ControlPlane.Api.Problems;

public sealed record ProblemFilter(string? Status, string? Kind, string? Version, string? Pc, string? Os, string? Model);

public sealed record CountedValue(string Label, int Pcs);

public sealed record ProblemRow(
    string Fingerprint, string Kind, string Source, string Title, string? ExceptionType,
    string Status, string? FixedInVersion, string? FixedBy, DateTimeOffset? FixedAtUtc, DateTimeOffset? ReopenedAtUtc,
    string? ConversationId, DateTimeOffset FirstSeenUtc, DateTimeOffset LastSeenUtc, long Count, int Pcs,
    IReadOnlyList<string> Versions, IReadOnlyList<CountedValue> Windows, IReadOnlyList<CountedValue> Models,
    IReadOnlyList<string> OnlyOn);

/// <summary>Turns stored reports into what the Problems page shows. No AI: only counting.</summary>
public static class ProblemViews
{
    public static string WindowsLabel(string? name, string? version) =>
        string.Join(' ', new[] { name, version }.Where(part => !string.IsNullOrWhiteSpace(part)));

    public static string ModelLabel(string? manufacturer, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return manufacturer?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(manufacturer)
            || model.StartsWith(manufacturer.Trim(), StringComparison.OrdinalIgnoreCase)) return model.Trim();
        return $"{manufacturer.Trim()} {model.Trim()}";
    }

    public static IReadOnlyList<ProblemRow> Build(IEnumerable<StoredProblem> problems,
        IReadOnlyList<ProblemOccurrence> occurrences, IReadOnlyList<StoredProblemDevice> fleet, ProblemFilter filter)
    {
        var byProblem = occurrences.GroupBy(item => item.Fingerprint)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var rows = new List<ProblemRow>();
        foreach (var problem in problems)
        {
            if (filter.Status is { Length: > 0 } status && problem.Status != status) continue;
            if (filter.Kind is { Length: > 0 } kind && problem.Kind != kind) continue;
            var matching = (byProblem.GetValueOrDefault(problem.Fingerprint) ?? []).Where(item => Matches(item, filter)).ToArray();
            if (matching.Length == 0) continue;
            rows.Add(new ProblemRow(problem.Fingerprint, problem.Kind, problem.Source, problem.Title,
                problem.ExceptionType, problem.Status, problem.FixedInVersion, problem.FixedBy, problem.FixedAtUtc,
                problem.ReopenedAtUtc, problem.ConversationId, problem.FirstSeenUtc, problem.LastSeenUtc,
                matching.Sum(item => item.Count), PcCount(matching),
                matching.Select(item => item.AgentVersion).OfType<string>().Distinct()
                    .OrderByDescending(version => version, Comparer<string>.Create(ProblemText.CompareVersions)).ToArray(),
                Counted(matching, item => WindowsLabel(item.OsName, item.OsVersion)),
                Counted(matching, item => ModelLabel(item.Manufacturer, item.Model)),
                OnlyOn(matching, fleet)));
        }
        return rows;
    }

    /// <summary>
    /// "only Windows 10 22H2", "only Dell OptiPlex 7080": said only when the PCs with this problem
    /// all share it and other PCs in the fleet do not, otherwise it tells nothing.
    /// </summary>
    public static IReadOnlyList<string> OnlyOn(IReadOnlyList<ProblemOccurrence> occurrences,
        IReadOnlyList<StoredProblemDevice> fleet)
    {
        var notes = new List<string>();
        void Check(Func<ProblemOccurrence, string> affected, Func<ProblemDeviceFacts, string> known)
        {
            var values = occurrences.Select(affected).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (values.Length != 1 || values[0].Length == 0) return;
            var others = fleet.Select(device => known(device.Facts))
                .Where(value => value.Length > 0)
                .Any(value => !value.Equals(values[0], StringComparison.OrdinalIgnoreCase));
            if (others) notes.Add("only " + values[0]);
        }
        Check(item => WindowsLabel(item.OsName, item.OsVersion), facts => WindowsLabel(facts.OsName, facts.OsVersion));
        Check(item => ModelLabel(item.Manufacturer, item.Model), facts => ModelLabel(facts.Manufacturer, facts.Model));
        Check(item => item.AgentVersion ?? "", facts => facts.AgentVersion ?? "");
        return notes;
    }

    private static bool Matches(ProblemOccurrence item, ProblemFilter filter) =>
        (filter.Version is not { Length: > 0 } version || item.AgentVersion == version)
        && (filter.Pc is not { Length: > 0 } pc || item.MachineName.Contains(pc, StringComparison.OrdinalIgnoreCase))
        && (filter.Os is not { Length: > 0 } os || WindowsLabel(item.OsName, item.OsVersion) == os)
        && (filter.Model is not { Length: > 0 } model || ModelLabel(item.Manufacturer, item.Model) == model);

    private static int PcCount(IEnumerable<ProblemOccurrence> items) =>
        items.Select(item => item.AgentId).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    private static IReadOnlyList<CountedValue> Counted(IEnumerable<ProblemOccurrence> items,
        Func<ProblemOccurrence, string> label) =>
        items.Where(item => label(item).Length > 0).GroupBy(label)
            .Select(group => new CountedValue(group.Key, PcCount(group)))
            .OrderByDescending(item => item.Pcs).ThenBy(item => item.Label).ToArray();

    /// <summary>The text behind "Copy for developer".</summary>
    public static string DeveloperText(StoredProblem problem, IReadOnlyList<ProblemOccurrence> occurrences)
    {
        var lines = new List<string>
        {
            $"{problem.Kind} in {problem.Source}: {problem.Title}",
            $"Status: {problem.Status}" + (problem.FixedInVersion is null ? "" : $" (fixed in {problem.FixedInVersion})"),
            $"Seen {problem.TotalCount} time(s) on {PcCount(occurrences)} PC(s), first {problem.FirstSeenUtc:u}, last {problem.LastSeenUtc:u}",
            "Versions: " + string.Join(", ", occurrences.Select(item => item.AgentVersion).OfType<string>().Distinct()),
            "Windows: " + string.Join(", ", Counted(occurrences, item => WindowsLabel(item.OsName, item.OsVersion)).Select(item => $"{item.Label} ({item.Pcs})")),
            "Models: " + string.Join(", ", Counted(occurrences, item => ModelLabel(item.Manufacturer, item.Model)).Select(item => $"{item.Label} ({item.Pcs})"))
        };
        if (problem.ExceptionType is not null) lines.Add("Exception: " + problem.ExceptionType);
        if (!string.IsNullOrEmpty(problem.Stack)) { lines.Add("Stack:"); lines.Add(problem.Stack); }
        lines.Add("Fingerprint: " + problem.Fingerprint);
        return string.Join('\n', lines);
    }
}
