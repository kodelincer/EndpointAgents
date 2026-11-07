using ControlPlane.Api.Actions;
using ControlPlane.Api.Chat;
using EndpointAgents.Shared.Actions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ControlPlane.Api.Lessons;

public static class LessonStatuses
{
    public const string New = "New";
    /// <summary>Read and kept for later analysis; nothing changes in the agent.</summary>
    public const string Kept = "Kept";
    /// <summary>An IT member started a skill draft from it; the draft is reviewed and published on the Skills page.</summary>
    public const string Drafted = "Drafted";
    public const string Dropped = "Dropped";
    public static IReadOnlyList<string> All { get; } = [New, Kept, Drafted, Dropped];
}

public sealed record LessonStep(string ActionId, string Name, string Status, string Summary);

/// <summary>What one solved chat teaches: the symptom, what was tried and what fixed it. Written without AI.</summary>
public sealed record Lesson(string LessonId, string ConversationId, string AgentId, string MachineName,
    string Symptom, IReadOnlyList<LessonStep> Tried, string? FixedBy, IReadOnlyList<string> Skills,
    DateTimeOffset ResolvedAtUtc, string Status, string Note, string? DecidedBy, DateTimeOffset? DecidedAtUtc);

public sealed record LessonPatch(string? Status, string? Note);

public sealed record LessonFixStat(string FixedBy, int Solved);

