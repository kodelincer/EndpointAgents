using ControlPlane.Api.Actions;
using ControlPlane.Api.Agents;
using ControlPlane.Api.Chat;
using ControlPlane.Api.Security;
using EndpointAgents.Shared.Actions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json;

namespace ControlPlane.Api.Dashboard;

public sealed record OverviewCount(string Key, string Label, int Count);
public sealed record OverviewDay(string Date, IReadOnlyDictionary<string, int> Values);
public sealed record OverviewDevice(string AgentId, string MachineName, DateTimeOffset? LastSeenUtc);
public sealed record OverviewDevices(int Enrolled, int Online, int Offline, int SeenLast24Hours, int NotSeen7Days,
    IReadOnlyList<OverviewCount> Windows, IReadOnlyList<OverviewCount> AgentVersions,
    IReadOnlyList<OverviewDevice> LongestOffline);
public sealed record OverviewEscalation(string ConversationId, string UserName, string MachineName,
    string? Reason, DateTimeOffset? EscalatedAtUtc);
public sealed record OverviewChats(int Started, int Solved, int Escalated, int StillOpen,
    int OpenNow, int EscalatedNow, int AgentRuns, int AgentRunsFailed,
    IReadOnlyList<OverviewDay> Daily, IReadOnlyList<OverviewEscalation> RecentEscalations);
public sealed record OverviewOperation(string Id, string Name, int Runs, int Worked, int Failed, int Blocked);
public sealed record OverviewOperations(int Runs, int Worked, int Failed, int Blocked,
    IReadOnlyList<OverviewCount> BySource, IReadOnlyList<OverviewDay> Daily, IReadOnlyList<OverviewOperation> Top);
public sealed record OverviewPackage(string ArtifactId, int Tries, int Worked, int Failed, DateTimeOffset LastTriedUtc);
public sealed record OverviewApproval(string ApprovalId, string Kind, string Title, DateTimeOffset CreatedAtUtc);
public sealed record OverviewApprovals(int Pending, DateTimeOffset? OldestPendingUtc,
    IReadOnlyList<OverviewCount> DecidedInRange, IReadOnlyList<OverviewApproval> Waiting);
public sealed record OverviewFeatureRequest(string RequestId, string Summary, string Status, DateTimeOffset CreatedAtUtc);
public sealed record OverviewFeatureRequests(int Total, int New, int InRange,
    IReadOnlyList<OverviewCount> ByStatus, IReadOnlyList<OverviewFeatureRequest> Newest);
public sealed record OverviewHealthRule(string RuleId, string Name, bool Enabled, int Passed, int Fixed,
    int Failed, int Errors, DateTimeOffset? LastCheckedUtc);
public sealed record OverviewHealth(int Rules, int Passed, int Fixed, int Failed, int Errors,
    IReadOnlyList<OverviewHealthRule> ByRule);
public sealed record OverviewUpdate(string AgentId, string MachineName, string Status, string? FromVersion,
    string? ToVersion, string? Message, DateTimeOffset? ReportedAtUtc);
public sealed record OverviewUpdates(IReadOnlyList<OverviewCount> ByStatus, int RolledBackInRange,
    IReadOnlyList<OverviewUpdate> Problems);
public sealed record OverviewSkill(string Name, int Uses, int Solved, int Escalated);

/// <summary>Everything the Overview page shows, read from the control-plane database in one go.
/// Plain counting only: no AI and nothing is asked of the PCs.</summary>
public sealed record OverviewSnapshot(DateTimeOffset GeneratedAtUtc, int Days, DateTimeOffset SinceUtc,
    OverviewDevices Devices, OverviewChats Chats, OverviewOperations Operations,
    IReadOnlyList<OverviewPackage> Packages, OverviewApprovals Approvals,
    OverviewFeatureRequests FeatureRequests, OverviewHealth Health, OverviewUpdates Updates,
    IReadOnlyList<OverviewCount> Lessons, IReadOnlyList<OverviewSkill> Skills,
    /// <summary>Reserved for agent problem reports from PCs; null until that feature exists.</summary>
    object? Problems);

public sealed class OverviewStore(IOptions<ChatOptions> options, AgentRegistry registry, TimeProvider clock)
{
    public const int MinDays = 1;
    public const int MaxDays = 90;
    private readonly string _databasePath = Path.GetFullPath(options.Value.DatabasePath);

