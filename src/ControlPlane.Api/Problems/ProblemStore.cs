using ControlPlane.Api.Chat;
using EndpointAgents.Shared.Problems;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Globalization;

namespace ControlPlane.Api.Problems;

public sealed class ProblemOptions
{
    /// <summary>Empty means problems.db next to the main database. Never the main database itself.</summary>
    public string DatabasePath { get; set; } = "";
    public int RetentionDays { get; set; } = 90;
    public int MaxDatabaseMegabytes { get; set; } = 1024;
    public int MinFreeDiskMegabytes { get; set; } = 1024;
    public int MaxReportsPerPcPerHour { get; set; } = ProblemLimits.MaxReportsPerHour;
    public int LogBundleRetentionDays { get; set; } = 30;
    public int MaxLogBundlesMegabytes { get; set; } = 2048;
    public int QuietAfterHours { get; set; } = 24;
}

public sealed record ProblemOccurrence(
    string Fingerprint, string AgentId, string MachineName, string? AgentVersion,
    string? OsName, string? OsEdition, string? OsVersion, string? OsBuild, string? Architecture,
    string? Manufacturer, string? Model, long Count, DateTimeOffset FirstSeenUtc, DateTimeOffset LastSeenUtc);

public sealed record StoredProblem(
    string Fingerprint, string Kind, string Source, string Title, string? ExceptionType, string? Stack,
    string Status, string? FixedInVersion, string? FixedBy, DateTimeOffset? FixedAtUtc,
    DateTimeOffset? ReopenedAtUtc, string? ConversationId,
    DateTimeOffset FirstSeenUtc, DateTimeOffset LastSeenUtc, long TotalCount);

public sealed record StoredProblemDevice(string AgentId, ProblemDeviceFacts Facts, DateTimeOffset LastContactUtc);

public sealed record LogBundle(
    string BundleId, string AgentId, string MachineName, string RequestedBy, DateTimeOffset RequestedAtUtc,
    string Status, long SizeBytes, DateTimeOffset? CompletedAtUtc, string? Message);

public static class ProblemStatuses
{
    public const string Open = "Open";
    public const string Fixed = "Fixed";
}

public static class LogBundleStatuses
{
    public const string Requested = "Requested";
    public const string Ready = "Ready";
    public const string Failed = "Failed";
}

/// <summary>
/// Problem reports, in their own SQLite file so a flood of reports cannot slow down chats and
/// actions, and the file can be removed without losing anything else. Writes count up one row per
/// problem, PC and version, so the file grows with the number of different problems, not with
/// how often they happen.
/// </summary>
public sealed class ProblemStore(IOptions<ProblemOptions> problemOptions, IOptions<ChatOptions> chatOptions)
{
    private readonly ProblemOptions _options = problemOptions.Value;
    private readonly string _path = ResolvePath(problemOptions.Value, chatOptions.Value);

    public string DatabasePath => _path;
    public string BundleRoot => Path.Combine(Path.GetDirectoryName(_path)!, "problem-logs");