/// <summary>
/// The Lessons queue (supervised learning): every solved chat leaves a lesson, people decide
/// what happens with it, and only a skill draft a reviewer publishes ever reaches Hermes.
/// </summary>
public sealed class LessonStore(IOptions<ChatOptions> chatOptions, ActionStore actions, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _databasePath = Path.GetFullPath(chatOptions.Value.DatabasePath);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await using var connection = await OpenAsync(cancellationToken);
        await using var create = connection.CreateCommand();
        create.CommandText =
            """
            CREATE TABLE IF NOT EXISTS lessons (
                lesson_id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL UNIQUE,
                agent_id TEXT NOT NULL,
                machine_name TEXT NOT NULL,
                symptom TEXT NOT NULL,
                tried_json TEXT NOT NULL,
                fixed_by TEXT NULL,
                skills_json TEXT NOT NULL,
                resolved_at_utc TEXT NOT NULL,
                status TEXT NOT NULL,
                note TEXT NOT NULL,
                decided_by TEXT NULL,
                decided_at_utc TEXT NULL
            );
            """;
        await create.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Writes a lesson for every solved chat that has none yet; returns how many were written.</summary>
    public async Task<int> CollectAsync(CancellationToken cancellationToken)
    {
        var pending = new List<(string Conversation, string Agent, string Machine, string Resolved)>();
        await using (var connection = await OpenAsync(cancellationToken))
        await using (var select = connection.CreateCommand())
        {
            select.CommandText =
                """
                SELECT c.conversation_id, c.agent_id, COALESCE(d.machine_name, ''), c.resolved_at_utc
                FROM conversations c
                LEFT JOIN lessons l ON l.conversation_id = c.conversation_id
                LEFT JOIN devices d ON d.agent_id = c.agent_id
                WHERE c.helpdesk_status = 'Resolved' AND c.resolved_at_utc IS NOT NULL AND l.lesson_id IS NULL
                ORDER BY c.resolved_at_utc LIMIT 50;
                """;
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                pending.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }
        foreach (var item in pending)
        {
            var messages = await actions.GetConversationMessagesAsync(item.Conversation, 40, cancellationToken);
            var runs = await actions.GetRecentRunsAsync(null, item.Conversation, 50, cancellationToken);
            var tried = runs.OrderBy(run => run.StartedAtUtc)
                .Select(run => (Run: run, Definition: EndpointActionCatalog.Find(run.ActionId)))
                .Where(pair => pair.Definition is null || pair.Definition.Tier != ActionTier.OperatorOnly)
                .Select(pair => new LessonStep(pair.Run.ActionId, pair.Definition?.Name ?? pair.Run.ActionId,
                    pair.Run.Status, Shorten(pair.Run.Summary, 160)))
                .ToArray();
            var fixedBy = tried.LastOrDefault(step => step.Status == nameof(ActionStatus.Done)
                && EndpointActionCatalog.Find(step.ActionId)?.Tier is not ActionTier.Look)?.ActionId;
            var symptom = Shorten(messages.FirstOrDefault(message => message.Role == "User").Body ?? "", 300);
            await SaveAsync(item.Conversation, item.Agent, item.Machine, symptom, tried, fixedBy,
                await SkillsUsedAsync(item.Conversation, cancellationToken), item.Resolved, cancellationToken);
        }
        return pending.Count;
    }

    public async Task<IReadOnlyList<Lesson>> ListAsync(string? status, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var select = connection.CreateCommand();
        select.CommandText = status is null
            ? "SELECT * FROM lessons ORDER BY resolved_at_utc DESC LIMIT $limit;"
            : "SELECT * FROM lessons WHERE status = $status ORDER BY resolved_at_utc DESC LIMIT $limit;";
        select.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        if (status is not null) select.Parameters.AddWithValue("$status", status);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        var lessons = new List<Lesson>();
        while (await reader.ReadAsync(cancellationToken))
            lessons.Add(new Lesson(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), JsonSerializer.Deserialize<LessonStep[]>(reader.GetString(5), Json) ?? [],
                reader.IsDBNull(6) ? null : reader.GetString(6),
                JsonSerializer.Deserialize<string[]>(reader.GetString(7), Json) ?? [], Parse(reader.GetString(8)),
                reader.GetString(9), reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : Parse(reader.GetString(12))));
        return lessons;
    }

    public async Task<bool> PatchAsync(string lessonId, LessonPatch patch, string decidedBy, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var update = connection.CreateCommand();
        update.CommandText =
            """
            UPDATE lessons SET status = COALESCE($status, status), note = COALESCE($note, note),
                decided_by = $by, decided_at_utc = $at WHERE lesson_id = $id;
            """;
        update.Parameters.AddWithValue("$status", (object?)patch.Status ?? DBNull.Value);
        update.Parameters.AddWithValue("$note", patch.Note is null ? DBNull.Value : Shorten(patch.Note, 1000));
        update.Parameters.AddWithValue("$by", decidedBy);
        update.Parameters.AddWithValue("$at", Format(clock.GetUtcNow()));
        update.Parameters.AddWithValue("$id", lessonId);
        return await update.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>Which fixes solved most chats, for the analysis at the top of the page.</summary>
    public async Task<IReadOnlyList<LessonFixStat>> FixStatsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var select = connection.CreateCommand();
        select.CommandText =
            """
            SELECT COALESCE(fixed_by, ''), COUNT(*) FROM lessons WHERE status <> 'Dropped'
            GROUP BY COALESCE(fixed_by, '') ORDER BY COUNT(*) DESC LIMIT 15;
            """;
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        var stats = new List<LessonFixStat>();
        while (await reader.ReadAsync(cancellationToken))
            stats.Add(new LessonFixStat(reader.GetString(0), reader.GetInt32(1)));
        return stats;
    }

    private async Task SaveAsync(string conversationId, string agentId, string machine, string symptom,
        IReadOnlyList<LessonStep> tried, string? fixedBy, IReadOnlyList<string> skills, string resolvedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT OR IGNORE INTO lessons VALUES ($id, $conversation, $agent, $machine, $symptom, $tried,
                $fixed, $skills, $resolved, 'New', '', NULL, NULL);
            """;
        insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        insert.Parameters.AddWithValue("$conversation", conversationId);
        insert.Parameters.AddWithValue("$agent", agentId);
        insert.Parameters.AddWithValue("$machine", machine);
        insert.Parameters.AddWithValue("$symptom", symptom);
        insert.Parameters.AddWithValue("$tried", JsonSerializer.Serialize(tried, Json));
        insert.Parameters.AddWithValue("$fixed", (object?)fixedBy ?? DBNull.Value);
        insert.Parameters.AddWithValue("$skills", JsonSerializer.Serialize(skills, Json));
        insert.Parameters.AddWithValue("$resolved", resolvedAt);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<string>> SkillsUsedAsync(string conversationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT DISTINCT skill_name FROM skill_usage WHERE conversation_id = $id;";
        select.Parameters.AddWithValue("$id", conversationId);
        var skills = new List<string>();
        try
        {
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) skills.Add(reader.GetString(0));
        }
        catch (SqliteException) { }
        return skills;
    }

    private static string Shorten(string value, int max)
    {
        var flat = Regex.Replace(value ?? "", @"\s+", " ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
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

/// <summary>Writes lessons for newly solved chats every five minutes. No AI.</summary>
public sealed class LessonWorker(LessonStore lessons, ILogger<LessonWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try { await lessons.CollectAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogWarning(ex, "Lessons could not be collected."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
