using ControlPlane.Api.Chat;
using EndpointAgents.Shared.Support;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json.Nodes;

namespace ControlPlane.Api.Inventory;

/// <summary>
/// "Keep, don't schedule": facts a tool already read from a PC are kept with the time they
/// were read, and given again while they are fresh, so the PC is not asked twice. PCs never
/// collect inventory on a timer and no AI is involved.
/// </summary>
public static class InventoryKeys
{
    public static string System(string section) => "system:" + section;
    public static string Action(string actionId) => "action:" + actionId;

    /// <summary>How long each kind of fact stays fresh. Network facts are never kept: they change and include addresses.</summary>
    public static IReadOnlyDictionary<string, TimeSpan> MaxAge { get; } = new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
    {
        [System(SystemInfoContracts.Hardware)] = TimeSpan.FromDays(7),
        [System(SystemInfoContracts.Windows)] = TimeSpan.FromDays(1),
        [System(SystemInfoContracts.Domain)] = TimeSpan.FromDays(1),
        [System(SystemInfoContracts.Storage)] = TimeSpan.FromDays(1),
        [Action("software.inventory")] = TimeSpan.FromDays(1),
        [Action("drivers.inventory")] = TimeSpan.FromDays(7),
        [Action("disk.health")] = TimeSpan.FromDays(7),
        [Action("bitlocker.status")] = TimeSpan.FromDays(1)
    };

    public static bool IsKept(string key) => MaxAge.ContainsKey(key);
}

public sealed record StoredFact(string AgentId, string MachineName, string Key, JsonNode? Value, DateTimeOffset ReadAtUtc)
{
    public bool IsFresh(DateTimeOffset now) =>
        InventoryKeys.MaxAge.TryGetValue(Key, out var age) && now - ReadAtUtc <= age;
}

public sealed record StoredDevice(string AgentId, string MachineName, DateTimeOffset LastReadUtc, int Facts);

public sealed class InventoryStore(IOptions<ChatOptions> chatOptions, TimeProvider clock)
{
    public const int MaxValueCharacters = 256 * 1024;
    private readonly string _databasePath = Path.GetFullPath(chatOptions.Value.DatabasePath);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await using var connection = await OpenAsync(cancellationToken);
        await using var create = connection.CreateCommand();
        create.CommandText =
            """
            CREATE TABLE IF NOT EXISTS device_facts (
                agent_id TEXT NOT NULL,
                machine_name TEXT NOT NULL,
                fact_key TEXT NOT NULL,
                value_json TEXT NOT NULL,
                read_at_utc TEXT NOT NULL,
                PRIMARY KEY (agent_id, fact_key)
            );
            """;
        await create.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Keeps one fact; ignores keys that are not kept and answers that are too large.</summary>
    public async Task SaveAsync(string agentId, string? machineName, string key, JsonNode? value,
        CancellationToken cancellationToken)
    {
        if (!InventoryKeys.IsKept(key) || value is null) return;
        var json = value.ToJsonString();
        if (json.Length > MaxValueCharacters) return;
        await using var connection = await OpenAsync(cancellationToken);
        await using var upsert = connection.CreateCommand();
        upsert.CommandText =
            """
            INSERT INTO device_facts VALUES ($agent, $machine, $key, $value, $at)
            ON CONFLICT (agent_id, fact_key) DO UPDATE SET
                machine_name = CASE WHEN $machine = '' THEN machine_name ELSE $machine END,
                value_json = $value, read_at_utc = $at;
            """;
        upsert.Parameters.AddWithValue("$agent", agentId);
        upsert.Parameters.AddWithValue("$machine", machineName ?? string.Empty);
        upsert.Parameters.AddWithValue("$key", key);
        upsert.Parameters.AddWithValue("$value", json);
        upsert.Parameters.AddWithValue("$at", clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        await upsert.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The stored fact when it is still fresh, otherwise null.</summary>
    public async Task<StoredFact?> GetFreshAsync(string agentId, string key, CancellationToken cancellationToken)
    {
        var fact = (await ReadAsync(agentId, key, cancellationToken)).FirstOrDefault();
        return fact is not null && fact.IsFresh(clock.GetUtcNow()) ? fact : null;
    }

    public Task<IReadOnlyList<StoredFact>> GetDeviceAsync(string agentId, CancellationToken cancellationToken) =>
        ReadAsync(agentId, null, cancellationToken);

    public async Task<IReadOnlyList<StoredDevice>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var select = connection.CreateCommand();
        select.CommandText =
            """
            SELECT agent_id, MAX(machine_name), MAX(read_at_utc), COUNT(*) FROM device_facts
            GROUP BY agent_id ORDER BY MAX(machine_name);
            """;
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        var devices = new List<StoredDevice>();
        while (await reader.ReadAsync(cancellationToken))
            devices.Add(new StoredDevice(reader.GetString(0), reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture), reader.GetInt32(3)));
        return devices;
    }

    private async Task<IReadOnlyList<StoredFact>> ReadAsync(string agentId, string? key, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var select = connection.CreateCommand();
        select.CommandText = key is null
            ? "SELECT * FROM device_facts WHERE agent_id = $agent ORDER BY fact_key;"
            : "SELECT * FROM device_facts WHERE agent_id = $agent AND fact_key = $key;";
        select.Parameters.AddWithValue("$agent", agentId);
        if (key is not null) select.Parameters.AddWithValue("$key", key);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        var facts = new List<StoredFact>();
        while (await reader.ReadAsync(cancellationToken))
            facts.Add(new StoredFact(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                JsonNode.Parse(reader.GetString(3)), DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)));
        return facts;
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
}