    internal static string ResolvePath(ProblemOptions options, ChatOptions chat) =>
        Path.GetFullPath(options.DatabasePath.Length > 0 ? options.DatabasePath
            : Path.Combine(Path.GetDirectoryName(chat.DatabasePath)!, "problems.db"));

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        Directory.CreateDirectory(BundleRoot);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // auto_vacuum has to be set before the first table, so deleted rows can shrink the file.
        command.CommandText =
            """
            PRAGMA auto_vacuum=INCREMENTAL;
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS problems (
                fingerprint TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                source TEXT NOT NULL,
                title TEXT NOT NULL,
                exception_type TEXT NULL,
                stack TEXT NULL,
                status TEXT NOT NULL,
                fixed_in_version TEXT NULL,
                fixed_by TEXT NULL,
                fixed_at_utc TEXT NULL,
                reopened_at_utc TEXT NULL,
                conversation_id TEXT NULL,
                first_seen_utc TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL,
                total_count INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS problem_occurrences (
                fingerprint TEXT NOT NULL,
                agent_id TEXT NOT NULL,
                machine_name TEXT NOT NULL,
                agent_version TEXT NOT NULL,
                os_name TEXT NULL,
                os_edition TEXT NULL,
                os_version TEXT NULL,
                os_build TEXT NULL,
                architecture TEXT NULL,
                manufacturer TEXT NULL,
                model TEXT NULL,
                count INTEGER NOT NULL,
                first_seen_utc TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL,
                PRIMARY KEY (fingerprint, agent_id, agent_version)
            );
            CREATE INDEX IF NOT EXISTS ix_problem_occurrences_seen ON problem_occurrences(last_seen_utc);
            CREATE TABLE IF NOT EXISTS problem_devices (
                agent_id TEXT PRIMARY KEY,
                machine_name TEXT NULL,
                agent_version TEXT NULL,
                os_name TEXT NULL,
                os_edition TEXT NULL,
                os_version TEXT NULL,
                os_build TEXT NULL,
                architecture TEXT NULL,
                manufacturer TEXT NULL,
                model TEXT NULL,
                last_contact_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS log_bundles (
                bundle_id TEXT PRIMARY KEY,
                agent_id TEXT NOT NULL,
                machine_name TEXT NOT NULL,
                requested_by TEXT NOT NULL,
                requested_at_utc TEXT NOT NULL,
                status TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                completed_at_utc TEXT NULL,
                message TEXT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Adds reports from one PC. A problem marked fixed comes back by itself when it is seen on the
    /// fixed version or a later one; on an older version it stays fixed, because that PC only
    /// has not updated yet.
    /// </summary>
    public async Task AddAsync(string agentId, ProblemDeviceFacts facts, IReadOnlyList<ProblemReport> reports,
        string? conversationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var report in reports)
        {
            var (status, fixedIn) = await ReadStatusAsync(connection, report.Fingerprint, cancellationToken);
            var version = report.AgentVersion ?? facts.AgentVersion ?? "";
            var reopen = status == ProblemStatuses.Fixed && fixedIn is not null
                && version.Length > 0 && ProblemText.CompareVersions(version, fixedIn) >= 0;

            await using (var upsert = connection.CreateCommand())
            {
                upsert.CommandText =
                    """
                    INSERT INTO problems (fingerprint, kind, source, title, exception_type, stack, status,
                        conversation_id, first_seen_utc, last_seen_utc, total_count)
                    VALUES ($fingerprint, $kind, $source, $title, $type, $stack, 'Open',
                        $conversation, $first, $last, $count)
                    ON CONFLICT(fingerprint) DO UPDATE SET
                        title = excluded.title,
                        stack = COALESCE(excluded.stack, problems.stack),
                        first_seen_utc = MIN(problems.first_seen_utc, excluded.first_seen_utc),
                        last_seen_utc = MAX(problems.last_seen_utc, excluded.last_seen_utc),
                        total_count = problems.total_count + excluded.total_count,
                        status = CASE WHEN $reopen = 1 THEN 'Open' ELSE problems.status END,
                        reopened_at_utc = CASE WHEN $reopen = 1 THEN $now ELSE problems.reopened_at_utc END;
                    """;
                upsert.Parameters.AddWithValue("$fingerprint", report.Fingerprint);
                upsert.Parameters.AddWithValue("$kind", report.Kind);
                upsert.Parameters.AddWithValue("$source", report.Source);
                upsert.Parameters.AddWithValue("$title", report.Title);
                upsert.Parameters.AddWithValue("$type", (object?)report.ExceptionType ?? DBNull.Value);
                upsert.Parameters.AddWithValue("$stack", (object?)report.Stack ?? DBNull.Value);
                upsert.Parameters.AddWithValue("$conversation", (object?)conversationId ?? DBNull.Value);
                upsert.Parameters.AddWithValue("$first", Format(report.FirstSeenUtc));
                upsert.Parameters.AddWithValue("$last", Format(report.LastSeenUtc));
                upsert.Parameters.AddWithValue("$count", report.Count);
                upsert.Parameters.AddWithValue("$reopen", reopen ? 1 : 0);
                upsert.Parameters.AddWithValue("$now", Format(now));
                await upsert.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var occurrence = connection.CreateCommand();
            occurrence.CommandText =
                """
                INSERT INTO problem_occurrences (fingerprint, agent_id, machine_name, agent_version, os_name,
                    os_edition, os_version, os_build, architecture, manufacturer, model, count,
                    first_seen_utc, last_seen_utc)
                VALUES ($fingerprint, $agent, $machine, $version, $os, $edition, $osVersion, $build, $arch,
                    $manufacturer, $model, $count, $first, $last)
                ON CONFLICT(fingerprint, agent_id, agent_version) DO UPDATE SET
                    machine_name = excluded.machine_name,
                    os_name = COALESCE(excluded.os_name, problem_occurrences.os_name),
                    os_edition = COALESCE(excluded.os_edition, problem_occurrences.os_edition),
                    os_version = COALESCE(excluded.os_version, problem_occurrences.os_version),
                    os_build = COALESCE(excluded.os_build, problem_occurrences.os_build),
                    architecture = COALESCE(excluded.architecture, problem_occurrences.architecture),
                    manufacturer = COALESCE(excluded.manufacturer, problem_occurrences.manufacturer),
                    model = COALESCE(excluded.model, problem_occurrences.model),
                    count = problem_occurrences.count + excluded.count,
                    first_seen_utc = MIN(problem_occurrences.first_seen_utc, excluded.first_seen_utc),
                    last_seen_utc = MAX(problem_occurrences.last_seen_utc, excluded.last_seen_utc);
                """;
            occurrence.Parameters.AddWithValue("$fingerprint", report.Fingerprint);
            occurrence.Parameters.AddWithValue("$agent", agentId);
            occurrence.Parameters.AddWithValue("$machine", facts.MachineName ?? "");
            occurrence.Parameters.AddWithValue("$version", version);
            AddFacts(occurrence, facts);
            occurrence.Parameters.AddWithValue("$count", report.Count);
            occurrence.Parameters.AddWithValue("$first", Format(report.FirstSeenUtc));
            occurrence.Parameters.AddWithValue("$last", Format(report.LastSeenUtc));
            await occurrence.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Remembers the facts of a PC and that it was in contact now.</summary>
    public async Task SaveDeviceAsync(string agentId, ProblemDeviceFacts facts, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO problem_devices (agent_id, machine_name, agent_version, os_name, os_edition, os_version,
                os_build, architecture, manufacturer, model, last_contact_utc)
            VALUES ($agent, $machine, $version, $os, $edition, $osVersion, $build, $arch, $manufacturer, $model, $now)
            ON CONFLICT(agent_id) DO UPDATE SET
                machine_name = COALESCE(excluded.machine_name, problem_devices.machine_name),
                agent_version = COALESCE(excluded.agent_version, problem_devices.agent_version),
                os_name = COALESCE(excluded.os_name, problem_devices.os_name),
                os_edition = COALESCE(excluded.os_edition, problem_devices.os_edition),
                os_version = COALESCE(excluded.os_version, problem_devices.os_version),
                os_build = COALESCE(excluded.os_build, problem_devices.os_build),
                architecture = COALESCE(excluded.architecture, problem_devices.architecture),
                manufacturer = COALESCE(excluded.manufacturer, problem_devices.manufacturer),
                model = COALESCE(excluded.model, problem_devices.model),
                last_contact_utc = excluded.last_contact_utc;
            """;
        command.Parameters.AddWithValue("$agent", agentId);
        command.Parameters.AddWithValue("$machine", (object?)facts.MachineName ?? DBNull.Value);
        command.Parameters.AddWithValue("$version", (object?)facts.AgentVersion ?? DBNull.Value);
        AddFacts(command, facts);
        command.Parameters.AddWithValue("$now", Format(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<StoredProblemDevice?> GetDeviceAsync(string agentId, CancellationToken cancellationToken) =>
        (await ListDevicesAsync(cancellationToken)).FirstOrDefault(device =>
            string.Equals(device.AgentId, agentId, StringComparison.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<StoredProblemDevice>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT agent_id, machine_name, agent_version, os_name, os_edition, os_version, os_build,
                   architecture, manufacturer, model, last_contact_utc
            FROM problem_devices;
            """;
        var rows = new List<StoredProblemDevice>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new StoredProblemDevice(reader.GetString(0),
                new ProblemDeviceFacts(Text(reader, 2), Text(reader, 1), Text(reader, 3), Text(reader, 4),
                    Text(reader, 5), Text(reader, 6), Text(reader, 7), Text(reader, 8), Text(reader, 9)),
                Parse(reader.GetString(10))));
        return rows;
    }

    public async Task<IReadOnlyList<StoredProblem>> ListProblemsAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = ProblemSelect + " ORDER BY last_seen_utc DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        var rows = new List<StoredProblem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(ReadProblem(reader));
        return rows;
    }

    public async Task<StoredProblem?> GetProblemAsync(string fingerprint, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = ProblemSelect + " WHERE fingerprint = $fingerprint;";
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProblem(reader) : null;
    }

    public async Task<IReadOnlyList<ProblemOccurrence>> ListOccurrencesAsync(string? fingerprint,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT fingerprint, agent_id, machine_name, agent_version, os_name, os_edition, os_version, os_build,
                   architecture, manufacturer, model, count, first_seen_utc, last_seen_utc
            FROM problem_occurrences
            WHERE $fingerprint IS NULL OR fingerprint = $fingerprint
            ORDER BY last_seen_utc DESC;
            """;
        command.Parameters.AddWithValue("$fingerprint", (object?)fingerprint ?? DBNull.Value);
        var rows = new List<ProblemOccurrence>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new ProblemOccurrence(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3).Length == 0 ? null : reader.GetString(3), Text(reader, 4), Text(reader, 5),
                Text(reader, 6), Text(reader, 7), Text(reader, 8), Text(reader, 9), Text(reader, 10),
                reader.GetInt64(11), Parse(reader.GetString(12)), Parse(reader.GetString(13))));
        return rows;
    }

    public async Task<bool> MarkFixedAsync(string fingerprint, string version, string by, DateTimeOffset now,
        CancellationToken cancellationToken) => await ExecAsync(
        """
        UPDATE problems SET status = 'Fixed', fixed_in_version = $version, fixed_by = $by, fixed_at_utc = $now
        WHERE fingerprint = $fingerprint;
        """,
        cancellationToken, ("$fingerprint", fingerprint), ("$version", version), ("$by", by), ("$now", Format(now))) > 0;

    public async Task<bool> ReopenAsync(string fingerprint, DateTimeOffset now, CancellationToken cancellationToken) =>
        await ExecAsync(
            "UPDATE problems SET status = 'Open', reopened_at_utc = $now WHERE fingerprint = $fingerprint;",
            cancellationToken, ("$fingerprint", fingerprint), ("$now", Format(now))) > 0;

    public long DatabaseBytes()
    {
        long total = 0;
        foreach (var path in new[] { _path, _path + "-wal" })
            if (File.Exists(path)) total += new FileInfo(path).Length;
        return total;
    }

    /// <summary>
    /// Deletes what is older than the retention, then the oldest rows until the file is under the
    /// size limit. Returns the number of occurrence rows removed.
    /// </summary>
    public async Task<long> EnforceLimitsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        long removed = await ExecAsync(
            "DELETE FROM problem_occurrences WHERE last_seen_utc < $cutoff;",
            cancellationToken, ("$cutoff", Format(now.AddDays(-_options.RetentionDays))));
        await DeleteOrphansAsync(cancellationToken);
        var maxBytes = _options.MaxDatabaseMegabytes * 1024L * 1024;
        while (await UsedBytesAsync(cancellationToken) > maxBytes)
        {
            var batch = await ExecAsync(
                """
                DELETE FROM problem_occurrences WHERE rowid IN (
                    SELECT rowid FROM problem_occurrences ORDER BY last_seen_utc
                    LIMIT MAX(50, (SELECT COUNT(*) / 10 FROM problem_occurrences)));
                """, cancellationToken);
            var problems = await DeleteOrphansAsync(cancellationToken);
            removed += batch;
            if (batch == 0 && problems == 0) break;
        }
        await ExecAsync("PRAGMA incremental_vacuum;", cancellationToken);
        await ExecAsync("PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);
        return removed;
    }

    private Task<int> DeleteOrphansAsync(CancellationToken cancellationToken) => ExecAsync(
        """
        DELETE FROM problems WHERE NOT EXISTS (
            SELECT 1 FROM problem_occurrences o WHERE o.fingerprint = problems.fingerprint);
        """, cancellationToken);

    /// <summary>The pages in use, without the free ones the next vacuum gives back.</summary>
    private async Task<long> UsedBytesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT (page_count - freelist_count) * page_size FROM pragma_page_count(), pragma_freelist_count(), pragma_page_size();";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task CreateBundleAsync(LogBundle bundle, CancellationToken cancellationToken) => await ExecAsync(
        """
        INSERT INTO log_bundles (bundle_id, agent_id, machine_name, requested_by, requested_at_utc, status, size_bytes)
        VALUES ($id, $agent, $machine, $by, $at, $status, 0);
        """,
        cancellationToken, ("$id", bundle.BundleId), ("$agent", bundle.AgentId), ("$machine", bundle.MachineName),
        ("$by", bundle.RequestedBy), ("$at", Format(bundle.RequestedAtUtc)), ("$status", bundle.Status));

