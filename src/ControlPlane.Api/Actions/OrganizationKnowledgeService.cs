using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ControlPlane.Api.Actions;

public sealed class OrganizationKnowledgeOptions
{
    /// <summary>
    /// The site's organization knowledge file (Markdown), filled in by an administrator from
    /// deploy\control-plane\organization\ORGANIZATION.template.md. Empty means the agent has no
    /// site knowledge and works with the environment map alone.
    /// </summary>
    public string KnowledgeFile { get; set; } = "";

    /// <summary>The review date older than this many days marks the file as stale.</summary>
    public int StaleAfterDays { get; set; } = 90;
}

public sealed record OrganizationKnowledge(bool Configured, string Text, DateOnly? LastReviewed,
    bool Stale, bool IsTemplate);

/// <summary>
/// Reads the organization knowledge file. The text is returned raw; callers must mask it
/// (<see cref="TargetMasker"/> and the tool sanitizer) before it reaches the cloud model.
/// </summary>
public sealed partial class OrganizationKnowledgeService(IOptions<OrganizationKnowledgeOptions> options)
{
    public const int MaxCharacters = 16_000;
    public const string TemplateMarker = "TEMPLATE-NOT-FILLED";

    public OrganizationKnowledge Load(DateOnly today)
    {
        var path = options.Value.KnowledgeFile;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new(false, "", null, false, false);

        var text = File.ReadAllText(path);
        if (text.Length > MaxCharacters) text = text[..MaxCharacters];
        return Parse(text, today, options.Value.StaleAfterDays);
    }

    public static OrganizationKnowledge Parse(string text, DateOnly today, int staleAfterDays = 90)
    {
        DateOnly? reviewed = null;
        var match = ReviewedPattern().Match(text);
        if (match.Success && DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            reviewed = date;
        var isTemplate = text.Contains(TemplateMarker, StringComparison.Ordinal);
        var stale = reviewed is null || today.DayNumber - reviewed.Value.DayNumber > staleAfterDays;
        return new(true, text, reviewed, stale, isTemplate);
    }

    [GeneratedRegex(@"(?im)^\s*Last reviewed:\s*(\d{4}-\d{2}-\d{2})\s*$")]
    private static partial Regex ReviewedPattern();
}
