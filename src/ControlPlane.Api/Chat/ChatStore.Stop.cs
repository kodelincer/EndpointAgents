using EndpointAgents.Shared.Chat;
using Microsoft.Data.Sqlite;

namespace ControlPlane.Api.Chat;

public sealed partial class ChatStore
{
    public const string RunStoppedMessage = "درخواست شما متوقف شد.";
    public const string RunStoppedLastError = "Stopped by the user.";

    /// <summary>In-memory stop switches for the runs this process is working on.</summary>
    public ChatRunControl Runs { get; } = new();

    /// <summary>
    /// The user's Stop button. A queued request, a request not yet started and a proposal still
    /// waiting for approval are cancelled on the spot; a request the assistant is working on is
    /// stopped as soon as no repair is running. A repair that already started is never killed.
    /// </summary>
    public async Task<ChatRunStopReceipt> StopConversationRunAsync(string agentId,
        ChatRunStopRequest request, CancellationToken cancellationToken)
    {
        ValidateIdentity(request.Identity);
        var conversationId = Normalize(request.ConversationId, 100);
        var handles = new List<RunHandle>();
        var delayed = false;
        var stoppedNow = false;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);
            var conversation = await GetConversationAsync(connection, transaction,
                conversationId, cancellationToken);
            if (conversation is null || conversation.AgentId != agentId
                || conversation.UserSid != request.Identity.UserSid)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new ChatRunStopReceipt(false, ChatRunStopOutcome.NothingToStop,
                    "این گفتگو متعلق به کاربر فعلی نیست.");
            }

            var runs = new List<(string RunId, string MessageId, bool Running)>();
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText =
                    """
                    SELECT run_id, user_message_id, state FROM agent_runs
                    WHERE conversation_id = $conversation_id AND state IN ('Queued', 'Running')
                    ORDER BY created_at_utc;
                    """;
                select.Parameters.AddWithValue("$conversation_id", conversationId);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    runs.Add((reader.GetString(0), reader.GetString(1),
                        reader.GetString(2) == "Running"));
            }

            var now = DateTimeOffset.UtcNow;
            long executingActions;
            await using (var count = connection.CreateCommand())
            {
                count.Transaction = transaction;
                count.CommandText =
                    """
                    SELECT COUNT(*) FROM action_proposals
                    WHERE conversation_id = $conversation_id AND state = 'Approved';
                    """;
                count.Parameters.AddWithValue("$conversation_id", conversationId);
                executingActions = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
            }

            int cancelledProposals;
            await using (var proposals = connection.CreateCommand())
            {
                proposals.Transaction = transaction;
                proposals.CommandText =
                    """
                    UPDATE action_proposals SET state = 'Cancelled', decided_at_utc = $now
                    WHERE conversation_id = $conversation_id AND state = 'Pending';
                    """;
                proposals.Parameters.AddWithValue("$conversation_id", conversationId);
                proposals.Parameters.AddWithValue("$now", Format(now));
                cancelledProposals = await proposals.ExecuteNonQueryAsync(cancellationToken);
            }

            var cancelledHere = 0;
            foreach (var run in runs)
            {
                if (run.Running && Runs.Get(run.RunId) is { } handle)
                {
                    handles.Add(handle);
                    continue;
                }
                // Queued behind other requests, or running without an owner in this process.
                await CancelRunRowsAsync(connection, transaction, run.RunId, run.MessageId,
                    now, cancellationToken);
                cancelledHere++;
            }

            // The run that is still working writes the reply; otherwise this call does.
            if (handles.Count == 0 && (cancelledHere > 0 || cancelledProposals > 0))
                await AppendStoppedMessageAsync(connection, transaction, conversationId, now,
                    cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            stoppedNow = cancelledHere > 0 || cancelledProposals > 0;
            if (runs.Count == 0 && cancelledProposals == 0 && executingActions == 0)
                return new ChatRunStopReceipt(true, ChatRunStopOutcome.NothingToStop,
                    "درخواستی برای توقف وجود ندارد.");
            delayed = executingActions > 0;
        }
        finally
        {
            _gate.Release();
        }

        // Signalled outside the gate: the stopped run finishes through the same gate.
        foreach (var handle in handles)
            if (!handle.RequestStop()) delayed = true;
            else stoppedNow = true;

        return delayed
            ? new ChatRunStopReceipt(true, ChatRunStopOutcome.FinishingOperation,
                "کاری که روی رایانه شروع شده بدون خطر قابل توقف نیست. بعد از پایان آن، درخواست متوقف می‌شود.")
            : new ChatRunStopReceipt(stoppedNow, ChatRunStopOutcome.Stopped, RunStoppedMessage);
    }

    /// <summary>Ends a run the user stopped, once the assistant and any repair have let go of it.</summary>
    public async Task FinishCancelledRunAsync(string runId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);
            string conversationId, messageId;
            await using (var lookup = connection.CreateCommand())
            {
                lookup.Transaction = transaction;
                lookup.CommandText =
                    """
                    SELECT conversation_id, user_message_id FROM agent_runs
                    WHERE run_id = $run_id AND state = 'Running';
                    """;
                lookup.Parameters.AddWithValue("$run_id", runId);
                await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return;
                }
                conversationId = reader.GetString(0);
                messageId = reader.GetString(1);
            }

            var now = DateTimeOffset.UtcNow;
            await CancelRunRowsAsync(connection, transaction, runId, messageId, now,
                cancellationToken);
            await AppendStoppedMessageAsync(connection, transaction, conversationId, now,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task CancelRunRowsAsync(SqliteConnection connection,
        SqliteTransaction transaction, string runId, string messageId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await AppendProgressAsync(connection, transaction, runId, ChatProgressStage.Cancelled,
            ChatProgressActivity.None, ChatProgressOutcome.None, now, cancellationToken);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE agent_runs SET state = 'Cancelled', capability_hash = NULL,
                capability_expires_at_utc = NULL, framework_run_id = NULL,
                updated_at_utc = $now, last_error = $error
            WHERE run_id = $run_id;
            UPDATE messages SET state = 'Cancelled' WHERE message_id = $message_id;
            """;
        update.Parameters.AddWithValue("$now", Format(now));
        update.Parameters.AddWithValue("$error", RunStoppedLastError);
        update.Parameters.AddWithValue("$run_id", runId);
        update.Parameters.AddWithValue("$message_id", messageId);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AppendStoppedMessageAsync(SqliteConnection connection,
        SqliteTransaction transaction, string conversationId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE conversations SET last_sequence = last_sequence + 1, updated_at_utc = $now
            WHERE conversation_id = $conversation_id;
            INSERT INTO messages(message_id, conversation_id, sequence, role, body,
                text_direction, state, created_at_utc, run_id)
            SELECT $message_id, conversation_id, last_sequence, 'Assistant', $body,
                'rtl', 'Completed', $now, NULL
            FROM conversations WHERE conversation_id = $conversation_id;
            """;
        command.Parameters.AddWithValue("$now", Format(now));
        command.Parameters.AddWithValue("$conversation_id", conversationId);
        command.Parameters.AddWithValue("$message_id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$body", RunStoppedMessage);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