    public static readonly IReadOnlySet<string> WorkedStatuses =
        new HashSet<string>(["Ok", "Done", "NotNeeded"], StringComparer.Ordinal);
    public static readonly IReadOnlySet<string> BlockedStatuses =
        new HashSet<string>(["Blocked", "Rejected"], StringComparer.Ordinal);

    public async Task<OverviewSnapshot> GetAsync(int days, int utcOffsetMinutes, CancellationToken cancellationToken)
    {
        days = Math.Clamp(days, MinDays, MaxDays);
        var offset = TimeSpan.FromMinutes(Math.Clamp(utcOffsetMinutes, -14 * 60, 14 * 60));
        var now = clock.GetUtcNow();
        // Day buckets follow the operator's own calendar; the window starts at local midnight.
        var localToday = now.ToOffset(offset).Date;
        var firstDay = localToday.AddDays(-(days - 1));
        var since = new DateTimeOffset(firstDay, offset).ToUniversalTime();
        var dayKeys = Enumerable.Range(0, days).Select(i => firstDay.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray();
        string DayOf(DateTimeOffset value) => value.ToOffset(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var sinceText = Format(since);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared, Pooling = true
        }.ToString());
        await connection.OpenAsync(cancellationToken);

        var machines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var devices = await ReadDevicesAsync(connection, now, machines, cancellationToken);
        var chats = await ReadChatsAsync(connection, sinceText, dayKeys, DayOf, machines, cancellationToken);
        var operations = await ReadOperationsAsync(connection, sinceText, dayKeys, DayOf, cancellationToken);
        var packages = await ReadPackagesAsync(connection, sinceText, cancellationToken);
        var approvals = await ReadApprovalsAsync(connection, sinceText, cancellationToken);
        var features = await ReadFeatureRequestsAsync(connection, sinceText, cancellationToken);
        var health = await ReadHealthAsync(connection, cancellationToken);
        var updates = await ReadUpdatesAsync(connection, sinceText, machines, cancellationToken);
        var lessons = (await CountsAsync(connection, "SELECT status, COUNT(*) FROM lessons GROUP BY status;", [], cancellationToken))
            .Select(pair => new OverviewCount(pair.Key, pair.Key, pair.Value)).ToArray();
        var skills = await QueryAsync(connection,
            """
            SELECT u.skill_name, COUNT(*),
                SUM(CASE WHEN c.helpdesk_status = 'Resolved' THEN 1 ELSE 0 END),
                SUM(CASE WHEN c.helpdesk_status = 'Escalated' THEN 1 ELSE 0 END)
            FROM skill_usage u LEFT JOIN conversations c ON c.conversation_id = u.conversation_id
            WHERE u.used_at_utc >= $since GROUP BY u.skill_name ORDER BY COUNT(*) DESC, u.skill_name LIMIT 8;
            """, [("$since", sinceText)],
            reader => new OverviewSkill(reader.GetString(0), reader.GetInt32(1), Int(reader, 2), Int(reader, 3)), cancellationToken);

        return new OverviewSnapshot(now, days, since, devices, chats, operations, packages, approvals, features,
            health, updates, lessons, skills, null);
    }

