using ControlPlane.Api.Chat;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json;

namespace ControlPlane.Api.Health;

public static class HealthFrequencies
{
    public const string Daily = "daily";
    public const string Weekly = "weekly";
    public static IReadOnlyList<string> All { get; } = [Daily, Weekly];
}

/// <summary>What happens when the check fails, after the first report-only week.</summary>
public static class HealthOnFailure
{
    public const string Report = "report";
    public const string Fix = "fix";
    public const string TellUser = "tell-user";
    public static IReadOnlyList<string> All { get; } = [Report, Fix, TellUser];
}

/// <summary>The five answers of one rule, as an admin picks them on the Health rules page.</summary>
public sealed record HealthRuleInput(string Name, string CheckId, string? Option, string? FixActionId,
    string Frequency, IReadOnlyList<string>? Machines, string OnFailure);

public sealed record HealthRule(string RuleId, string Name, string CheckId, string? Option,
    string? FixActionId, string Frequency, IReadOnlyList<string> Machines, string OnFailure,
    bool Enabled, string CreatedBy, DateTimeOffset CreatedAtUtc, DateTimeOffset ReportOnlyUntilUtc)
{
    public bool ReportOnly(DateTimeOffset now) => now < ReportOnlyUntilUtc;
    public TimeSpan Period => Frequency == HealthFrequencies.Weekly ? TimeSpan.FromDays(7) : TimeSpan.FromDays(1);
}

/// <summary>Outcomes recorded for one PC.</summary>
public static class HealthOutcomes
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    /// <summary>Failed, the fix ran and the check passed afterwards.</summary>
    public const string Fixed = "fixed";
    /// <summary>The PC could not be checked (offline Tray, old agent, action switched off).</summary>
    public const string Error = "error";
}

public sealed record HealthResult(string RuleId, string AgentId, string MachineName,
    DateTimeOffset CheckedAtUtc, string Outcome, string Finding, string? ActionTaken,
    bool WouldAct);

public sealed record HealthRuleSummary(HealthRule Rule, int Passed, int Fixed, int Failed,
    int Errors, int WouldAct, DateTimeOffset? LastCheckedUtc);

/// <summary>Health rules and the latest result per rule and PC, in the control-plane database.</summary>
public sealed class HealthRuleStore(IOptions<ChatOptions> chatOptions, TimeProvider clock)
{
    public static readonly TimeSpan ReportOnlyPeriod = TimeSpan.FromDays(7);
    public const int MaxRules = 50;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _databasePath = Path.GetFullPath(chatOptions.Value.DatabasePath);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await using var connection = await OpenAsync(cancellationToken);
        await using var create = connection.CreateCommand();
        create.CommandText =
            """
            CREATE TABLE IF NOT EXISTS health_rules (
                rule_id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                check_id TEXT NOT NULL,
                option_value TEXT NULL,
                fix_action_id TEXT NULL,
                frequency TEXT NOT NULL,
                machines_json TEXT NOT NULL,
                on_failure TEXT NOT NULL,
                enabled INTEGER NOT NULL,
                created_by TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                report_only_until_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS health_results (
                rule_id TEXT NOT NULL,
                agent_id TEXT NOT NULL,
                machine_name TEXT NOT NULL,
                checked_at_utc TEXT NOT NULL,
                outcome TEXT NOT NULL,
                finding TEXT NOT NULL,
                action_taken TEXT NULL,
                would_act INTEGER NOT NULL,
                PRIMARY KEY (rule_id, agent_id)
            );
            """;
        await create.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Checks the five answers against the fixed lists; returns the problem or null.</summary>
    public static string? Validate(HealthRuleInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 80) return "Give the rule a name (at most 80 characters).";
        var check = HealthCheckCatalog.Find(input.CheckId);
        if (check is null) return "Pick a check from the list.";
        if (check.Options.Count > 0 && !check.Options.Any(option => option.Value == input.Option))
            return $"Pick one of: {string.Join(", ", check.Options.Select(option => option.Label))}.";
        if (check.Options.Count == 0 && input.Option is { Length: > 0 }) return "This check has no setting.";
        if (input.FixActionId is { Length: > 0 } fix && !check.Fixes.Contains(fix))
            return "That fix does not belong to this check.";
        if (!HealthFrequencies.All.Contains(input.Frequency)) return "Run it daily or weekly.";
        if (!HealthOnFailure.All.Contains(input.OnFailure)) return "Pick what happens when it fails.";
        if (input.OnFailure == HealthOnFailure.Fix && string.IsNullOrEmpty(input.FixActionId))
            return "Pick a fix, or choose 'only report'.";
        if (input.OnFailure == HealthOnFailure.TellUser && check.NoticeId is null)
            return "This check has no message for the user.";
        if (input.Machines is { Count: > 200 }) return "List at most 200 PCs.";
        if (input.Machines?.Any(name => !global::System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9._-]{1,63}$")) == true)
            return "PC names may only have letters, digits, dot, dash and underscore.";
        return null;
    }

