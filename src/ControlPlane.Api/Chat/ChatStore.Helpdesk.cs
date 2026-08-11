using EndpointAgents.Shared.Chat;
using EndpointAgents.Shared.Rpc;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace ControlPlane.Api.Chat;

public sealed partial class ChatStore
{
    public async Task<IReadOnlyList<HelpdeskConversationSummary>> GetHelpdeskConversationsAsync(
        int limit, string? status, string? query, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 200);
        var normalizedStatus = HelpdeskStatus.IsValid(status) ? status : null;
        var normalizedQuery = Normalize(query, 200);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT c.conversation_id, c.agent_id, c.user_name, c.session_id,
                    c.created_at_utc, c.updated_at_utc, c.last_sequence,
                    c.helpdesk_status, c.responder_mode,
                    (SELECT COUNT(*) FROM messages unread
                     WHERE unread.conversation_id = c.conversation_id
                       AND unread.role = 'User'
                       AND unread.sequence > c.operator_read_sequence),
                    COALESCE((SELECT body FROM messages latest
                              WHERE latest.conversation_id = c.conversation_id AND latest.body <> ''
                              ORDER BY latest.sequence DESC LIMIT 1), ''),
                    COALESCE((SELECT text_direction FROM messages latest
                              WHERE latest.conversation_id = c.conversation_id AND latest.body <> ''
                              ORDER BY latest.sequence DESC LIMIT 1), 'auto'),
                    COALESCE(d.machine_name, '')
                FROM conversations c
                LEFT JOIN devices d ON d.agent_id = c.agent_id
                WHERE c.is_active = 1
                  AND EXISTS (SELECT 1 FROM messages first_message
                              WHERE first_message.conversation_id = c.conversation_id
                                AND first_message.role = 'User' AND first_message.body <> '')
                  AND ($status IS NULL OR c.helpdesk_status = $status)
                  AND ($query = '' OR c.agent_id LIKE $like OR c.user_name LIKE $like
                       OR EXISTS (SELECT 1 FROM messages searched
                                  WHERE searched.conversation_id = c.conversation_id
                                    AND searched.body LIKE $like))
                ORDER BY CASE c.helpdesk_status WHEN 'New' THEN 0 WHEN 'Open' THEN 1 ELSE 2 END,
                    c.updated_at_utc DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$status", normalizedStatus is null
                ? DBNull.Value : normalizedStatus);
            command.Parameters.AddWithValue("$query", normalizedQuery);
            command.Parameters.AddWithValue("$like", $"%{normalizedQuery}%");
            command.Parameters.AddWithValue("$limit", limit);
            var result = new List<HelpdeskConversationSummary>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) result.Add(ReadHelpdeskSummary(reader));
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<HelpdeskConversationDetail?> GetHelpdeskConversationAsync(
        string conversationId, long afterSequence, CancellationToken cancellationToken)
    {
        conversationId = Normalize(conversationId, 100);
        afterSequence = Math.Max(0, afterSequence);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var summary = await ReadHelpdeskSummaryAsync(connection, conversationId, cancellationToken);
            if (summary is null) return null;

            var messages = new List<ChatMessage>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT message_id, conversation_id, sequence, role, body, text_direction,
                        state, created_at_utc, run_id
                    FROM messages WHERE conversation_id = $id AND sequence > $after
                    ORDER BY sequence;
                    """;
                command.Parameters.AddWithValue("$id", conversationId);
                command.Parameters.AddWithValue("$after", afterSequence);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) messages.Add(ReadMessage(reader));
            }

            var actions = new List<ChatActionProposal>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT action_id, conversation_id, sequence, method, display_name,
                        description, parameters_json, created_at_utc, expires_at_utc, state
                    FROM action_proposals WHERE conversation_id = $id AND sequence > $after
                    ORDER BY sequence;
                    """;
                command.Parameters.AddWithValue("$id", conversationId);
                command.Parameters.AddWithValue("$after", afterSequence);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) actions.Add(ReadAction(reader));
            }

            var progress = new List<ChatRunProgressEvent>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT event_id, conversation_id, run_id, sequence, stage, activity,
                        outcome, created_at_utc
                    FROM chat_progress_events WHERE conversation_id = $id AND sequence > $after
                    ORDER BY sequence;
                    """;
                command.Parameters.AddWithValue("$id", conversationId);
                command.Parameters.AddWithValue("$after", afterSequence);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) progress.Add(ReadProgressEvent(reader));
            }

            var trace = new List<HelpdeskTraceEvent>();
            trace.AddRange(messages.Select(message => new HelpdeskTraceEvent(message.MessageId,
                conversationId, message.Sequence, message.CreatedAtUtc,
                message.Role == ChatMessageRole.User ? "AgentToControlPlane" : "ControlPlaneToAgent",
                $"chat.message.{message.Role.ToString().ToLowerInvariant()}", message.State.ToString(),
                JsonSerializer.SerializeToElement(message))));
            trace.AddRange(actions.Select(action => new HelpdeskTraceEvent(action.ActionId,
                conversationId, action.Sequence, action.CreatedAtUtc, "ControlPlaneToAgent",
                "chat.action", action.State.ToString(), JsonSerializer.SerializeToElement(action))));
            trace.AddRange(progress.Select(item => new HelpdeskTraceEvent(item.EventId,
                conversationId, item.Sequence, item.CreatedAtUtc, "ControlPlaneToAgent",
                "chat.progress", item.Outcome.ToString(), JsonSerializer.SerializeToElement(item))));

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT event_id, conversation_id, sequence, created_at_utc, direction,
                        event_type, outcome, payload_json
                    FROM helpdesk_trace_events
                    WHERE conversation_id = $id AND (sequence IS NULL OR sequence > $after)
                    ORDER BY created_at_utc;
                    """;
                command.Parameters.AddWithValue("$id", conversationId);
                command.Parameters.AddWithValue("$after", afterSequence);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    trace.Add(new HelpdeskTraceEvent(reader.GetString(0), reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetInt64(2), Parse(reader.GetString(3)),
                        reader.GetString(4), reader.GetString(5), reader.GetString(6),
                        JsonSerializer.Deserialize<JsonElement>(reader.GetString(7))));
            }

            return new HelpdeskConversationDetail(summary, messages, actions, progress,
                trace.OrderBy(item => item.TimestampUtc).ThenBy(item => item.Sequence).ToArray());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ChatMessageReceipt> SubmitOperatorMessageAsync(string conversationId,
        HelpdeskOperatorMessageRequest request, CancellationToken cancellationToken)
    {
        conversationId = Normalize(conversationId, 100);
        var body = Normalize(request.MessageText, ChatLimits.MessageCharacters + 1);
        var clientId = Normalize(request.ClientMessageId, 100);
        if (string.IsNullOrWhiteSpace(body) || string.IsNullOrWhiteSpace(clientId)
            || body.Length > ChatLimits.MessageCharacters)
            return new ChatMessageReceipt(conversationId, string.Empty, 0, false,
                $"Message must contain 1-{ChatLimits.MessageCharacters} characters.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var lookup = connection.CreateCommand();
            lookup.Transaction = (SqliteTransaction)transaction;
            lookup.CommandText =
                "SELECT last_sequence, responder_mode FROM conversations WHERE conversation_id = $id AND is_active = 1;";
            lookup.Parameters.AddWithValue("$id", conversationId);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return new ChatMessageReceipt(conversationId, string.Empty, 0, false,
                    "Conversation was not found.");
            var lastSequence = reader.GetInt64(0);
            var responder = reader.GetString(1);
            await reader.DisposeAsync();
            if (responder != HelpdeskResponder.Operator)
                return new ChatMessageReceipt(conversationId, string.Empty, 0, false,
                    "Take over this conversation before sending an operator reply.");

            await using var duplicate = connection.CreateCommand();
            duplicate.Transaction = (SqliteTransaction)transaction;
            duplicate.CommandText =
                "SELECT message_id, sequence FROM messages WHERE conversation_id = $id AND client_message_id = $client;";
            duplicate.Parameters.AddWithValue("$id", conversationId);
            duplicate.Parameters.AddWithValue("$client", clientId);
            await using var duplicateReader = await duplicate.ExecuteReaderAsync(cancellationToken);
            if (await duplicateReader.ReadAsync(cancellationToken))
                return new ChatMessageReceipt(conversationId, duplicateReader.GetString(0),
                    duplicateReader.GetInt64(1), true, "Reply was already received.");
            await duplicateReader.DisposeAsync();

            var message = new ChatMessage(Guid.NewGuid().ToString("N"), conversationId,
                lastSequence + 1, ChatMessageRole.Operator, body,
                request.TextDirection is "rtl" or "ltr" ? request.TextDirection : "auto",
                ChatMessageState.Completed, DateTimeOffset.UtcNow);
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText =
                """
                INSERT INTO messages(message_id, conversation_id, client_message_id, sequence,
                    role, body, text_direction, state, created_at_utc, run_id)
                VALUES($message_id, $conversation_id, $client_id, $sequence, 'Operator',
                    $body, $direction, 'Completed', $now, NULL);
                UPDATE conversations SET last_sequence = $sequence, updated_at_utc = $now,
                    helpdesk_status = 'Open', resolved_at_utc = NULL
                WHERE conversation_id = $conversation_id;
                """;
            insert.Parameters.AddWithValue("$message_id", message.MessageId);
            insert.Parameters.AddWithValue("$conversation_id", conversationId);
            insert.Parameters.AddWithValue("$client_id", clientId);
            insert.Parameters.AddWithValue("$sequence", message.Sequence);
            insert.Parameters.AddWithValue("$body", message.Body);
            insert.Parameters.AddWithValue("$direction", message.TextDirection);
            insert.Parameters.AddWithValue("$now", Format(message.CreatedAtUtc));
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ChatMessageReceipt(conversationId, message.MessageId, message.Sequence,
                true, "Reply sent to the endpoint user.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> PatchHelpdeskConversationAsync(string conversationId,
        HelpdeskConversationPatch patch, CancellationToken cancellationToken)
    {
        conversationId = Normalize(conversationId, 100);
        if (patch.Status is not null && !HelpdeskStatus.IsValid(patch.Status)) return false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var lookup = connection.CreateCommand();
            lookup.Transaction = (SqliteTransaction)transaction;
            lookup.CommandText =
                "SELECT last_sequence, helpdesk_status, responder_mode FROM conversations WHERE conversation_id = $id AND is_active = 1;";
            lookup.Parameters.AddWithValue("$id", conversationId);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return false;
            var lastSequence = reader.GetInt64(0);
            var currentStatus = reader.GetString(1);
            var currentResponder = reader.GetString(2);
            await reader.DisposeAsync();

            var newStatus = patch.Status ?? (patch.OperatorTakeover == true
                && currentStatus == HelpdeskStatus.New ? HelpdeskStatus.Open : currentStatus);
            var newResponder = patch.OperatorTakeover is null
                ? currentResponder
                : patch.OperatorTakeover.Value ? HelpdeskResponder.Operator : HelpdeskResponder.Hermes;
            if (newStatus == HelpdeskStatus.Resolved) newResponder = HelpdeskResponder.Hermes;
            var responderChanged = newResponder != currentResponder;
            var now = DateTimeOffset.UtcNow;

            if (newResponder == HelpdeskResponder.Operator && responderChanged)
            {
                await using var cancel = connection.CreateCommand();
                cancel.Transaction = (SqliteTransaction)transaction;
                cancel.CommandText =
                    """
                    UPDATE messages SET state = 'Completed'
                    WHERE message_id IN (SELECT user_message_id FROM agent_runs
                        WHERE conversation_id = $id AND state IN ('Queued', 'Running'));
                    UPDATE agent_runs SET state = 'Cancelled', capability_hash = NULL,
                        capability_expires_at_utc = NULL, framework_run_id = NULL,
                        updated_at_utc = $now, last_error = 'Cancelled by operator takeover.'
                    WHERE conversation_id = $id AND state IN ('Queued', 'Running');
                    UPDATE action_proposals SET state = 'Expired', decided_at_utc = $now
                    WHERE conversation_id = $id AND state IN ('Pending', 'Approved');
                    """;
                cancel.Parameters.AddWithValue("$id", conversationId);
                cancel.Parameters.AddWithValue("$now", Format(now));
                await cancel.ExecuteNonQueryAsync(cancellationToken);
            }

            if (responderChanged)
            {
                lastSequence++;
                var text = newResponder == HelpdeskResponder.Operator
                    ? "کارشناس پشتیبانی به گفتگو پیوست."
                    : "پاسخ‌گویی خودکار دوباره فعال شد.";
                await using var system = connection.CreateCommand();
                system.Transaction = (SqliteTransaction)transaction;
                system.CommandText =
                    """
                    INSERT INTO messages(message_id, conversation_id, sequence, role, body,
                        text_direction, state, created_at_utc, run_id)
                    VALUES($message_id, $id, $sequence, 'System', $body, 'rtl', 'Completed', $now, NULL);
                    """;
                system.Parameters.AddWithValue("$message_id", Guid.NewGuid().ToString("N"));
                system.Parameters.AddWithValue("$id", conversationId);
                system.Parameters.AddWithValue("$sequence", lastSequence);
                system.Parameters.AddWithValue("$body", text);
                system.Parameters.AddWithValue("$now", Format(now));
                await system.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var update = connection.CreateCommand();
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText =
                """
                UPDATE conversations SET helpdesk_status = $status,
                    responder_mode = $responder,
                    operator_read_sequence = CASE WHEN $read IS NULL THEN operator_read_sequence
                        ELSE MIN(last_sequence, MAX(0, $read)) END,
                    resolved_at_utc = CASE WHEN $status = 'Resolved' THEN COALESCE(resolved_at_utc, $now) ELSE NULL END,
                    last_sequence = $last_sequence,
                    updated_at_utc = CASE WHEN $changed = 1 THEN $now ELSE updated_at_utc END
                WHERE conversation_id = $id;
                """;
            update.Parameters.AddWithValue("$status", newStatus);
            update.Parameters.AddWithValue("$responder", newResponder);
            update.Parameters.AddWithValue("$read", patch.ReadThroughSequence is null
                ? DBNull.Value : patch.ReadThroughSequence.Value);
            update.Parameters.AddWithValue("$now", Format(now));
            update.Parameters.AddWithValue("$last_sequence", lastSequence);
            update.Parameters.AddWithValue("$changed", responderChanged || newStatus != currentStatus ? 1 : 0);
            update.Parameters.AddWithValue("$id", conversationId);
            await update.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendDiagnosticTraceAsync(string conversationId, string agentId,
        RpcRequest request, RpcResponse response, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var validate = connection.CreateCommand();
            validate.Transaction = (SqliteTransaction)transaction;
            validate.CommandText =
                "SELECT EXISTS(SELECT 1 FROM conversations WHERE conversation_id = $id AND agent_id = $agent AND is_active = 1);";
            validate.Parameters.AddWithValue("$id", conversationId);
            validate.Parameters.AddWithValue("$agent", agentId);
            if (Convert.ToInt64(await validate.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture) == 0) return;

            var now = DateTimeOffset.UtcNow;
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText =
                """
                INSERT INTO helpdesk_trace_events(event_id, conversation_id, sequence, direction,
                    event_type, outcome, payload_json, created_at_utc)
                VALUES($request_event, $id, NULL, 'ControlPlaneToAgent', 'rpc.request', 'Pending',
                    $request, $now);
                INSERT INTO helpdesk_trace_events(event_id, conversation_id, sequence, direction,
                    event_type, outcome, payload_json, created_at_utc)
                VALUES($response_event, $id, NULL, 'AgentToControlPlane', 'rpc.response', $outcome,
                    $response, $now);
                UPDATE conversations SET updated_at_utc = $now WHERE conversation_id = $id;
                """;
            insert.Parameters.AddWithValue("$request_event", Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("$response_event", Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("$id", conversationId);
            insert.Parameters.AddWithValue("$request", JsonSerializer.Serialize(request));
            insert.Parameters.AddWithValue("$response", JsonSerializer.Serialize(response));
            insert.Parameters.AddWithValue("$outcome", response.Error is null ? "Success" : "Error");
            insert.Parameters.AddWithValue("$now", Format(now));
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<HelpdeskConversationSummary?> ReadHelpdeskSummaryAsync(
        SqliteConnection connection, string conversationId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT c.conversation_id, c.agent_id, c.user_name, c.session_id,
                c.created_at_utc, c.updated_at_utc, c.last_sequence,
                c.helpdesk_status, c.responder_mode,
                (SELECT COUNT(*) FROM messages unread
                 WHERE unread.conversation_id = c.conversation_id AND unread.role = 'User'
                   AND unread.sequence > c.operator_read_sequence),
                COALESCE((SELECT body FROM messages latest
                          WHERE latest.conversation_id = c.conversation_id AND latest.body <> ''
                          ORDER BY latest.sequence DESC LIMIT 1), ''),
                COALESCE((SELECT text_direction FROM messages latest
                          WHERE latest.conversation_id = c.conversation_id AND latest.body <> ''
                          ORDER BY latest.sequence DESC LIMIT 1), 'auto'),
                COALESCE(d.machine_name, '')
            FROM conversations c LEFT JOIN devices d ON d.agent_id = c.agent_id
            WHERE c.conversation_id = $id AND c.is_active = 1;
            """;
        command.Parameters.AddWithValue("$id", conversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadHelpdeskSummary(reader) : null;
    }

    private static HelpdeskConversationSummary ReadHelpdeskSummary(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
            Parse(reader.GetString(4)), Parse(reader.GetString(5)), reader.GetInt64(6),
            reader.GetString(7), reader.GetString(8), reader.GetInt32(9), reader.GetString(10),
            reader.GetString(11), reader.GetString(12));
}