    public async Task CompleteBundleAsync(string bundleId, string status, long size, string? message,
        DateTimeOffset now, CancellationToken cancellationToken) => await ExecAsync(
        """
        UPDATE log_bundles SET status = $status, size_bytes = $size, message = $message, completed_at_utc = $now
        WHERE bundle_id = $id;
        """,
        cancellationToken, ("$id", bundleId), ("$status", status), ("$size", size), ("$message", message),
        ("$now", Format(now)));

    public async Task<LogBundle?> GetBundleAsync(string bundleId, CancellationToken cancellationToken) =>
        (await ReadBundlesAsync("WHERE bundle_id = $id", bundleId, cancellationToken)).FirstOrDefault();

    public Task<IReadOnlyList<LogBundle>> ListBundlesAsync(CancellationToken cancellationToken) =>
        ReadBundlesAsync("", null, cancellationToken);

    public string BundlePath(string bundleId) => Path.Combine(BundleRoot, bundleId + ".zip");

    /// <summary>Removes bundles past their days, then the oldest ones over the total size limit.</summary>
    public async Task<int> DeleteOldBundlesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var bundles = await ListBundlesAsync(cancellationToken);
        var cutoff = now.AddDays(-_options.LogBundleRetentionDays);
        var maxBytes = _options.MaxLogBundlesMegabytes * 1024L * 1024;
        var total = bundles.Where(item => item.Status == LogBundleStatuses.Ready).Sum(item => item.SizeBytes);
        var removed = 0;
        // ListBundlesAsync is newest first; walk from the oldest.
        foreach (var bundle in bundles.Reverse())
        {
            var expired = bundle.RequestedAtUtc < cutoff;
            if (!expired && total <= maxBytes) continue;
            if (bundle.Status == LogBundleStatuses.Ready) total -= bundle.SizeBytes;
            TryDelete(BundlePath(bundle.BundleId));
            await ExecAsync("DELETE FROM log_bundles WHERE bundle_id = $id;", cancellationToken, ("$id", bundle.BundleId));
            removed++;
        }
        return removed;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private async Task<IReadOnlyList<LogBundle>> ReadBundlesAsync(string where, string? id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT bundle_id, agent_id, machine_name, requested_by, requested_at_utc, status, size_bytes,
                   completed_at_utc, message
            FROM log_bundles {where} ORDER BY requested_at_utc DESC;
            """;
        if (id is not null) command.Parameters.AddWithValue("$id", id);
        var rows = new List<LogBundle>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new LogBundle(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                Parse(reader.GetString(4)), reader.GetString(5), reader.GetInt64(6),
                reader.IsDBNull(7) ? null : Parse(reader.GetString(7)), Text(reader, 8)));
        return rows;
    }

    private const string ProblemSelect =
        """
        SELECT fingerprint, kind, source, title, exception_type, stack, status, fixed_in_version, fixed_by,
               fixed_at_utc, reopened_at_utc, conversation_id, first_seen_utc, last_seen_utc, total_count
        FROM problems
        """;

    private static StoredProblem ReadProblem(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Text(reader, 4),
        Text(reader, 5), reader.GetString(6), Text(reader, 7), Text(reader, 8),
        reader.IsDBNull(9) ? null : Parse(reader.GetString(9)),
        reader.IsDBNull(10) ? null : Parse(reader.GetString(10)), Text(reader, 11),
        Parse(reader.GetString(12)), Parse(reader.GetString(13)), reader.GetInt64(14));

    private static async Task<(string? Status, string? FixedIn)> ReadStatusAsync(SqliteConnection connection,
        string fingerprint, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, fixed_in_version FROM problems WHERE fingerprint = $fingerprint;";
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetString(0), Text(reader, 1)) : (null, null);
    }

    private static void AddFacts(SqliteCommand command, ProblemDeviceFacts facts)
    {
        command.Parameters.AddWithValue("$os", (object?)facts.OsName ?? DBNull.Value);
        command.Parameters.AddWithValue("$edition", (object?)facts.OsEdition ?? DBNull.Value);
        command.Parameters.AddWithValue("$osVersion", (object?)facts.OsVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$build", (object?)facts.OsBuild ?? DBNull.Value);
        command.Parameters.AddWithValue("$arch", (object?)facts.Architecture ?? DBNull.Value);
        command.Parameters.AddWithValue("$manufacturer", (object?)facts.Manufacturer ?? DBNull.Value);
        command.Parameters.AddWithValue("$model", (object?)facts.Model ?? DBNull.Value);
    }

    private async Task<int> ExecAsync(string sql, CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
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

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
