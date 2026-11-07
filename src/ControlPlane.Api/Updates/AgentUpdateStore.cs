using ControlPlane.Api.Chat;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json;

namespace ControlPlane.Api.Updates;

/// <summary>Which version endpoints should run, set by an admin on the Agent updates page.</summary>
/// <param name="TargetVersion">The version for every PC; null keeps PCs on what they have.</param>
/// <param name="PilotVersion">A version for the pilot PCs only (tried there first).</param>
/// <param name="WindowStartHour">Updates start only from this hour (server time), with <paramref name="WindowEndHour"/>.</param>
public sealed record AgentUpdateSettings(
    string? TargetVersion,
    string? PilotVersion,
    IReadOnlyList<string> PilotAgentIds,
    bool Paused,
    int? WindowStartHour,
    int? WindowEndHour,
    string? UpdatedBy = null,
    DateTimeOffset? UpdatedAtUtc = null)
{
    public static readonly AgentUpdateSettings Default = new(null, null, [], false, null, null);
}

/// <summary>What the control plane knows about one PC's agent version and its last update.</summary>
public sealed record AgentUpdateDeviceStatus(
    string AgentId,
    string MachineName,
    string? CurrentVersion,
    DateTimeOffset? LastCheckUtc,
    string? LastStatus,
    string? LastFromVersion,
    string? LastToVersion,
    string? LastMessage,
    DateTimeOffset? LastReportUtc);

/// <summary>Agent update settings and per-PC update state, in the control-plane SQLite database.</summary>
public sealed class AgentUpdateStore(IOptions<ChatOptions> options)
{
    private const string SettingsKey = "agent-update-settings";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _databasePath = Path.GetFullPath(options.Value.DatabasePath);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS agent_update_settings (
                setting_key TEXT PRIMARY KEY,
                setting_json TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS agent_update_status (
                agent_id TEXT PRIMARY KEY,
                current_version TEXT NULL,
                last_check_utc TEXT NULL,
                last_status TEXT NULL,
                last_from_version TEXT NULL,
                last_to_version TEXT NULL,
                last_message TEXT NULL,
                last_report_utc TEXT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AgentUpdateSettings> GetSettingsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_json FROM agent_update_settings WHERE setting_key = $key;";
        command.Parameters.AddWithValue("$key", SettingsKey);
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (json is null) return AgentUpdateSettings.Default;
        try { return JsonSerializer.Deserialize<AgentUpdateSettings>(json, Json) ?? AgentUpdateSettings.Default; }
        catch (JsonException) { return AgentUpdateSettings.Default; }
    }

    public async Task SaveSettingsAsync(AgentUpdateSettings settings, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO agent_update_settings (setting_key, setting_json, updated_at_utc)
            VALUES ($key, $json, $now)
            ON CONFLICT(setting_key) DO UPDATE SET setting_json = excluded.setting_json,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$key", SettingsKey);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(settings, Json));
        command.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Remembers the version a PC runs (on every connect and every check).</summary>
    public Task RecordVersionAsync(string agentId, string version, bool isCheck,
        CancellationToken cancellationToken) => ExecAsync(
        isCheck
            ? """
              INSERT INTO agent_update_status (agent_id, current_version, last_check_utc)
              VALUES ($id, $version, $now)
              ON CONFLICT(agent_id) DO UPDATE SET current_version = excluded.current_version,
                  last_check_utc = excluded.last_check_utc;
              """
            : """
              INSERT INTO agent_update_status (agent_id, current_version) VALUES ($id, $version)
              ON CONFLICT(agent_id) DO UPDATE SET current_version = excluded.current_version;
              """,
        [("$id", agentId), ("$version", Limit(version, 40)), ("$now", Format(DateTimeOffset.UtcNow))],
        cancellationToken);

    public Task RecordReportAsync(string agentId, string status, string fromVersion,
        string toVersion, string message, CancellationToken cancellationToken) => ExecAsync(
        """
        INSERT INTO agent_update_status (agent_id, last_status, last_from_version, last_to_version,
            last_message, last_report_utc)
        VALUES ($id, $status, $from, $to, $message, $now)
        ON CONFLICT(agent_id) DO UPDATE SET last_status = excluded.last_status,
            last_from_version = excluded.last_from_version, last_to_version = excluded.last_to_version,
            last_message = excluded.last_message, last_report_utc = excluded.last_report_utc;
        """,
        [("$id", agentId), ("$status", status), ("$from", Limit(fromVersion, 40)),
            ("$to", Limit(toVersion, 40)), ("$message", Limit(message, 1000)),
            ("$now", Format(DateTimeOffset.UtcNow))],
        cancellationToken);

    /// <summary>Every enrolled PC (and any PC that reported without enrolment) with its update state.</summary>
    public async Task<IReadOnlyList<AgentUpdateDeviceStatus>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.agent_id, COALESCE(d.machine_name, ''), s.current_version, s.last_check_utc,
                   s.last_status, s.last_from_version, s.last_to_version, s.last_message, s.last_report_utc
            FROM agent_update_status s
            LEFT JOIN devices d ON d.agent_id = s.agent_id
            WHERE d.revoked_at_utc IS NULL
            UNION ALL
            SELECT d.agent_id, d.machine_name, NULL, NULL, NULL, NULL, NULL, NULL, NULL
            FROM devices d
            WHERE d.revoked_at_utc IS NULL
              AND NOT EXISTS (SELECT 1 FROM agent_update_status s WHERE s.agent_id = d.agent_id)
            ORDER BY 2, 1;
            """;
        var rows = new List<AgentUpdateDeviceStatus>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new AgentUpdateDeviceStatus(reader.GetString(0), reader.GetString(1),
                Text(reader, 2), Time(reader, 3), Text(reader, 4), Text(reader, 5), Text(reader, 6),
                Text(reader, 7), Time(reader, 8)));
        return rows;
    }

    /// <summary>
    /// True while the PC's user is in the middle of a conversation (a run is queued or running,
    /// or the conversation changed in the last <paramref name="quietPeriod"/>), so an update
    /// never closes the Tray in the middle of someone's chat.
    /// </summary>
    public async Task<bool> IsInConversationAsync(string agentId, TimeSpan quietPeriod,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS (
                SELECT 1 FROM conversations c
                WHERE c.agent_id = $id AND (c.updated_at_utc > $since
                    OR EXISTS (SELECT 1 FROM agent_runs r WHERE r.conversation_id = c.conversation_id
                               AND r.state IN ('Queued', 'Running'))));
            """;
        command.Parameters.AddWithValue("$id", agentId);
        command.Parameters.AddWithValue("$since", Format(DateTimeOffset.UtcNow - quietPeriod));
        try { return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0; }
        catch (SqliteException) { return false; }
    }

    private async Task ExecAsync(string sql, (string Name, object? Value)[] parameters,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
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

    private static string? Text(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetString(index);

    private static DateTimeOffset? Time(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : DateTimeOffset.Parse(reader.GetString(index),
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max];

    private static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