    private async Task<OverviewDevices> ReadDevicesAsync(SqliteConnection connection, DateTimeOffset now,
        Dictionary<string, string> machines, CancellationToken cancellationToken)
    {
        var enrolled = await QueryAsync(connection,
            """
            SELECT d.agent_id, d.machine_name, d.last_seen_at_utc, s.current_version, f.value_json
            FROM devices d
            LEFT JOIN agent_update_status s ON s.agent_id = d.agent_id
            LEFT JOIN device_facts f ON f.agent_id = d.agent_id AND f.fact_key = 'system:windows'
            WHERE d.revoked_at_utc IS NULL;
            """, [],
            reader => (Id: reader.GetString(0), Machine: reader.GetString(1), LastSeen: ParseOrNull(reader, 2),
                Version: reader.IsDBNull(3) ? null : reader.GetString(3), Windows: reader.IsDBNull(4) ? null : reader.GetString(4)),
            cancellationToken);
        foreach (var device in enrolled) machines[device.Id] = device.Machine;

        var online = registry.GetOnlineAgents().ToDictionary(agent => agent.AgentId, StringComparer.OrdinalIgnoreCase);
        foreach (var agent in online.Values) machines.TryAdd(agent.AgentId, agent.MachineName);

        // Online PCs that are not in the enrolled list (development agents) still count as PCs.
        var all = enrolled.Select(device => (device.Id, device.Machine, device.LastSeen, device.Version, device.Windows))
            .Concat(online.Values.Where(agent => enrolled.All(device => !string.Equals(device.Id, agent.AgentId, StringComparison.OrdinalIgnoreCase)))
                .Select(agent => (Id: agent.AgentId, Machine: agent.MachineName, LastSeen: (DateTimeOffset?)now, Version: (string?)agent.Version, Windows: (string?)null)))
            .ToArray();

        var windows = all.GroupBy(device => WindowsFamily(device.Windows))
            .Select(group => new OverviewCount(group.Key, WindowsLabel(group.Key), group.Count()))
            .OrderBy(item => WindowsOrder(item.Key)).ToArray();
        var versions = all.GroupBy(device =>
                online.TryGetValue(device.Id, out var live) && !string.IsNullOrWhiteSpace(live.Version) ? live.Version
                : string.IsNullOrWhiteSpace(device.Version) ? "unknown" : device.Version!)
            .Select(group => new OverviewCount(group.Key, group.Key == "unknown" ? "Not reported" : group.Key, group.Count()))
            .OrderByDescending(item => item.Count).ThenByDescending(item => item.Key, StringComparer.Ordinal).ToArray();
        var offline = all.Where(device => !online.ContainsKey(device.Id)).ToArray();
        return new OverviewDevices(all.Length, all.Length - offline.Length, offline.Length,
            all.Count(device => online.ContainsKey(device.Id) || device.LastSeen >= now.AddDays(-1)),
            offline.Count(device => device.LastSeen is null || device.LastSeen < now.AddDays(-7)),
            windows, versions,
            offline.OrderBy(device => device.LastSeen ?? DateTimeOffset.MinValue).Take(5)
                .Select(device => new OverviewDevice(device.Id, device.Machine, device.LastSeen)).ToArray());
    }

    /// <summary>"win11", "win10", "other" or "unknown" from a kept system:windows fact.</summary>
    public static string WindowsFamily(string? valueJson)
    {
        if (string.IsNullOrWhiteSpace(valueJson)) return "unknown";
        try
        {
            using var document = JsonDocument.Parse(valueJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return "unknown";
            string? Read(string name) => document.RootElement.EnumerateObject()
                .FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)).Value is var value
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var product = Read("productName") ?? "";
            if (int.TryParse(Read("build"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var build) && build > 0)
                return build >= 22000 ? "win11" : build >= 10240 ? "win10" : "other";
            if (product.Contains("Windows 11", StringComparison.OrdinalIgnoreCase)) return "win11";
            if (product.Contains("Windows 10", StringComparison.OrdinalIgnoreCase)) return "win10";
            return product.Length > 0 ? "other" : "unknown";
        }
        catch (JsonException) { return "unknown"; }
    }

    private static string WindowsLabel(string key) => key switch
    {
        "win11" => "Windows 11", "win10" => "Windows 10", "other" => "Other Windows", _ => "Not read yet"
    };

    private static int WindowsOrder(string key) => key switch { "win11" => 0, "win10" => 1, "other" => 2, _ => 3 };

