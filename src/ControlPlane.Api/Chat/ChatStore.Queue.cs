using Microsoft.Data.Sqlite;

namespace ControlPlane.Api.Chat;

// The Hermes request queue: waiting runs are agent_runs rows in state 'Queued'. Only
// runs that the worker may lease (active conversation, Hermes responder) count.
public sealed partial class ChatStore
{
    private const string WaitingRunsSql =
        """
        FROM agent_runs r
        JOIN conversations c ON c.conversation_id = r.conversation_id
        WHERE r.state = 'Queued' AND c.is_active = 1 AND c.responder_mode = 'Hermes'
        """;

    public const string QueueFullMessage =
        "در حال حاضر درخواست‌های زیادی در صف پشتیبانی است. لطفاً چند دقیقه دیگر دوباره بفرستید.";

    public const string AlreadyWaitingMessage =
        "پیام قبلی شما هنوز در صف است. لطفاً صبر کنید تا بررسی آن شروع شود.";

    public const string QueueExpiredMessage =
        "متأسفم، صف پشتیبانی شلوغ بود و نوبت به پیام شما نرسید. لطفاً دوباره بفرستید.";

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM control_plane_settings WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            return await command.ExecuteScalarAsync(cancellationToken) as string;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task SetSettingAsync(string key, string value, CancellationToken cancellationToken) =>
        ExecuteAsync(
            """
            INSERT INTO control_plane_settings(key, value, updated_at_utc)
            VALUES($key, $value, $now)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value,
                updated_at_utc = excluded.updated_at_utc;
            """,
            [("$key", key), ("$value", value), ("$now", Format(DateTimeOffset.UtcNow))],
            cancellationToken);

    public async Task<HermesQueueSnapshot> GetQueueSnapshotAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"""
                SELECT (SELECT COUNT(*) FROM agent_runs WHERE state = 'Running'),
                    (SELECT COUNT(*) {WaitingRunsSql}),
                    (SELECT MIN(r.created_at_utc) {WaitingRunsSql});
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            return new HermesQueueSnapshot(reader.GetInt32(0), reader.GetInt32(1),
                reader.IsDBNull(2) ? null : Parse(reader.GetString(2)));
        }
        finally
        {
            _gate.Release();
        }
    }

    // Answers every run that waited longer than the cutoff with a polite "too busy"
    // reply, so nobody waits forever when Hermes cannot keep up.
    public async Task<int> ExpireWaitingRunsAsync(DateTimeOffset waitingSince,
        CancellationToken cancellationToken)
    {
        var runIds = new List<string>();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT r.run_id {WaitingRunsSql} AND r.updated_at_utc < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", Format(waitingSince));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                runIds.Add(reader.GetString(0));
        }
        finally
        {
            _gate.Release();
        }

        var expired = 0;
        foreach (var runId in runIds)
        {
            if (await FinishRunAsync(runId, QueueExpiredMessage, succeeded: false,
                    cancellationToken, "Waited too long in the Hermes queue.",
                    expectedState: "Queued"))
                expired++;
        }
        return expired;
    }

    // Returns a refusal message when the queue is full or the user already waits.
    private static async Task<string?> CheckQueueLimitsAsync(SqliteConnection connection,
        SqliteTransaction transaction, string agentId, string userSid,
        ChatQueueLimits limits, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
            SELECT (SELECT COUNT(*) {WaitingRunsSql}),
                (SELECT COUNT(*) {WaitingRunsSql}
                    AND c.agent_id = $agent_id AND c.user_sid = $user_sid);
            """;
        command.Parameters.AddWithValue("$agent_id", agentId);
        command.Parameters.AddWithValue("$user_sid", userSid);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        if (reader.GetInt32(1) > 0) return AlreadyWaitingMessage;
        if (reader.GetInt32(0) >= limits.MaxQueueLength) return QueueFullMessage;
        return null;
    }

    // 1-based place of the conversation's oldest waiting run, or null when it waits for nothing.
    private static async Task<int?> GetQueuePositionAsync(SqliteConnection connection,
        string conversationId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            WITH mine AS (
                SELECT MIN(r.created_at_utc) AS created {WaitingRunsSql}
                    AND r.conversation_id = $conversation_id
            )
            SELECT COUNT(*) {WaitingRunsSql}
                AND r.created_at_utc <= (SELECT created FROM mine);
            """;
        command.Parameters.AddWithValue("$conversation_id", conversationId);
        var position = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        return position > 0 ? position : null;
    }
}

public sealed record ChatQueueLimits(int MaxQueueLength);

public sealed record HermesQueueSnapshot(int Running, int Waiting,
    DateTimeOffset? OldestWaitingSinceUtc);
