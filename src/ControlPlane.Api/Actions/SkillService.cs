using EndpointAgents.Shared.Actions;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.RegularExpressions;

namespace ControlPlane.Api.Actions;

public sealed class HelpdeskSkillOptions
{
    /// <summary>
    /// The Hermes profile's skills folder (for example
    /// C:\Users\admin\AppData\Local\hermes\profiles\endpointagents\skills). Empty means
    /// publishing only stores the skill; an administrator copies SKILL.md by hand.
    /// </summary>
    public string PublishPath { get; set; } = "";
}

public sealed record SkillLintIssue(string Severity, string Message);

public sealed record SkillPublishResult(bool Published, bool WrittenToHermes, string? Path,
    string? Error, IReadOnlyList<SkillLintIssue> Issues);

/// <summary>
/// Checks, publishes and retires the Hermes skills helpdesk members write in the dashboard.
/// A skill only tells the agent when and in which order to use the existing tools and
/// actions; it can never add a tool, an action or a permission.
/// </summary>
public sealed partial class SkillService(ActionStore store, IOptions<HelpdeskSkillOptions> options,
    ILogger<SkillService> logger)
{
    /// <summary>Folder prefix that keeps dashboard skills apart from the reviewed repo skills.</summary>
    public const string FolderPrefix = "hd-";
    public const int MaxBodyCharacters = 12_000;

    /// <summary>ControlPlane MCP tools a skill may name (Hermes calls them mcp_controlplane_&lt;name&gt;).</summary>
    public static readonly IReadOnlySet<string> KnownTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "endpoint_get_info", "endpoint_system_info", "windows_activation_status",
        "office_inventory", "office_activation_status", "office_runtime_status",
        "run_windows_activation_repair", "run_office_activation_repair",
        "find_repair_packages", "run_repair_package", "list_repair_attempts",
        "propose_endpoint_action", "mark_problem_resolved", "escalate_to_helpdesk",
        "record_feature_request", "list_endpoint_actions", "list_environment_targets",
        "run_endpoint_action", "check_known_incidents", "recent_reports_from_others",
        "probe_from_server", "check_user_account", "note_skill_used"
    };

    private static readonly HashSet<string> ActionPrefixes = EndpointActionCatalog.All
        .Select(item => item.Id[..item.Id.IndexOf('.')]).ToHashSet(StringComparer.Ordinal);

    /// <summary>Older fixed RPC methods (proposed through propose_endpoint_action).</summary>
    private static readonly HashSet<string> LegacyCommands = new Commands.CommandCatalog().GetAll()
        .Select(item => item.Method).ToHashSet(StringComparer.Ordinal);

    public bool CanWriteToHermes => !string.IsNullOrWhiteSpace(options.Value.PublishPath);

    public static bool IsValidName(string? name) =>
        name is not null && NamePattern().IsMatch(name);

    public static IReadOnlyList<SkillLintIssue> Lint(string name, string description, string body)
    {
        var issues = new List<SkillLintIssue>();
        void Error(string message) => issues.Add(new("error", message));
        void Warn(string message) => issues.Add(new("warning", message));

        if (!IsValidName(name))
            Error("Name: 3-48 characters, lowercase English letters, digits and dashes (e.g. vpn-after-password-change).");
        if (string.IsNullOrWhiteSpace(description))
            Error("Description is required: say which user problems this skill is for (Hermes picks skills by it).");
        else if (description.Length > 400)
            Error("Description is longer than 400 characters.");
        else if (description.Contains('\n'))
            Error("Description must be one line.");
        if (string.IsNullOrWhiteSpace(body))
            Error("The steps are empty.");
        else if (body.Length > MaxBodyCharacters)
            Error($"The steps are longer than {MaxBodyCharacters} characters; split the skill.");
        if (body.TrimStart().StartsWith("---", StringComparison.Ordinal))
            Error("Do not write the '---' header; the dashboard adds it.");

        foreach (Match match in ToolPattern().Matches(body))
            if (!KnownTools.Contains(match.Groups[1].Value))
                Error($"Unknown tool '{match.Value}'. Skills can only use tools listed on the capabilities page.");
        foreach (Match match in ActionPattern().Matches(body))
        {
            var id = match.Groups[1].Value;
            var definition = EndpointActionCatalog.Find(id);
            if (definition is null && ActionPrefixes.Contains(id[..id.IndexOf('.')])
                && !LegacyCommands.Contains(id))
                Error($"Unknown action '{id}'. Check the id on the capabilities page, or ask for a new action.");
            else if (definition?.Tier == ActionTier.OperatorOnly)
                Error($"'{id}' is for the helpdesk only; the agent cannot run it.");
        }
        foreach (Match match in ShellPattern().Matches(body))
            Warn($"'{match.Value.Trim()}' looks like a command. The agent cannot run commands; use catalogued actions instead.");
        foreach (Match match in PermissionPattern().Matches(body))
            Warn($"'{match.Value}' reads like a permission. Skill text never grants permissions; SOUL rules and the action tiers still apply.");
        if (!AskPattern().IsMatch(body))
            Warn("No step asks the user whether the problem is solved after a fix.");
        if (!body.Contains("escalate", StringComparison.OrdinalIgnoreCase))
            Warn("No step says when to escalate_to_helpdesk.");
        if (PrivatePattern().IsMatch(body))
            Warn("The steps contain an IP address or UNC path. Use environment-map ids instead so the cloud model never sees internal addresses.");
        return issues;
    }

    public static string Render(HelpdeskSkill skill) => Render(skill.Name, skill.Description, skill.Body);

    public static string Render(string name, string description, string body) =>
        $"---\nname: {FolderPrefix}{name}\ndescription: {description.Trim()}\n---\n\n{body.Trim()}\n";

    public async Task<SkillPublishResult> PublishAsync(string name, int? version, string reviewer,
        string? note, CancellationToken cancellationToken)
    {
        var skill = await store.GetSkillAsync(name, cancellationToken);
        if (skill is null || skill.Status == SkillStatus.Retired && version is null)
            return new(false, false, null, "Unknown skill.", []);
        if (version is not null && version != skill.Version)
        {
            // Rollback: an older version becomes the newest draft, then is published.
            var old = (await store.GetSkillVersionsAsync(name, cancellationToken))
                .FirstOrDefault(item => item.Version == version);
            if (old is null) return new(false, false, null, "Unknown version.", []);
            skill = await store.SaveSkillDraftAsync(new SkillDraftInput(name, old.Description,
                old.Body, $"Rollback to version {version}", skill.SourceConversationId), reviewer,
                cancellationToken);
        }

        var issues = Lint(skill.Name, skill.Description, skill.Body);
        if (issues.Any(issue => issue.Severity == "error"))
            return new(false, false, null, "Fix the errors first.", issues);

        string? path = null;
        if (CanWriteToHermes)
        {
            try
            {
                path = WriteSkill(skill);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Skill {Skill} could not be written to Hermes: {Message}",
                    name, exception.Message);
                return new(false, false, null,
                    "The Hermes skills folder could not be written. Check HelpdeskSkills:PublishPath.", issues);
            }
        }
        await store.SetSkillStatusAsync(name, SkillStatus.Published, reviewer, note, cancellationToken);
        return new(true, path is not null, path, null, issues);
    }

    public async Task<bool> RetireAsync(string name, string reviewer, string? note,
        CancellationToken cancellationToken)
    {
        if (await store.GetSkillAsync(name, cancellationToken) is null) return false;
        if (CanWriteToHermes)
        {
            var folder = SkillFolder(name);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        await store.SetSkillStatusAsync(name, SkillStatus.Retired, reviewer, note, cancellationToken);
        return true;
    }

    private string WriteSkill(HelpdeskSkill skill)
    {
        var folder = SkillFolder(skill.Name);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "SKILL.md");
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, Render(skill), new UTF8Encoding(false));
        File.Move(temporary, file, overwrite: true);
        return file;
    }

    private string SkillFolder(string name)
    {
        if (!IsValidName(name)) throw new ArgumentException("Invalid skill name.", nameof(name));
        var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(options.Value.PublishPath));
        var folder = Path.GetFullPath(Path.Combine(root, FolderPrefix + name));
        if (!folder.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid skill name.", nameof(name));
        return folder;
    }

    /// <summary>A starting draft from a solved or escalated conversation.</summary>
    public async Task<SkillDraftInput?> DraftFromConversationAsync(string conversationId,
        CancellationToken cancellationToken)
    {
        var messages = await store.GetConversationMessagesAsync(conversationId, 40, cancellationToken);
        if (messages.Count == 0) return null;
        var runs = await store.GetRecentRunsAsync(null, conversationId, 50, cancellationToken);
        var (reason, summary) = await store.GetEscalationAsync(conversationId, cancellationToken);
        var firstUserMessage = messages.FirstOrDefault(item => item.Role == "User").Body ?? "";

        var body = new StringBuilder();
        body.AppendLine("# <Problem name>");
        body.AppendLine();
        body.AppendLine("## When to use");
        body.AppendLine();
        body.AppendLine($"The user says something like: \"{Shorten(firstUserMessage, 200)}\"");
        body.AppendLine();
        body.AppendLine("## Steps");
        body.AppendLine();
        body.AppendLine("1. Call `check_known_incidents` first.");
        var step = 2;
        foreach (var run in runs.OrderBy(item => item.StartedAtUtc)
                     .DistinctBy(item => item.ActionId))
        {
            var definition = EndpointActionCatalog.Find(run.ActionId);
            if (definition is null || definition.Tier == ActionTier.OperatorOnly) continue;
            body.AppendLine($"{step++}. Run `{run.ActionId}` ({definition.Name}). It returned {run.Status} here: {Shorten(run.Summary, 120)}");
        }
        body.AppendLine($"{step++}. After a fix, ask the user whether the problem is solved.");
        body.AppendLine($"{step}. If it is not solved, escalate_to_helpdesk with what was checked.");
        if (reason is not null || summary is not null)
        {
            body.AppendLine();
            body.AppendLine("## What the helpdesk found last time");
            body.AppendLine();
            body.AppendLine(Shorten($"{reason} {summary}".Trim(), 600));
        }
        return new SkillDraftInput("", "", body.ToString(), null, conversationId);
    }

    public static string Template => """
        # <Problem name / نام مشکل>

        ## When to use / چه زمانی

        The user says: "..." (write two or three real phrasings, in Persian if users write Persian).
        Do not use it when: ...

        ## Steps / مراحل

        1. Call `check_known_incidents`. If an active incident matches, tell the user and stop.
        2. Look first: run `network.triage` (or another Look action) with `run_endpoint_action`.
        3. If the result shows <X>, run the safe fix `<action.id>`.
        4. Ask the user whether the problem is solved.
        5. If not, try `<next action>`; if that also fails, escalate_to_helpdesk with what was checked.

        ## Notes / نکات

        - Disruptive actions (restart Explorer, close an app, restart the PC) need the user's
          go-ahead: offer "now" or "later".
        - Use environment-map ids (from `list_environment_targets`), never IP addresses or
          server names.
        """;

    private static string Shorten(string value, int max)
    {
        var flat = Regex.Replace(value ?? "", @"\s+", " ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{2,47}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\bmcp_controlplane_([a-z0-9_]+)")]
    private static partial Regex ToolPattern();

    [GeneratedRegex(@"`([a-z_]+\.[a-z_]+)`")]
    private static partial Regex ActionPattern();

    [GeneratedRegex(@"(?im)\b(powershell(\.exe)?|cmd(\.exe)?\s*/c|reg\s+(add|delete)|Set-ItemProperty|Invoke-Expression|wmic\s|sc\s+(config|stop|start)|net\s+(use|user|stop)|bcdedit)\b")]
    private static partial Regex ShellPattern();

    [GeneratedRegex(@"(?i)\b(you (are|may be) allowed|without asking|no need to ask|skip (the )?(verification|confirmation)|ignore (the )?(soul|rules)|userConfirmed\s*=\s*true)\b")]
    private static partial Regex PermissionPattern();

    [GeneratedRegex(@"(?i)(ask (the )?user|حل شد|برطرف شد|solved)")]
    private static partial Regex AskPattern();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b|\\\\[A-Za-z0-9.\-]+\\")]
    private static partial Regex PrivatePattern();
}