    private static async Task<OverviewChats> ReadChatsAsync(SqliteConnection connection, string since, string[] dayKeys,
        Func<DateTimeOffset, string> dayOf, Dictionary<string, string> machines, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync(connection,
            "SELECT created_at_utc, helpdesk_status FROM conversations WHERE created_at_utc >= $since;",
            [("$since", since)], reader => (Created: Parse(reader.GetString(0)), Status: reader.GetString(1)), cancellationToken);
        static string Bucket(string status) => status switch
        {
            HelpdeskStatus.Resolved => "solved", HelpdeskStatus.Escalated => "escalated", _ => "open"
        };
        var daily = Daily(dayKeys, ["solved", "escalated", "open"], rows.Select(row => (dayOf(row.Created), Bucket(row.Status))));
        var now = await CountsAsync(connection, "SELECT helpdesk_status, COUNT(*) FROM conversations GROUP BY helpdesk_status;", [], cancellationToken);
        var runs = await CountsAsync(connection, "SELECT state, COUNT(*) FROM agent_runs WHERE created_at_utc >= $since GROUP BY state;",
            [("$since", since)], cancellationToken);
        var escalations = await QueryAsync(connection,
            """
            SELECT conversation_id, agent_id, user_name, escalation_reason, escalated_at_utc FROM conversations
            WHERE helpdesk_status = 'Escalated' ORDER BY COALESCE(escalated_at_utc, updated_at_utc) DESC LIMIT 5;
            """, [],
            reader => new OverviewEscalation(reader.GetString(0),
                reader.GetString(2), machines.GetValueOrDefault(reader.GetString(1), reader.GetString(1)),
                reader.IsDBNull(3) ? null : reader.GetString(3), ParseOrNull(reader, 4)), cancellationToken);
        return new OverviewChats(rows.Count,
            rows.Count(row => Bucket(row.Status) == "solved"), rows.Count(row => Bucket(row.Status) == "escalated"),
            rows.Count(row => Bucket(row.Status) == "open"),
            now.GetValueOrDefault(HelpdeskStatus.New) + now.GetValueOrDefault(HelpdeskStatus.Open),
            now.GetValueOrDefault(HelpdeskStatus.Escalated),
            runs.Values.Sum(), runs.GetValueOrDefault("Failed"), daily, escalations);
    }

    private static async Task<OverviewOperations> ReadOperationsAsync(SqliteConnection connection, string since, string[] dayKeys,
        Func<DateTimeOffset, string> dayOf, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync(connection,
            "SELECT action_id, status, source, started_at_utc FROM action_runs WHERE started_at_utc >= $since;",
            [("$since", since)],
            reader => (Action: reader.GetString(0), Status: reader.GetString(1), Source: reader.GetString(2), Started: Parse(reader.GetString(3))),
            cancellationToken);
        // Runs still going or waiting for approval are left out of worked/failed.
        static string? Bucket(string status) => WorkedStatuses.Contains(status) ? "worked"
            : status == "Failed" ? "failed" : BlockedStatuses.Contains(status) ? "blocked" : null;
        var finished = rows.Where(row => Bucket(row.Status) is not null).ToArray();
        var top = finished.GroupBy(row => row.Action)
            .Select(group => new OverviewOperation(group.Key, EndpointActionCatalog.Find(group.Key)?.Name ?? group.Key, group.Count(),
                group.Count(row => Bucket(row.Status) == "worked"), group.Count(row => Bucket(row.Status) == "failed"),
                group.Count(row => Bucket(row.Status) == "blocked")))
            .OrderByDescending(item => item.Runs).ThenBy(item => item.Name, StringComparer.Ordinal).Take(8).ToArray();
        var bySource = finished.GroupBy(row => row.Source)
            .Select(group => new OverviewCount(group.Key, SourceLabel(group.Key), group.Count()))
            .OrderByDescending(item => item.Count).ToArray();
        return new OverviewOperations(finished.Length, finished.Count(row => Bucket(row.Status) == "worked"),
            finished.Count(row => Bucket(row.Status) == "failed"), finished.Count(row => Bucket(row.Status) == "blocked"),
            bySource, Daily(dayKeys, ["worked", "failed", "blocked"], finished.Select(row => (dayOf(row.Started), Bucket(row.Status)!))), top);
    }

    private static string SourceLabel(string source) => source switch
    {
        ActionRunSources.Agent => "Support agent (chat)",
        ActionRunSources.Operator => "Helpdesk member",
        ActionRunSources.Approval => "After approval",
        ActionRunSources.HealthRule => "Health rule",
        _ => source
    };

