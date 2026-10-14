using ControlPlane.Api.Chat;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json;

namespace ControlPlane.Api.Actions;

/// <summary>
/// Persistence for the general-action system: the environment map, organisation settings,
/// per-action kill switches and limits, the action run log, known incidents, and helpdesk
/// skills. Uses the control-plane SQLite database with its own tables.
/// </summary>
public sealed class ActionStore(IOptions<ChatOptions> options)
{
    private readonly string _databasePath = Path.GetFullPath(options.Value.DatabasePath);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await ExecAsync(
            """
            CREATE TABLE IF NOT EXISTS env_targets (
                target_id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                label TEXT NOT NULL,
                address TEXT NOT NULL,
                ports TEXT NOT NULL DEFAULT '',
                expected_addresses TEXT NOT NULL DEFAULT '',
                drive_letter TEXT NULL,
                location TEXT NULL,
                notes TEXT NULL,
                updated_at_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS env_settings (
                setting_key TEXT PRIMARY KEY,
                setting_value TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS action_settings (
                action_id TEXT PRIMARY KEY,
                enabled INTEGER NOT NULL DEFAULT 1,
                max_per_hour INTEGER NULL,
                updated_at_utc TEXT NOT NULL,
                updated_by TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS action_runs (
                run_id TEXT PRIMARY KEY,
                action_id TEXT NOT NULL,
                agent_id TEXT NOT NULL,
                conversation_id TEXT NULL,
                source TEXT NOT NULL,
                arguments_json TEXT NOT NULL,
                status TEXT NOT NULL,
                summary TEXT NOT NULL,
                result_json TEXT NULL,
                verification_json TEXT NULL,
                undo_json TEXT NULL,
                undone_at_utc TEXT NULL,
                started_at_utc TEXT NOT NULL,
                completed_at_utc TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_action_runs_limit
                ON action_runs(agent_id, action_id, started_at_utc);
            CREATE INDEX IF NOT EXISTS ix_action_runs_started ON action_runs(started_at_utc);
            CREATE TABLE IF NOT EXISTS incidents (
                incident_id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                description TEXT NOT NULL,
                affected_targets TEXT NOT NULL DEFAULT '',
                status TEXT NOT NULL,
                started_at_utc TEXT NOT NULL,
                resolved_at_utc TEXT NULL,
                created_by TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS helpdesk_skills (
                skill_name TEXT PRIMARY KEY,
                status TEXT NOT NULL,
                description TEXT NOT NULL,
                body TEXT NOT NULL,
                author TEXT NOT NULL,
                reviewer TEXT NULL,
                version INTEGER NOT NULL,
                published_version INTEGER NULL,
                review_note TEXT NULL,
                source_conversation_id TEXT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                published_at_utc TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS helpdesk_skill_versions (
                skill_name TEXT NOT NULL,
                version INTEGER NOT NULL,
                description TEXT NOT NULL,
                body TEXT NOT NULL,
                author TEXT NOT NULL,
                note TEXT NULL,
                created_at_utc TEXT NOT NULL,
                PRIMARY KEY(skill_name, version)
            );
            CREATE TABLE IF NOT EXISTS skill_usage (
                usage_id INTEGER PRIMARY KEY AUTOINCREMENT,
                skill_name TEXT NOT NULL,
                conversation_id TEXT NOT NULL,
                run_id TEXT NULL,
                used_at_utc TEXT NOT NULL,
                UNIQUE(skill_name, conversation_id)
            );
            """, [], cancellationToken);
    }

    // ---------------- Environment map ----------------

    public Task<IReadOnlyList<EnvironmentTarget>> GetTargetsAsync(
        CancellationToken cancellationToken) =>
        QueryAsync("SELECT * FROM env_targets ORDER BY kind, label;", [], ReadTarget,
            cancellationToken);

    public async Task<EnvironmentTarget?> GetTargetAsync(string id,
        CancellationToken cancellationToken) =>
        (await QueryAsync("SELECT * FROM env_targets WHERE target_id = $id;",
            [("$id", id)], ReadTarget, cancellationToken)).FirstOrDefault();

    public Task UpsertTargetAsync(EnvironmentTargetInput input,
        CancellationToken cancellationToken) =>
        ExecAsync(
            """
            INSERT INTO env_targets(target_id, kind, label, address, ports, expected_addresses,
                drive_letter, location, notes, updated_at_utc)
            VALUES($id, $kind, $label, $address, $ports, $expected, $drive, $location, $notes, $now)
            ON CONFLICT(target_id) DO UPDATE SET kind = $kind, label = $label,
                address = $address, ports = $ports, expected_addresses = $expected,
                drive_letter = $drive, location = $location, notes = $notes,
                updated_at_utc = $now;
            """,
            [("$id", input.Id), ("$kind", input.Kind), ("$label", input.Label.Trim()),
                ("$address", input.Address.Trim()),
                ("$ports", string.Join(',', input.Ports ?? [])),
                ("$expected", string.Join(',', input.ExpectedAddresses ?? [])),
                ("$drive", input.DriveLetter?.ToUpperInvariant()),
                ("$location", Empty(input.Location)), ("$notes", Empty(input.Notes)),
                ("$now", Now())],
            cancellationToken);

    public Task<int> DeleteTargetAsync(string id, CancellationToken cancellationToken) =>
        ExecAsync("DELETE FROM env_targets WHERE target_id = $id;", [("$id", id)],
            cancellationToken);

    public async Task<IReadOnlyDictionary<string, string>> GetSettingsAsync(
        CancellationToken cancellationToken) =>
        (await QueryAsync("SELECT setting_key, setting_value FROM env_settings;", [],
            reader => (Key: reader.GetString(0), Value: reader.GetString(1)),
            cancellationToken))
        .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);

    public async Task SaveSettingsAsync(IReadOnlyDictionary<string, string?> values,
        CancellationToken cancellationToken)
    {
        foreach (var (key, value) in values)
            if (string.IsNullOrWhiteSpace(value))
                await ExecAsync("DELETE FROM env_settings WHERE setting_key = $key;",
                    [("$key", key)], cancellationToken);
            else
                await ExecAsync(
                    """
                    INSERT INTO env_settings(setting_key, setting_value, updated_at_utc)
                    VALUES($key, $value, $now)
                    ON CONFLICT(setting_key) DO UPDATE SET setting_value = $value, updated_at_utc = $now;
                    """, [("$key", key), ("$value", value.Trim()), ("$now", Now())],
                    cancellationToken);
    }

    // ---------------- Kill switches and limits ----------------

    public async Task<IReadOnlyDictionary<string, ActionSetting>> GetActionSettingsAsync(
        CancellationToken cancellationToken) =>
        (await QueryAsync("SELECT * FROM action_settings;", [], reader => new ActionSetting(
            reader.GetString(0), reader.GetInt64(1) == 1,
            reader.IsDBNull(2) ? null : reader.GetInt32(2), Parse(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4)), cancellationToken))
        .ToDictionary(item => item.ActionId, StringComparer.Ordinal);

    public Task SaveActionSettingAsync(string actionId, ActionSettingInput input,
        string? updatedBy, CancellationToken cancellationToken) =>
        ExecAsync(
            """
            INSERT INTO action_settings(action_id, enabled, max_per_hour, updated_at_utc, updated_by)
            VALUES($id, $enabled, $max, $now, $by)
            ON CONFLICT(action_id) DO UPDATE SET enabled = $enabled, max_per_hour = $max,
                updated_at_utc = $now, updated_by = $by;
            """,
            [("$id", actionId), ("$enabled", input.Enabled ? 1 : 0),
                ("$max", input.MaxPerHour), ("$now", Now()), ("$by", updatedBy)],
            cancellationToken);

    // ---------------- Run log ----------------

    public async Task<int> CountRecentRunsAsync(string agentId, string actionId,
        TimeSpan window, CancellationToken cancellationToken) =>
        (await QueryAsync(
            """
            SELECT COUNT(*) FROM action_runs
            WHERE agent_id = $agent AND action_id = $action AND started_at_utc >= $since
              AND status <> 'Rejected';
            """,
            [("$agent", agentId), ("$action", actionId),
                ("$since", Format(DateTimeOffset.UtcNow - window))],
            reader => reader.GetInt32(0), cancellationToken)).Single();

    public Task StartRunAsync(string runId, string actionId, string agentId,
        string? conversationId, string source, string argumentsJson,
        CancellationToken cancellationToken) =>
        ExecAsync(
            """
            INSERT INTO action_runs(run_id, action_id, agent_id, conversation_id, source,
                arguments_json, status, summary, started_at_utc)
            VALUES($run, $action, $agent, $conversation, $source, $args, 'Running', '', $now);
            """,
            [("$run", runId), ("$action", actionId), ("$agent", agentId),
                ("$conversation", conversationId), ("$source", source),
                ("$args", argumentsJson), ("$now", Now())],
            cancellationToken);

    public Task CompleteRunAsync(string runId, string status, string summary,
        string? resultJson, string? verificationJson, string? undoJson,
        CancellationToken cancellationToken) =>
        ExecAsync(
            """
            UPDATE action_runs SET status = $status, summary = $summary, result_json = $result,
                verification_json = $verification, undo_json = $undo, completed_at_utc = $now
            WHERE run_id = $run;
            """,
            [("$run", runId), ("$status", status), ("$summary", Limit(summary, 500)),
                ("$result", resultJson is null ? null : Limit(resultJson, 32_000)),
                ("$verification", verificationJson is null ? null : Limit(verificationJson, 32_000)),
                ("$undo", undoJson), ("$now", Now())],
            cancellationToken);

    public Task MarkUndoneAsync(string runId, CancellationToken cancellationToken) =>
        ExecAsync("UPDATE action_runs SET undone_at_utc = $now WHERE run_id = $run;",
            [("$run", runId), ("$now", Now())], cancellationToken);

    public async Task<ActionRunRecord?> GetRunAsync(string runId,
        CancellationToken cancellationToken) =>
        (await QueryAsync("SELECT * FROM action_runs WHERE run_id = $run;",
            [("$run", runId)], ReadRun, cancellationToken)).FirstOrDefault();

    public Task<IReadOnlyList<ActionRunRecord>> GetRecentRunsAsync(string? agentId,
        string? conversationId, int limit, CancellationToken cancellationToken) =>
        QueryAsync(
            """
            SELECT * FROM action_runs
            WHERE ($agent IS NULL OR agent_id = $agent)
              AND ($conversation IS NULL OR conversation_id = $conversation)
            ORDER BY started_at_utc DESC LIMIT $limit;
            """,
            [("$agent", agentId), ("$conversation", conversationId),
                ("$limit", Math.Clamp(limit, 1, 500))], ReadRun, cancellationToken);

    public async Task<IReadOnlyDictionary<string, ActionUsageStats>> GetUsageStatsAsync(
        TimeSpan window, CancellationToken cancellationToken) =>
        (await QueryAsync(
            """
            SELECT action_id, COUNT(*),
                SUM(CASE WHEN status IN ('Ok','Done','NotNeeded') THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = 'Failed' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status IN ('Blocked','Rejected') THEN 1 ELSE 0 END),
                MAX(started_at_utc)
            FROM action_runs WHERE started_at_utc >= $since GROUP BY action_id;
            """,
            [("$since", Format(DateTimeOffset.UtcNow - window))],
            reader => new ActionUsageStats(reader.GetString(0), reader.GetInt32(1),
                reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4),
                reader.IsDBNull(5) ? null : Parse(reader.GetString(5))),
            cancellationToken))
        .ToDictionary(item => item.ActionId, StringComparer.Ordinal);

    /// <summary>Adds the action to the conversation's helpdesk timeline.</summary>
    public Task AppendConversationTraceAsync(string conversationId, string outcome,
        object payload, CancellationToken cancellationToken) =>
        ExecAsync(
            """
            INSERT INTO helpdesk_trace_events(event_id, conversation_id, sequence, direction,
                event_type, outcome, payload_json, created_at_utc)
            SELECT $event, $conversation, NULL, 'Agent', 'agent.endpoint_action', $outcome,
                $payload, $now
            WHERE EXISTS(SELECT 1 FROM conversations WHERE conversation_id = $conversation);
            """,
            [("$event", Guid.NewGuid().ToString("N")), ("$conversation", conversationId),
                ("$outcome", outcome), ("$payload", JsonSerializer.Serialize(payload)),
                ("$now", Now())],
            cancellationToken);

    // ---------------- Fleet views ----------------

    /// <summary>First user messages of conversations started recently on other devices.</summary>
    public Task<IReadOnlyList<(string ConversationId, string Body, DateTimeOffset CreatedAtUtc, string HelpdeskStatus)>>
        GetRecentFirstMessagesAsync(string excludeAgentId, TimeSpan window, int limit,
            CancellationToken cancellationToken) =>
        QueryAsync(
            """
            SELECT c.conversation_id, m.body, m.created_at_utc, c.helpdesk_status
            FROM conversations c
            JOIN messages m ON m.conversation_id = c.conversation_id AND m.role = 'User'
                AND m.sequence = (SELECT MIN(first.sequence) FROM messages first
                    WHERE first.conversation_id = c.conversation_id AND first.role = 'User'
                      AND first.body <> '')
            WHERE c.agent_id <> $agent AND m.created_at_utc >= $since
            ORDER BY m.created_at_utc DESC LIMIT $limit;
            """,
            [("$agent", excludeAgentId), ("$since", Format(DateTimeOffset.UtcNow - window)),
                ("$limit", limit)],
            reader => (reader.GetString(0), reader.GetString(1), Parse(reader.GetString(2)),
                reader.GetString(3)),
            cancellationToken);

    // ---------------- Missing-action requests ----------------

    /// <summary>A helpdesk member asks the developers for an action the catalogue lacks;
    /// it lands in the same Feature requests list the agent writes to.</summary>
    public Task RecordMissingActionRequestAsync(string summary, string details,
        string requestedBy, CancellationToken cancellationToken) =>
        ExecAsync(
            """
            INSERT INTO feature_requests(request_id, conversation_id, agent_id, user_name,
                summary, user_words, status, created_at_utc, updated_at_utc)
            VALUES($id, '', '', $by, $summary, $details, 'New', $now, $now);
            """,
            [("$id", Guid.NewGuid().ToString("N")), ("$by", Limit(requestedBy, 100)),
                ("$summary", Limit("New action: " + summary.Trim(), 200)),
                ("$details", Limit(details.Trim(), 500)), ("$now", Now())],
            cancellationToken);

    // ---------------- Incidents ----------------

    public Task<IReadOnlyList<Incident>> GetIncidentsAsync(bool activeOnly,
        CancellationToken cancellationToken) =>
        QueryAsync(
            """
            SELECT * FROM incidents WHERE ($active = 0 OR status = 'Active')
            ORDER BY started_at_utc DESC LIMIT 200;
            """, [("$active", activeOnly ? 1 : 0)], ReadIncident, cancellationToken);

    public async Task<Incident> CreateIncidentAsync(IncidentInput input, string? createdBy,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        await ExecAsync(
            """
            INSERT INTO incidents(incident_id, title, description, affected_targets, status,
                started_at_utc, created_by)
            VALUES($id, $title, $description, $targets, 'Active', $now, $by);
            """,
            [("$id", id), ("$title", Limit(input.Title.Trim(), 200)),
                ("$description", Limit(input.Description?.Trim() ?? "", 1000)),
                ("$targets", string.Join(',', input.AffectedTargets ?? [])),
                ("$now", Now()), ("$by", createdBy)],
            cancellationToken);
        return (await GetIncidentsAsync(false, cancellationToken)).First(item => item.IncidentId == id);
    }

    public async Task<bool> PatchIncidentAsync(string id, IncidentPatch patch,
        CancellationToken cancellationToken) =>
        await ExecAsync(
            """
            UPDATE incidents SET
                title = COALESCE($title, title),
                description = COALESCE($description, description),
                affected_targets = COALESCE($targets, affected_targets),
                status = COALESCE($status, status),
                resolved_at_utc = CASE WHEN $status = 'Resolved' THEN $now
                                       WHEN $status = 'Active' THEN NULL ELSE resolved_at_utc END
            WHERE incident_id = $id;
            """,
            [("$id", id), ("$title", patch.Title is null ? null : Limit(patch.Title.Trim(), 200)),
                ("$description", patch.Description is null ? null : Limit(patch.Description.Trim(), 1000)),
                ("$targets", patch.AffectedTargets is null ? null : string.Join(',', patch.AffectedTargets)),
                ("$status", patch.Status), ("$now", Now())],
            cancellationToken) > 0;

    // ---------------- Helpdesk skills ----------------

    public Task<IReadOnlyList<HelpdeskSkill>> GetSkillsAsync(CancellationToken cancellationToken) =>
        QueryAsync("SELECT * FROM helpdesk_skills ORDER BY skill_name;", [], ReadSkill,
            cancellationToken);

    public async Task<HelpdeskSkill?> GetSkillAsync(string name,
        CancellationToken cancellationToken) =>
        (await QueryAsync("SELECT * FROM helpdesk_skills WHERE skill_name = $name;",
            [("$name", name)], ReadSkill, cancellationToken)).FirstOrDefault();

    public Task<IReadOnlyList<HelpdeskSkillVersion>> GetSkillVersionsAsync(string name,
        CancellationToken cancellationToken) =>
        QueryAsync(
            "SELECT * FROM helpdesk_skill_versions WHERE skill_name = $name ORDER BY version DESC;",
            [("$name", name)], reader => new HelpdeskSkillVersion(reader.GetString(0),
                reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), Parse(reader.GetString(6))),
            cancellationToken);

    /// <summary>Saves a new draft version; a published or in-review skill goes back to Draft.</summary>
    public async Task<HelpdeskSkill> SaveSkillDraftAsync(SkillDraftInput input, string author,
        CancellationToken cancellationToken)
    {
        var existing = await GetSkillAsync(input.Name, cancellationToken);
        var version = (existing?.Version ?? 0) + 1;
        var now = Now();
        await ExecAsync(
            """
            INSERT INTO helpdesk_skill_versions(skill_name, version, description, body, author, note, created_at_utc)
            VALUES($name, $version, $description, $body, $author, $note, $now);
            INSERT INTO helpdesk_skills(skill_name, status, description, body, author, version,
                source_conversation_id, created_at_utc, updated_at_utc)
            VALUES($name, 'Draft', $description, $body, $author, $version, $source, $now, $now)
            ON CONFLICT(skill_name) DO UPDATE SET status = 'Draft', description = $description,
                body = $body, author = $author, version = $version, reviewer = NULL,
                review_note = NULL, updated_at_utc = $now;
            """,
            [("$name", input.Name), ("$version", version), ("$description", input.Description.Trim()),
                ("$body", input.Body), ("$author", author), ("$note", Empty(input.Note)),
                ("$source", Empty(input.SourceConversationId)), ("$now", now)],
            cancellationToken);
        return (await GetSkillAsync(input.Name, cancellationToken))!;
    }

    public Task SetSkillStatusAsync(string name, string status, string? reviewer,
        string? note, CancellationToken cancellationToken) =>
        ExecAsync(
            """
            UPDATE helpdesk_skills SET status = $status, reviewer = COALESCE($reviewer, reviewer),
                review_note = $note, updated_at_utc = $now,
                published_version = CASE WHEN $status = 'Published' THEN version ELSE published_version END,
                published_at_utc = CASE WHEN $status = 'Published' THEN $now ELSE published_at_utc END
            WHERE skill_name = $name;
            """,
            [("$name", name), ("$status", status), ("$reviewer", reviewer),
                ("$note", Empty(note)), ("$now", Now())],
            cancellationToken);

    public Task RecordSkillUseAsync(string skillName, string conversationId, string runId,
        CancellationToken cancellationToken) =>
        ExecAsync(
            """
            INSERT OR IGNORE INTO skill_usage(skill_name, conversation_id, run_id, used_at_utc)
            VALUES($skill, $conversation, $run, $now);
            """,
            [("$skill", skillName), ("$conversation", conversationId), ("$run", runId),
                ("$now", Now())],
            cancellationToken);

    public async Task<IReadOnlyDictionary<string, SkillUsageStats>> GetSkillStatsAsync(
        TimeSpan window, CancellationToken cancellationToken) =>
        (await QueryAsync(
            """
            SELECT u.skill_name, COUNT(*),
                SUM(CASE WHEN c.helpdesk_status = 'Resolved' THEN 1 ELSE 0 END),
                SUM(CASE WHEN c.helpdesk_status = 'Escalated' THEN 1 ELSE 0 END),
                MAX(u.used_at_utc)
            FROM skill_usage u LEFT JOIN conversations c ON c.conversation_id = u.conversation_id
            WHERE u.used_at_utc >= $since GROUP BY u.skill_name;
            """,
            [("$since", Format(DateTimeOffset.UtcNow - window))],
            reader => new SkillUsageStats(reader.GetString(0), reader.GetInt32(1),
                reader.GetInt32(2), reader.GetInt32(3),
                reader.IsDBNull(4) ? null : Parse(reader.GetString(4))),
            cancellationToken))
        .ToDictionary(item => item.SkillName, StringComparer.Ordinal);

    /// <summary>What the agent and helpdesk did in one conversation, to start a skill from it.</summary>
    public Task<IReadOnlyList<(string Role, string Body)>> GetConversationMessagesAsync(
        string conversationId, int limit, CancellationToken cancellationToken) =>
        QueryAsync(
            """
            SELECT role, body FROM messages WHERE conversation_id = $id AND body <> ''
            ORDER BY sequence LIMIT $limit;
            """, [("$id", conversationId), ("$limit", limit)],
            reader => (reader.GetString(0), reader.GetString(1)), cancellationToken);

    public async Task<(string? Reason, string? Summary)> GetEscalationAsync(
        string conversationId, CancellationToken cancellationToken) =>
        (await QueryAsync(
            "SELECT escalation_reason, escalation_summary FROM conversations WHERE conversation_id = $id;",
            [("$id", conversationId)],
            reader => (reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1)),
            cancellationToken)).FirstOrDefault();

    // ---------------- Helpers ----------------

    private static EnvironmentTarget ReadTarget(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4).Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(item => int.Parse(item, CultureInfo.InvariantCulture)).ToArray(),
        reader.GetString(5).Split(',', StringSplitOptions.RemoveEmptyEntries),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8), Parse(reader.GetString(9)));

    private static ActionRunRecord ReadRun(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4),
        reader.GetString(5), reader.GetString(6), reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : Parse(reader.GetString(11)), Parse(reader.GetString(12)),
        reader.IsDBNull(13) ? null : Parse(reader.GetString(13)));

    private static Incident ReadIncident(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.GetString(3).Split(',', StringSplitOptions.RemoveEmptyEntries),
        reader.GetString(4), Parse(reader.GetString(5)),
        reader.IsDBNull(6) ? null : Parse(reader.GetString(6)),
        reader.IsDBNull(7) ? null : reader.GetString(7));

    private static HelpdeskSkill ReadSkill(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetInt32(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9), Parse(reader.GetString(10)),
        Parse(reader.GetString(11)), reader.IsDBNull(12) ? null : Parse(reader.GetString(12)));

    private async Task<int> ExecAsync(string sql, (string Name, object? Value)[] parameters,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<T>> QueryAsync<T>(string sql,
        (string Name, object? Value)[] parameters, Func<SqliteDataReader, T> map,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<T>();
            while (await reader.ReadAsync(cancellationToken)) rows.Add(map(reader));
            return rows;
        }
        finally
        {
            _gate.Release();
        }
    }

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

    private static string? Empty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Limit(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static string Now() => Format(DateTimeOffset.UtcNow);

    internal static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    internal static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