    public async Task<HealthRule> CreateAsync(HealthRuleInput input, string createdBy, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var rule = new HealthRule(Guid.NewGuid().ToString("N"), input.Name.Trim(), input.CheckId,
            string.IsNullOrEmpty(input.Option) ? null : input.Option,
            string.IsNullOrEmpty(input.FixActionId) ? null : input.FixActionId, input.Frequency,
            [.. (input.Machines ?? []).Select(name => name.Trim().ToUpperInvariant()).Distinct()],
            input.OnFailure, true, createdBy, now, now + ReportOnlyPeriod);
        await using var connection = await OpenAsync(cancellationToken);
        await using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO health_rules VALUES ($id, $name, $check, $option, $fix, $frequency,
                $machines, $onFailure, 1, $by, $created, $until);
            """;
        insert.Parameters.AddWithValue("$id", rule.RuleId);
        insert.Parameters.AddWithValue("$name", rule.Name);
        insert.Parameters.AddWithValue("$check", rule.CheckId);
        insert.Parameters.AddWithValue("$option", (object?)rule.Option ?? DBNull.Value);
        insert.Parameters.AddWithValue("$fix", (object?)rule.FixActionId ?? DBNull.Value);
        insert.Parameters.AddWithValue("$frequency", rule.Frequency);
        insert.Parameters.AddWithValue("$machines", JsonSerializer.Serialize(rule.Machines, Json));
        insert.Parameters.AddWithValue("$onFailure", rule.OnFailure);
        insert.Parameters.AddWithValue("$by", createdBy);
        insert.Parameters.AddWithValue("$created", Format(rule.CreatedAtUtc));
        insert.Parameters.AddWithValue("$until", Format(rule.ReportOnlyUntilUtc));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return rule;
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken) => (await ListAsync(cancellationToken)).Count;

    public async Task<IReadOnlyList<HealthRule>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT * FROM health_rules ORDER BY created_at_utc;";
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        var rules = new List<HealthRule>();
        while (await reader.ReadAsync(cancellationToken))
            rules.Add(new HealthRule(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5), JsonSerializer.Deserialize<string[]>(reader.GetString(6), Json) ?? [],
                reader.GetString(7), reader.GetInt64(8) == 1, reader.GetString(9), Parse(reader.GetString(10)),
                Parse(reader.GetString(11))));
        return rules;
    }

    public async Task<bool> SetEnabledAsync(string ruleId, bool enabled, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE health_rules SET enabled = $enabled WHERE rule_id = $id;";
        update.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        update.Parameters.AddWithValue("$id", ruleId);
        return await update.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<bool> DeleteAsync(string ruleId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM health_results WHERE rule_id = $id; DELETE FROM health_rules WHERE rule_id = $id;";
        delete.Parameters.AddWithValue("$id", ruleId);
        return await delete.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task SaveResultAsync(HealthResult result, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var upsert = connection.CreateCommand();
        upsert.CommandText =
            """
            INSERT INTO health_results VALUES ($rule, $agent, $machine, $at, $outcome, $finding, $action, $would)
            ON CONFLICT (rule_id, agent_id) DO UPDATE SET machine_name = $machine, checked_at_utc = $at,
                outcome = $outcome, finding = $finding, action_taken = $action, would_act = $would;
            """;
        upsert.Parameters.AddWithValue("$rule", result.RuleId);
        upsert.Parameters.AddWithValue("$agent", result.AgentId);
        upsert.Parameters.AddWithValue("$machine", result.MachineName);
        upsert.Parameters.AddWithValue("$at", Format(result.CheckedAtUtc));
        upsert.Parameters.AddWithValue("$outcome", result.Outcome);
        upsert.Parameters.AddWithValue("$finding", result.Finding.Length > 200 ? result.Finding[..200] : result.Finding);
        upsert.Parameters.AddWithValue("$action", (object?)result.ActionTaken ?? DBNull.Value);
        upsert.Parameters.AddWithValue("$would", result.WouldAct ? 1 : 0);
        await upsert.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<HealthResult>> ResultsAsync(string? ruleId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var select = connection.CreateCommand();
        select.CommandText = ruleId is null
            ? "SELECT * FROM health_results;"
            : "SELECT * FROM health_results WHERE rule_id = $id ORDER BY outcome, machine_name;";
        if (ruleId is not null) select.Parameters.AddWithValue("$id", ruleId);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        var results = new List<HealthResult>();
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new HealthResult(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                Parse(reader.GetString(3)), reader.GetString(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetInt64(7) == 1));
        return results;
    }

    /// <summary>The Compliance view: how many PCs pass, were fixed, still fail or could not be checked.</summary>
    public async Task<IReadOnlyList<HealthRuleSummary>> SummariesAsync(CancellationToken cancellationToken)
    {
        var results = (await ResultsAsync(null, cancellationToken)).ToLookup(result => result.RuleId);
        return [.. (await ListAsync(cancellationToken)).Select(rule =>
        {
            var mine = results[rule.RuleId].ToArray();
            return new HealthRuleSummary(rule,
                mine.Count(result => result.Outcome == HealthOutcomes.Passed),
                mine.Count(result => result.Outcome == HealthOutcomes.Fixed),
                mine.Count(result => result.Outcome == HealthOutcomes.Failed),
                mine.Count(result => result.Outcome == HealthOutcomes.Error),
                mine.Count(result => result.WouldAct),
                mine.Length == 0 ? null : mine.Max(result => result.CheckedAtUtc));
        })];
    }

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }
}