    private static async Task<IReadOnlyList<OverviewPackage>> ReadPackagesAsync(SqliteConnection connection, string since,
        CancellationToken cancellationToken)
    {
        var rows = await QueryAsync(connection,
            """
            SELECT payload_json, outcome, created_at_utc FROM helpdesk_trace_events
            WHERE event_type = $type AND created_at_utc >= $since;
            """, [("$type", ChatStore.RepairAttemptEventType), ("$since", since)],
            reader => (Payload: reader.GetString(0), Outcome: reader.GetString(1), At: Parse(reader.GetString(2))),
            cancellationToken);
        var attempts = new List<(string Artifact, bool Worked, DateTimeOffset At)>();
        foreach (var row in rows)
        {
            try
            {
                var attempt = JsonSerializer.Deserialize<RepairAttempt>(row.Payload, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (attempt is null) continue;
                var name = string.IsNullOrWhiteSpace(attempt.ArtifactId) ? attempt.ToolName : attempt.ArtifactId;
                attempts.Add((name, attempt.Outcome == RepairAttemptOutcomes.Completed, row.At));
            }
            catch (JsonException) { }
        }
        return attempts.GroupBy(item => item.Artifact)
            .Select(group => new OverviewPackage(group.Key, group.Count(), group.Count(item => item.Worked),
                group.Count(item => !item.Worked), group.Max(item => item.At)))
            .OrderByDescending(item => item.Tries).ThenBy(item => item.ArtifactId, StringComparer.Ordinal).Take(8).ToArray();
    }

    private static async Task<OverviewApprovals> ReadApprovalsAsync(SqliteConnection connection, string since,
        CancellationToken cancellationToken)
    {
        var waiting = await QueryAsync(connection,
            "SELECT approval_id, kind, title, created_at_utc FROM action_approvals WHERE status = 'Pending' ORDER BY created_at_utc;",
            [], reader => new OverviewApproval(reader.GetString(0), reader.GetString(1), reader.GetString(2), Parse(reader.GetString(3))),
            cancellationToken);
        var decided = await CountsAsync(connection,
            "SELECT status, COUNT(*) FROM action_approvals WHERE status <> 'Pending' AND COALESCE(decided_at_utc, created_at_utc) >= $since GROUP BY status;",
            [("$since", since)], cancellationToken);
        return new OverviewApprovals(waiting.Count, waiting.Count > 0 ? waiting[0].CreatedAtUtc : null,
            decided.Select(pair => new OverviewCount(pair.Key, pair.Key, pair.Value)).OrderByDescending(item => item.Count).ToArray(),
            waiting.Take(5).ToArray());
    }

    private static async Task<OverviewFeatureRequests> ReadFeatureRequestsAsync(SqliteConnection connection, string since,
        CancellationToken cancellationToken)
    {
        var byStatus = await CountsAsync(connection, "SELECT status, COUNT(*) FROM feature_requests GROUP BY status;", [], cancellationToken);
        var inRange = (await CountsAsync(connection,
            "SELECT 'all', COUNT(*) FROM feature_requests WHERE created_at_utc >= $since;", [("$since", since)], cancellationToken))
            .GetValueOrDefault("all");
        var newest = await QueryAsync(connection,
            "SELECT request_id, summary, status, created_at_utc FROM feature_requests ORDER BY created_at_utc DESC LIMIT 5;", [],
            reader => new OverviewFeatureRequest(reader.GetString(0), reader.GetString(1), reader.GetString(2), Parse(reader.GetString(3))),
            cancellationToken);
        return new OverviewFeatureRequests(byStatus.Values.Sum(), byStatus.GetValueOrDefault(FeatureRequestStatus.New), inRange,
            byStatus.Select(pair => new OverviewCount(pair.Key, pair.Key, pair.Value)).OrderByDescending(item => item.Count).ToArray(),
            newest);
    }

    private static async Task<OverviewHealth> ReadHealthAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var rules = await QueryAsync(connection,
            """
            SELECT r.rule_id, r.name, r.enabled,
                SUM(CASE WHEN h.outcome = 'passed' THEN 1 ELSE 0 END),
                SUM(CASE WHEN h.outcome = 'fixed' THEN 1 ELSE 0 END),
                SUM(CASE WHEN h.outcome = 'failed' THEN 1 ELSE 0 END),
                SUM(CASE WHEN h.outcome = 'error' THEN 1 ELSE 0 END),
                MAX(h.checked_at_utc)
            FROM health_rules r LEFT JOIN health_results h ON h.rule_id = r.rule_id
            GROUP BY r.rule_id, r.name, r.enabled ORDER BY r.name;
            """, [],
            reader => new OverviewHealthRule(reader.GetString(0), reader.GetString(1), reader.GetInt64(2) != 0,
                Int(reader, 3), Int(reader, 4), Int(reader, 5), Int(reader, 6), ParseOrNull(reader, 7)),
            cancellationToken);
        return new OverviewHealth(rules.Count, rules.Sum(rule => rule.Passed), rules.Sum(rule => rule.Fixed),
            rules.Sum(rule => rule.Failed), rules.Sum(rule => rule.Errors), rules);
    }

    private static async Task<OverviewUpdates> ReadUpdatesAsync(SqliteConnection connection, string since,
        Dictionary<string, string> machines, CancellationToken cancellationToken)
    {
        var byStatus = await CountsAsync(connection,
            "SELECT COALESCE(last_status, 'None'), COUNT(*) FROM agent_update_status GROUP BY COALESCE(last_status, 'None');", [],
            cancellationToken);
        var rolledBack = (await CountsAsync(connection,
            "SELECT 'all', COUNT(*) FROM agent_update_status WHERE last_status = 'RolledBack' AND last_report_utc >= $since;",
            [("$since", since)], cancellationToken)).GetValueOrDefault("all");
        var problems = await QueryAsync(connection,
            """
            SELECT agent_id, last_status, last_from_version, last_to_version, last_message, last_report_utc
            FROM agent_update_status WHERE last_status IN ('RolledBack', 'Failed')
            ORDER BY last_report_utc DESC LIMIT 5;
            """, [],
            reader => new OverviewUpdate(reader.GetString(0), machines.GetValueOrDefault(reader.GetString(0), reader.GetString(0)),
                reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), ParseOrNull(reader, 5)), cancellationToken);
        return new OverviewUpdates(byStatus.Where(pair => pair.Key != "None")
                .Select(pair => new OverviewCount(pair.Key, pair.Key, pair.Value)).OrderByDescending(item => item.Count).ToArray(),
            rolledBack, problems);
    }

    /// <summary>One entry per day, every series present (zero when nothing happened).</summary>
    public static IReadOnlyList<OverviewDay> Daily(IReadOnlyList<string> dayKeys, IReadOnlyList<string> series,
        IEnumerable<(string Day, string Series)> items)
    {
        var map = dayKeys.ToDictionary(day => day, _ => series.ToDictionary(name => name, _ => 0, StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var (day, name) in items)
            if (map.TryGetValue(day, out var values) && values.ContainsKey(name)) values[name]++;
        return dayKeys.Select(day => new OverviewDay(day, map[day])).ToArray();
    }

    private static async Task<Dictionary<string, int>> CountsAsync(SqliteConnection connection, string sql,
        IReadOnlyList<(string Name, object? Value)> parameters, CancellationToken cancellationToken)
    {
        var pairs = await QueryAsync(connection, sql, parameters,
            reader => (Key: reader.IsDBNull(0) ? "" : reader.GetString(0), Count: reader.GetInt32(1)), cancellationToken);
        return pairs.ToDictionary(pair => pair.Key, pair => pair.Count, StringComparer.Ordinal);
    }

    private static async Task<List<T>> QueryAsync<T>(SqliteConnection connection, string sql,
        IReadOnlyList<(string Name, object? Value)> parameters, Func<SqliteDataReader, T> read,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var items = new List<T>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) items.Add(read(reader));
        }
        // A table another part creates later (fresh database) simply has nothing to count yet.
        catch (SqliteException error) when (error.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase)) { }
        return items;
    }

    private static int Int(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? 0 : reader.GetInt32(ordinal);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static DateTimeOffset? ParseOrNull(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) || string.IsNullOrWhiteSpace(reader.GetString(ordinal)) ? null
        : DateTimeOffset.TryParse(reader.GetString(ordinal), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;

    private static string Format(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}

public static class OverviewEndpoints
{
    public static void MapOverviewEndpoints(this WebApplication app) =>
        app.MapGet("/api/overview", async (int? days, int? utcOffsetMinutes, OverviewStore store, CancellationToken cancellationToken) =>
            Results.Ok(await store.GetAsync(days ?? 14, utcOffsetMinutes ?? 0, cancellationToken)))
            .RequireAuthorization(OperatorPolicies.Operator);
}
