using EndpointAgents.Shared.Chat;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ControlPlane.Api.Chat;

public sealed partial class ChatStore(IOptions<ChatOptions> options)
{
    private readonly string _databasePath = Path.GetFullPath(options.Value.DatabasePath);
    private readonly int _maxVisibleItems = options.Value.MaxVisibleItems;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_databasePath)
            ?? throw new InvalidOperationException("Chat database path has no parent directory.");
        Directory.CreateDirectory(directory);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                PRAGMA journal_mode=WAL;
                PRAGMA foreign_keys=ON;
                PRAGMA busy_timeout=5000;

                CREATE TABLE IF NOT EXISTS devices (
                    agent_id TEXT PRIMARY KEY,
                    token_hash TEXT NOT NULL,
                    machine_name TEXT NOT NULL,
                    enrolled_at_utc TEXT NOT NULL,
                    last_seen_at_utc TEXT NOT NULL,
                    revoked_at_utc TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS enrollment_codes (
                    code_hash TEXT PRIMARY KEY,
                    expires_at_utc TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    redeemed_at_utc TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS conversations (
                    conversation_id TEXT PRIMARY KEY,
                    agent_id TEXT NOT NULL,
                    user_sid TEXT NOT NULL,
                    user_name TEXT NOT NULL,
                    session_id INTEGER NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    is_active INTEGER NOT NULL DEFAULT 1,
                    last_sequence INTEGER NOT NULL DEFAULT 0,
                    helpdesk_status TEXT NOT NULL DEFAULT 'New',
                    responder_mode TEXT NOT NULL DEFAULT 'Hermes',
                    operator_read_sequence INTEGER NOT NULL DEFAULT 0,
                    resolved_at_utc TEXT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS ux_conversations_active_owner
                    ON conversations(agent_id, user_sid) WHERE is_active = 1;

                CREATE TABLE IF NOT EXISTS messages (
                    message_id TEXT PRIMARY KEY,
                    conversation_id TEXT NOT NULL REFERENCES conversations(conversation_id),
                    client_message_id TEXT NULL,
                    sequence INTEGER NOT NULL,
                    role TEXT NOT NULL,
                    body TEXT NOT NULL,
                    text_direction TEXT NOT NULL,
                    state TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    run_id TEXT NULL,
                    UNIQUE(conversation_id, sequence),
                    UNIQUE(conversation_id, client_message_id)
                );

                CREATE TABLE IF NOT EXISTS agent_runs (
                    run_id TEXT PRIMARY KEY,
                    conversation_id TEXT NOT NULL REFERENCES conversations(conversation_id),
                    user_message_id TEXT NOT NULL REFERENCES messages(message_id),
                    state TEXT NOT NULL,
                    capability_hash TEXT NULL,
                    capability_expires_at_utc TEXT NULL,
                    framework_run_id TEXT NULL,
                    attempts INTEGER NOT NULL DEFAULT 0,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    last_error TEXT NULL,
                    UNIQUE(user_message_id)
                );
                CREATE INDEX IF NOT EXISTS ix_agent_runs_state
                    ON agent_runs(state, created_at_utc);

                CREATE TABLE IF NOT EXISTS chat_progress_events (
                    event_id TEXT PRIMARY KEY,
                    conversation_id TEXT NOT NULL REFERENCES conversations(conversation_id),
                    run_id TEXT NOT NULL REFERENCES agent_runs(run_id),
                    sequence INTEGER NOT NULL,
                    stage TEXT NOT NULL,
                    activity TEXT NOT NULL,
                    outcome TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    UNIQUE(conversation_id, sequence)
                );
                CREATE INDEX IF NOT EXISTS ix_chat_progress_events_run
                    ON chat_progress_events(run_id, sequence);

                CREATE TABLE IF NOT EXISTS action_proposals (
                    action_id TEXT PRIMARY KEY,
                    conversation_id TEXT NOT NULL REFERENCES conversations(conversation_id),
                    run_id TEXT NOT NULL REFERENCES agent_runs(run_id),
                    sequence INTEGER NOT NULL,
                    method TEXT NOT NULL,
                    display_name TEXT NOT NULL,
                    description TEXT NOT NULL,
                    parameters_json TEXT NULL,
                    state TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    expires_at_utc TEXT NOT NULL,
                    decided_at_utc TEXT NULL,
                    executed_at_utc TEXT NULL,
                    execution_error TEXT NULL,
                    UNIQUE(conversation_id, sequence)
                );

                CREATE TABLE IF NOT EXISTS tool_audit (
                    event_id TEXT PRIMARY KEY,
                    run_id TEXT NOT NULL,
                    conversation_id TEXT NOT NULL,
                    agent_id TEXT NOT NULL,
                    tool_name TEXT NOT NULL,
                    outcome TEXT NOT NULL,
                    detail TEXT NULL,
                    created_at_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS helpdesk_trace_events (
                    event_id TEXT PRIMARY KEY,
                    conversation_id TEXT NOT NULL REFERENCES conversations(conversation_id),
                    sequence INTEGER NULL,
                    direction TEXT NOT NULL,
                    event_type TEXT NOT NULL,
                    outcome TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_helpdesk_trace_conversation
                    ON helpdesk_trace_events(conversation_id, created_at_utc);

                -- Requests for things the support agent cannot do yet; kept for the
                -- development team and never removed by chat retention.
                CREATE TABLE IF NOT EXISTS feature_requests (
                    request_id TEXT PRIMARY KEY,
                    conversation_id TEXT NOT NULL,
                    agent_id TEXT NOT NULL,
                    user_name TEXT NOT NULL,
                    summary TEXT NOT NULL,
                    user_words TEXT NOT NULL,
                    status TEXT NOT NULL DEFAULT 'New',
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_feature_requests_created
                    ON feature_requests(created_at_utc);

                UPDATE agent_runs
                SET state = 'Queued', capability_hash = NULL,
                    capability_expires_at_utc = NULL, framework_run_id = NULL,
                    updated_at_utc = strftime('%Y-%m-%dT%H:%M:%fZ', 'now'),
                    last_error = 'Recovered after ControlPlane restart.'
                WHERE state = 'Running';
                UPDATE messages
                SET state = 'Queued'
                WHERE message_id IN (
                    SELECT user_message_id FROM agent_runs WHERE state = 'Queued'
                ) AND state = 'Working';
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await EnsureColumnAsync(connection, "conversations", "helpdesk_status",
                "TEXT NOT NULL DEFAULT 'New'", cancellationToken);
            await EnsureColumnAsync(connection, "conversations", "responder_mode",
                "TEXT NOT NULL DEFAULT 'Hermes'", cancellationToken);
            await EnsureColumnAsync(connection, "conversations", "operator_read_sequence",
                "INTEGER NOT NULL DEFAULT 0", cancellationToken);
            await EnsureColumnAsync(connection, "conversations", "resolved_at_utc",
                "TEXT NULL", cancellationToken);
            await EnsureColumnAsync(connection, "conversations", "escalation_reason",
                "TEXT NULL", cancellationToken);
            await EnsureColumnAsync(connection, "conversations", "escalation_summary",
                "TEXT NULL", cancellationToken);
            await EnsureColumnAsync(connection, "conversations", "escalated_at_utc",
                "TEXT NULL", cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EnrollmentIssue> CreateEnrollmentAsync(
        TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var code = CreateSecret(24);
        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(lifetime);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO enrollment_codes(code_hash, expires_at_utc, created_at_utc)
                VALUES($hash, $expires, $created);
                """;
            command.Parameters.AddWithValue("$hash", HashSecret(code));
            command.Parameters.AddWithValue("$expires", Format(expires));
            command.Parameters.AddWithValue("$created", Format(now));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new EnrollmentIssue(code, expires);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <param name="allowReplacingActiveDevice">
    /// False for callers from the network: a one-time code must not be able to take over the
    /// identity (and the conversations) of a PC that is already enrolled and not revoked. An
    /// admin revokes the old device first. The server itself (development scripts) may replace.
    /// </param>
    /// <exception cref="DeviceAlreadyEnrolledException">The code is valid, but the requested
    /// device is active and may not be replaced. The code stays unused.</exception>
    public async Task<EnrollmentRedemption?> RedeemEnrollmentAsync(
        EnrollmentRequest request, CancellationToken cancellationToken,
        bool allowReplacingActiveDevice = true)
    {
        if (string.IsNullOrWhiteSpace(request.EnrollmentCode)) return null;
        var now = DateTimeOffset.UtcNow;
        var agentId = Guid.TryParse(request.AgentId, out var parsed)
            ? parsed.ToString("D")
            : Guid.NewGuid().ToString("D");
        var token = CreateSecret(32);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var lookup = connection.CreateCommand();
            lookup.Transaction = (SqliteTransaction)transaction;
            lookup.CommandText =
                """
                SELECT expires_at_utc, redeemed_at_utc
                FROM enrollment_codes WHERE code_hash = $hash;
                """;
            lookup.Parameters.AddWithValue("$hash", HashSecret(request.EnrollmentCode.Trim()));
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)
                || !reader.IsDBNull(1)
                || Parse(reader.GetString(0)) <= now)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
            await reader.DisposeAsync();

            if (!allowReplacingActiveDevice)
            {
                await using var existing = connection.CreateCommand();
                existing.Transaction = (SqliteTransaction)transaction;
                existing.CommandText =
                    """
                    SELECT EXISTS(
                        SELECT 1 FROM devices
                        WHERE agent_id = $agent_id AND revoked_at_utc IS NULL
                    );
                    """;
                existing.Parameters.AddWithValue("$agent_id", agentId);
                if (Convert.ToInt64(await existing.ExecuteScalarAsync(cancellationToken),
                        CultureInfo.InvariantCulture) == 1)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    throw new DeviceAlreadyEnrolledException(agentId);
                }
            }

            await using var redeem = connection.CreateCommand();
            redeem.Transaction = (SqliteTransaction)transaction;
            redeem.CommandText =
                """
                UPDATE enrollment_codes SET redeemed_at_utc = $now
                WHERE code_hash = $code_hash AND redeemed_at_utc IS NULL;

                INSERT INTO devices(agent_id, token_hash, machine_name,
                    enrolled_at_utc, last_seen_at_utc, revoked_at_utc)
                VALUES($agent_id, $token_hash, $machine_name, $now, $now, NULL)
                ON CONFLICT(agent_id) DO UPDATE SET
                    token_hash = excluded.token_hash,
                    machine_name = excluded.machine_name,
                    enrolled_at_utc = excluded.enrolled_at_utc,
                    last_seen_at_utc = excluded.last_seen_at_utc,
                    revoked_at_utc = NULL;
                """;
            redeem.Parameters.AddWithValue("$now", Format(now));
            redeem.Parameters.AddWithValue("$code_hash", HashSecret(request.EnrollmentCode.Trim()));
            redeem.Parameters.AddWithValue("$agent_id", agentId);
            redeem.Parameters.AddWithValue("$token_hash", HashSecret(token));
            redeem.Parameters.AddWithValue("$machine_name", Normalize(request.MachineName, 256));
            await redeem.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new EnrollmentRedemption(agentId, token, now);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> ValidateDeviceTokenAsync(
        string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT agent_id FROM devices
                WHERE token_hash = $hash AND revoked_at_utc IS NULL;
                """;
            command.Parameters.AddWithValue("$hash", HashSecret(token));
            var value = await command.ExecuteScalarAsync(cancellationToken);
            if (value is not string agentId) return null;

            await using var touch = connection.CreateCommand();
            touch.CommandText =
                "UPDATE devices SET last_seen_at_utc = $now WHERE agent_id = $agent_id;";
            touch.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
            touch.Parameters.AddWithValue("$agent_id", agentId);
            await touch.ExecuteNonQueryAsync(cancellationToken);
            return agentId;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task RevokeDeviceAsync(string agentId, CancellationToken cancellationToken) =>
        ExecuteAsync(
            """
            UPDATE devices SET revoked_at_utc = $now
            WHERE agent_id = $agent_id AND revoked_at_utc IS NULL;
            """,
            [("$now", Format(DateTimeOffset.UtcNow)), ("$agent_id", agentId)],
            cancellationToken);

    /// <summary>True when this id was ever enrolled, also when it was revoked since.</summary>
    public async Task<bool> IsDeviceEnrolledAsync(
        string agentId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT EXISTS(SELECT 1 FROM devices WHERE agent_id = $agent_id COLLATE NOCASE);";
            command.Parameters.AddWithValue("$agent_id", agentId);
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture) == 1;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> IsDeviceActiveAsync(
        string agentId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT EXISTS(
                    SELECT 1 FROM devices
                    WHERE agent_id = $agent_id AND revoked_at_utc IS NULL
                );
                """;
            command.Parameters.AddWithValue("$agent_id", agentId);
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture) == 1;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ChatConversation> OpenConversationAsync(
        string agentId, ChatIdentity identity, CancellationToken cancellationToken)
    {
        ValidateIdentity(identity);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            return await FindOrCreateConversationAsync(connection, null, agentId,
                identity, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ChatConversationSummary>> ListConversationsAsync(
        string agentId, ChatConversationListRequest request,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(request.Identity);
        var limit = Math.Clamp(request.Limit, 1, 50);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT c.conversation_id,
                    (SELECT body FROM messages first_message
                     WHERE first_message.conversation_id = c.conversation_id
                       AND first_message.role = 'User' AND first_message.body <> ''
                     ORDER BY first_message.sequence LIMIT 1),
                    COALESCE((SELECT body FROM messages latest
                              WHERE latest.conversation_id = c.conversation_id
                                AND latest.body <> ''
                              ORDER BY latest.sequence DESC LIMIT 1), ''),
                    COALESCE((SELECT text_direction FROM messages latest
                              WHERE latest.conversation_id = c.conversation_id
                                AND latest.body <> ''
                              ORDER BY latest.sequence DESC LIMIT 1), 'auto'),
                    c.created_at_utc, c.updated_at_utc, c.last_sequence,
                    CASE
                        WHEN c.is_active = 0 THEN 'Closed'
                        WHEN EXISTS (SELECT 1 FROM action_proposals pending
                                     WHERE pending.conversation_id = c.conversation_id
                                       AND pending.state IN ('Pending', 'Approved'))
                            THEN 'AwaitingApproval'
                        WHEN EXISTS (SELECT 1 FROM agent_runs running
                                     WHERE running.conversation_id = c.conversation_id
                                       AND running.state IN ('Queued', 'Running'))
                            THEN 'Working'
                        WHEN c.helpdesk_status = 'Resolved' THEN 'Resolved'
                        WHEN COALESCE((SELECT role FROM messages latest_role
                                       WHERE latest_role.conversation_id = c.conversation_id
                                         AND latest_role.body <> ''
                                       ORDER BY latest_role.sequence DESC LIMIT 1), 'User')
                             IN ('Assistant', 'Operator', 'System') THEN 'Answered'
                        ELSE 'Open'
                    END,
                    c.is_active
                FROM conversations c
                WHERE c.agent_id = $agent_id AND c.user_sid = $user_sid
                  AND EXISTS (SELECT 1 FROM messages first_message
                              WHERE first_message.conversation_id = c.conversation_id
                                AND first_message.role = 'User' AND first_message.body <> '')
                ORDER BY c.updated_at_utc DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$agent_id", agentId);
            command.Parameters.AddWithValue("$user_sid", request.Identity.UserSid);
            command.Parameters.AddWithValue("$limit", limit);
            var conversations = new List<ChatConversationSummary>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                Enum.TryParse<ChatConversationState>(reader.GetString(7), out var state);
                conversations.Add(new ChatConversationSummary(
                    reader.GetString(0), ConversationTitle(reader.GetString(1)),
                    reader.GetString(2), reader.GetString(3), Parse(reader.GetString(4)),
                    Parse(reader.GetString(5)), reader.GetInt64(6), state,
                    reader.GetInt64(8) == 1));
            }
            return conversations;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ChatConversationCloseReceipt> CloseConversationAsync(
        string agentId, ChatConversationCloseRequest request,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(request.Identity);
        var conversationId = Normalize(request.ConversationId, 100);
        if (string.IsNullOrWhiteSpace(conversationId))
            return new ChatConversationCloseReceipt(false,
                "گفتگوی پشتیبانی پیدا نشد.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(
                cancellationToken);
            await using var lookup = connection.CreateCommand();
            lookup.Transaction = (SqliteTransaction)transaction;
            lookup.CommandText =
                """
                SELECT agent_id, user_sid, is_active
                FROM conversations
                WHERE conversation_id = $conversation_id;
                """;
            lookup.Parameters.AddWithValue("$conversation_id", conversationId);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)
                || reader.GetString(0) != agentId
                || reader.GetString(1) != request.Identity.UserSid)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new ChatConversationCloseReceipt(false,
                    "این گفتگو متعلق به کاربر فعلی نیست.");
            }
            var isActive = reader.GetInt64(2) == 1;
            await reader.DisposeAsync();
            if (!isActive)
            {
                await transaction.CommitAsync(cancellationToken);
                return new ChatConversationCloseReceipt(true,
                    "این گفتگو قبلاً پایان یافته است.");
            }

            var now = DateTimeOffset.UtcNow;
            await using (var expire = connection.CreateCommand())
            {
                expire.Transaction = (SqliteTransaction)transaction;
                expire.CommandText =
                    """
                    UPDATE action_proposals
                    SET state = 'Expired', decided_at_utc = COALESCE(decided_at_utc, $now)
                    WHERE conversation_id = $conversation_id
                      AND state = 'Pending' AND expires_at_utc <= $now;
                    """;
                expire.Parameters.AddWithValue("$conversation_id", conversationId);
                expire.Parameters.AddWithValue("$now", Format(now));
                await expire.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var active = connection.CreateCommand())
            {
                active.Transaction = (SqliteTransaction)transaction;
                active.CommandText =
                    """
                    SELECT EXISTS(
                        SELECT 1 FROM agent_runs
                        WHERE conversation_id = $conversation_id
                          AND state IN ('Queued', 'Running')
                    ) OR EXISTS(
                        SELECT 1 FROM action_proposals
                        WHERE conversation_id = $conversation_id
                          AND state IN ('Pending', 'Approved')
                    );
                    """;
                active.Parameters.AddWithValue("$conversation_id", conversationId);
                if (Convert.ToInt64(await active.ExecuteScalarAsync(cancellationToken),
                        CultureInfo.InvariantCulture) != 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new ChatConversationCloseReceipt(false,
                        "تا پایان بررسی یا تصمیم‌گیری درباره اقدام پیشنهادی صبر کنید.");
                }
            }

            await using var close = connection.CreateCommand();
            close.Transaction = (SqliteTransaction)transaction;
            close.CommandText =
                """
                UPDATE conversations
                SET is_active = 0, user_name = '', session_id = 0, updated_at_utc = $now
                WHERE conversation_id = $conversation_id;

                UPDATE agent_runs
                SET capability_hash = NULL, capability_expires_at_utc = NULL,
                    framework_run_id = NULL
                WHERE conversation_id = $conversation_id;

                UPDATE tool_audit SET detail = NULL
                WHERE conversation_id = $conversation_id;

                DELETE FROM helpdesk_trace_events
                WHERE conversation_id = $conversation_id;
                """;
            close.Parameters.AddWithValue("$conversation_id", conversationId);
            close.Parameters.AddWithValue("$now", Format(now));
            await close.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ChatConversationCloseReceipt(true,
                "گفتگو بسته شد و تا پایان دوره نگهداری در دسترس است.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ChatMessageReceipt> SubmitMessageAsync(
        string agentId, ChatMessageSubmission submission, CancellationToken cancellationToken)
    {
        ValidateIdentity(submission.Identity);
        var body = Normalize(submission.MessageText, ChatLimits.MessageCharacters + 1);
        var clientId = Normalize(submission.ClientMessageId, 100);
        if (string.IsNullOrWhiteSpace(body) || string.IsNullOrWhiteSpace(clientId))
            return new ChatMessageReceipt(string.Empty, string.Empty, 0, false,
                "متن پیام نمی‌تواند خالی باشد.");
        if (body.Length > ChatLimits.MessageCharacters)
            return new ChatMessageReceipt(string.Empty, string.Empty, 0, false,
                $"پیام باید حداکثر {ChatLimits.MessageCharacters} نویسه باشد.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var conversation = await FindOrCreateConversationAsync(connection,
                (SqliteTransaction)transaction, agentId, submission.Identity,
                cancellationToken, submission.ConversationId);

            await using var duplicate = connection.CreateCommand();
            duplicate.Transaction = (SqliteTransaction)transaction;
            duplicate.CommandText =
                """
                SELECT message_id, sequence FROM messages
                WHERE conversation_id = $conversation_id AND client_message_id = $client_id;
                """;
            duplicate.Parameters.AddWithValue("$conversation_id", conversation.ConversationId);
            duplicate.Parameters.AddWithValue("$client_id", clientId);
            await using (var reader = await duplicate.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    var existing = new ChatMessageReceipt(conversation.ConversationId,
                        reader.GetString(0), reader.GetInt64(1), true,
                        "این پیام قبلاً دریافت شده است.");
                    await transaction.CommitAsync(cancellationToken);
                    return existing;
                }
            }

            var now = DateTimeOffset.UtcNow;
            var messageId = Guid.NewGuid().ToString("N");
            await using var modeCommand = connection.CreateCommand();
            modeCommand.Transaction = (SqliteTransaction)transaction;
            modeCommand.CommandText =
                "SELECT helpdesk_status, responder_mode FROM conversations WHERE conversation_id = $id;";
            modeCommand.Parameters.AddWithValue("$id", conversation.ConversationId);
            await using var modeReader = await modeCommand.ExecuteReaderAsync(cancellationToken);
            await modeReader.ReadAsync(cancellationToken);
            var wasResolved = modeReader.GetString(0) == HelpdeskStatus.Resolved;
            var queueHermes = wasResolved
                || modeReader.GetString(1) != HelpdeskResponder.Operator;
            await modeReader.DisposeAsync();

            var runId = queueHermes ? Guid.NewGuid().ToString("N") : null;
            var sequence = conversation.LastSequence + 1;
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = queueHermes
                ? """
                UPDATE conversations SET last_sequence = $sequence, updated_at_utc = $now,
                    user_name = $user_name, session_id = $session_id,
                    helpdesk_status = CASE WHEN helpdesk_status = 'Resolved' THEN 'New' ELSE helpdesk_status END,
                    responder_mode = CASE WHEN helpdesk_status = 'Resolved' THEN 'Hermes' ELSE responder_mode END,
                    resolved_at_utc = CASE WHEN helpdesk_status = 'Resolved' THEN NULL ELSE resolved_at_utc END
                WHERE conversation_id = $conversation_id;

                INSERT INTO messages(message_id, conversation_id, client_message_id, sequence,
                    role, body, text_direction, state, created_at_utc, run_id)
                VALUES($message_id, $conversation_id, $client_id, $sequence,
                    $role, $body, $direction, $state, $now, $run_id);

                INSERT INTO agent_runs(run_id, conversation_id, user_message_id, state,
                    created_at_utc, updated_at_utc)
                VALUES($run_id, $conversation_id, $message_id, 'Queued', $now, $now);
                """
                : """
                UPDATE conversations SET last_sequence = $sequence, updated_at_utc = $now,
                    user_name = $user_name, session_id = $session_id
                WHERE conversation_id = $conversation_id;

                INSERT INTO messages(message_id, conversation_id, client_message_id, sequence,
                    role, body, text_direction, state, created_at_utc, run_id)
                VALUES($message_id, $conversation_id, $client_id, $sequence,
                    $role, $body, $direction, $state, $now, NULL);
                """;
            insert.Parameters.AddWithValue("$sequence", sequence);
            insert.Parameters.AddWithValue("$now", Format(now));
            insert.Parameters.AddWithValue("$user_name",
                Normalize(submission.Identity.UserName, 256));
            insert.Parameters.AddWithValue("$session_id", submission.Identity.SessionId);
            insert.Parameters.AddWithValue("$conversation_id", conversation.ConversationId);
            insert.Parameters.AddWithValue("$message_id", messageId);
            insert.Parameters.AddWithValue("$client_id", clientId);
            insert.Parameters.AddWithValue("$role", ChatMessageRole.User.ToString());
            insert.Parameters.AddWithValue("$body", body);
            insert.Parameters.AddWithValue("$direction",
                submission.TextDirection is "rtl" or "ltr" ? submission.TextDirection : "auto");
            insert.Parameters.AddWithValue("$state", (queueHermes
                ? ChatMessageState.Queued : ChatMessageState.Completed).ToString());
            if (queueHermes) insert.Parameters.AddWithValue("$run_id", runId!);
            await insert.ExecuteNonQueryAsync(cancellationToken);
            if (queueHermes)
                await AppendProgressAsync(connection, (SqliteTransaction)transaction, runId!,
                    ChatProgressStage.Queued, ChatProgressActivity.None,
                    ChatProgressOutcome.None, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ChatMessageReceipt(conversation.ConversationId, messageId, sequence,
                true, "پیام برای بررسی در صف قرار گرفت.");
        }
        catch (UnauthorizedAccessException)
        {
            return new ChatMessageReceipt(string.Empty, string.Empty, 0, false,
                "این گفتگو متعلق به کاربر فعلی نیست.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ChatDelta?> GetDeltaAsync(string agentId, ChatDeltaRequest request,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(request.Identity);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var conversation = await GetConversationAsync(connection, null,
                request.ConversationId, cancellationToken, activeOnly: false);
            if (conversation is null || conversation.AgentId != agentId
                || conversation.UserSid != request.Identity.UserSid)
                return null;

            var messages = new List<ChatMessage>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    WITH recent_messages AS (
                        SELECT message_id
                        FROM messages
                        WHERE conversation_id = $conversation_id AND sequence > $after
                        ORDER BY sequence DESC
                        LIMIT $limit
                    )
                    SELECT message_id, conversation_id, sequence, role, body, text_direction,
                        state, created_at_utc, run_id
                    FROM messages
                    WHERE conversation_id = $conversation_id
                      AND (
                        message_id IN (SELECT message_id FROM recent_messages)
                        OR (role = 'User' AND state IN ('Queued', 'Working'))
                      )
                    ORDER BY sequence;
                    """;
                command.Parameters.AddWithValue("$conversation_id", request.ConversationId);
                command.Parameters.AddWithValue("$after", Math.Max(0, request.AfterSequence));
                command.Parameters.AddWithValue("$limit", _maxVisibleItems);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    messages.Add(ReadMessage(reader));
            }

            var actions = new List<ChatActionProposal>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    WITH recent_actions AS (
                        SELECT action_id
                        FROM action_proposals
                        WHERE conversation_id = $conversation_id AND sequence > $after
                        ORDER BY sequence DESC
                        LIMIT $limit
                    )
                    SELECT action_id, conversation_id, sequence, method, display_name,
                        description, parameters_json, created_at_utc, expires_at_utc, state
                    FROM action_proposals
                    WHERE conversation_id = $conversation_id
                      AND (
                        action_id IN (SELECT action_id FROM recent_actions)
                        OR state = 'Pending'
                      )
                    ORDER BY sequence;
                    """;
                command.Parameters.AddWithValue("$conversation_id", request.ConversationId);
                command.Parameters.AddWithValue("$after", Math.Max(0, request.AfterSequence));
                command.Parameters.AddWithValue("$limit", _maxVisibleItems);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    actions.Add(ReadAction(reader));
            }

            var progressEvents = new List<ChatRunProgressEvent>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    WITH recent_runs AS (
                        SELECT run_id
                        FROM chat_progress_events
                        WHERE conversation_id = $conversation_id
                        GROUP BY run_id
                        HAVING MAX(sequence) > $after
                        ORDER BY MAX(sequence) DESC
                        LIMIT $limit
                    )
                    SELECT event_id, conversation_id, run_id, sequence, stage,
                        activity, outcome, created_at_utc
                    FROM chat_progress_events
                    WHERE conversation_id = $conversation_id
                      AND run_id IN (SELECT run_id FROM recent_runs)
                    ORDER BY sequence;
                    """;
                command.Parameters.AddWithValue("$conversation_id", request.ConversationId);
                command.Parameters.AddWithValue("$after", Math.Max(0, request.AfterSequence));
                command.Parameters.AddWithValue("$limit", _maxVisibleItems);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    progressEvents.Add(ReadProgressEvent(reader));
            }

            var refreshed = await GetConversationAsync(connection, null,
                request.ConversationId, cancellationToken) ?? conversation;
            return new ChatDelta(refreshed, messages, actions, progressEvents)
            {
                VisibleItemLimit = _maxVisibleItems
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ChatRunLease?> LeaseNextRunAsync(
        int historyLimit, TimeSpan capabilityLifetime, CancellationToken cancellationToken)
    {
        var capability = CreateSecret(32);
        var now = DateTimeOffset.UtcNow;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var select = connection.CreateCommand();
            select.Transaction = (SqliteTransaction)transaction;
            select.CommandText =
                """
                SELECT r.run_id, r.conversation_id, r.user_message_id,
                    c.agent_id, c.user_sid, c.user_name, COALESCE(d.machine_name, ''),
                    r.attempts
                FROM agent_runs r
                JOIN conversations c ON c.conversation_id = r.conversation_id
                LEFT JOIN devices d ON d.agent_id = c.agent_id
                WHERE r.state = 'Queued'
                  AND c.is_active = 1
                  AND c.responder_mode = 'Hermes'
                  AND NOT EXISTS (
                    SELECT 1 FROM agent_runs active
                    WHERE active.conversation_id = r.conversation_id
                      AND active.state = 'Running')
                ORDER BY r.created_at_utc
                LIMIT 1;
                """;
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
            var runId = reader.GetString(0);
            var conversationId = reader.GetString(1);
            var userMessageId = reader.GetString(2);
            var agentId = reader.GetString(3);
            var userSid = reader.GetString(4);
            var userName = reader.GetString(5);
            var machineName = reader.GetString(6);
            var attempt = reader.GetInt32(7) + 1;
            await reader.DisposeAsync();

            await using var update = connection.CreateCommand();
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText =
                """
                UPDATE agent_runs
                SET state = 'Running', capability_hash = $capability_hash,
                    capability_expires_at_utc = $expires, attempts = attempts + 1,
                    updated_at_utc = $now
                WHERE run_id = $run_id AND state = 'Queued';
                UPDATE messages SET state = 'Working' WHERE message_id = $message_id;
                """;
            update.Parameters.AddWithValue("$capability_hash", HashSecret(capability));
            update.Parameters.AddWithValue("$expires", Format(now.Add(capabilityLifetime)));
            update.Parameters.AddWithValue("$now", Format(now));
            update.Parameters.AddWithValue("$run_id", runId);
            update.Parameters.AddWithValue("$message_id", userMessageId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
            await AppendProgressAsync(connection, (SqliteTransaction)transaction, runId,
                ChatProgressStage.AssistantStarted, ChatProgressActivity.None,
                ChatProgressOutcome.None, now, cancellationToken);

            var history = new List<ChatMessage>();
            await using var historyCommand = connection.CreateCommand();
            historyCommand.Transaction = (SqliteTransaction)transaction;
            historyCommand.CommandText =
                """
                SELECT message_id, conversation_id, sequence, role, body, text_direction,
                    state, created_at_utc, run_id
                FROM (
                    SELECT * FROM messages WHERE conversation_id = $conversation_id
                    ORDER BY sequence DESC LIMIT $limit
                ) ORDER BY sequence;
                """;
            historyCommand.Parameters.AddWithValue("$conversation_id", conversationId);
            historyCommand.Parameters.AddWithValue("$limit", Math.Clamp(historyLimit, 1, 100));
            await using var historyReader =
                await historyCommand.ExecuteReaderAsync(cancellationToken);
            while (await historyReader.ReadAsync(cancellationToken))
                history.Add(ReadMessage(historyReader));
            await transaction.CommitAsync(cancellationToken);
            Runs.Register(runId);

            return new ChatRunLease(runId, conversationId, agentId, userSid, userName,
                machineName, userMessageId, capability, attempt, history);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetFrameworkRunIdAsync(string runId, string frameworkRunId,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            "UPDATE agent_runs SET framework_run_id = $value, updated_at_utc = $now WHERE run_id = $id;",
            [("$value", frameworkRunId), ("$now", Format(DateTimeOffset.UtcNow)), ("$id", runId)],
            cancellationToken);
    }

    public async Task WriteProgressAsync(string runId, ChatProgressStage stage,
        ChatProgressActivity activity, ChatProgressOutcome outcome,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await AppendProgressAsync(connection, (SqliteTransaction)transaction, runId,
                stage, activity, outcome, DateTimeOffset.UtcNow, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteActionProgressAsync(string actionId, ChatProgressStage stage,
        ChatProgressOutcome outcome, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var lookup = connection.CreateCommand();
            lookup.Transaction = (SqliteTransaction)transaction;
            lookup.CommandText =
                "SELECT run_id FROM action_proposals WHERE action_id = $action_id;";
            lookup.Parameters.AddWithValue("$action_id", actionId);
            var runId = await lookup.ExecuteScalarAsync(cancellationToken) as string;
            if (runId is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return;
            }
            await AppendProgressAsync(connection, (SqliteTransaction)transaction, runId,
                stage, ChatProgressActivity.ProposedAction, outcome,
                DateTimeOffset.UtcNow, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CompleteRunAsync(string runId, string response,
        CancellationToken cancellationToken)
    {
        await FinishRunAsync(runId, Normalize(response, 32_000), succeeded: true,
            cancellationToken);
    }

    public async Task FailRunAsync(string runId, string error,
        CancellationToken cancellationToken, string? userMessage = null)
    {
        await FinishRunAsync(runId,
            Normalize(userMessage
                ?? "متأسفم، بررسی این درخواست کامل نشد. لطفاً دوباره تلاش کنید.",
                32_000),
            succeeded: false, cancellationToken, Normalize(error, 2_000));
    }

    public async Task RequeueRunAsync(string runId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                UPDATE agent_runs
                SET state = 'Queued', capability_hash = NULL,
                    capability_expires_at_utc = NULL, framework_run_id = NULL,
                    updated_at_utc = $now, last_error = 'Requeued during shutdown.'
                WHERE run_id = $run_id AND state = 'Running';
                SELECT changes();
                """;
            command.Parameters.AddWithValue("$now", Format(now));
            command.Parameters.AddWithValue("$run_id", runId);
            var changed = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture) > 0;
            if (changed)
            {
                await using var message = connection.CreateCommand();
                message.Transaction = (SqliteTransaction)transaction;
                message.CommandText =
                    """
                    UPDATE messages SET state = 'Queued'
                    WHERE message_id = (
                        SELECT user_message_id FROM agent_runs WHERE run_id = $run_id
                    ) AND state = 'Working';
                    """;
                message.Parameters.AddWithValue("$run_id", runId);
                await message.ExecuteNonQueryAsync(cancellationToken);
                await AppendProgressAsync(connection, (SqliteTransaction)transaction, runId,
                    ChatProgressStage.Retrying, ChatProgressActivity.None,
                    ChatProgressOutcome.None, now, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> TryRequeueTransientRunAsync(string runId, int maxAttempts,
        CancellationToken cancellationToken)
        => await TryRequeueRunAsync(runId, maxAttempts,
            "Transient Hermes failure; retry queued.", cancellationToken);

    public async Task<bool> TryRequeueMissingActionAsync(string runId,
        CancellationToken cancellationToken)
        => await TryRequeueRunAsync(runId, maxAttempts: 2,
            "Hermes omitted the required Office action; corrective retry queued.",
            cancellationToken);

    private async Task<bool> TryRequeueRunAsync(string runId, int maxAttempts,
        string reason, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                UPDATE agent_runs
                SET state = 'Queued', capability_hash = NULL,
                    capability_expires_at_utc = NULL, framework_run_id = NULL,
                    updated_at_utc = $now, last_error = $reason
                WHERE run_id = $run_id AND state = 'Running' AND attempts < $max_attempts;
                SELECT changes();
                """;
            command.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$run_id", runId);
            command.Parameters.AddWithValue("$max_attempts", Math.Clamp(maxAttempts, 1, 10));
            command.Parameters.AddWithValue("$reason", Normalize(reason, 2_000));
            var changed = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture) > 0;
            if (changed)
            {
                await using var message = connection.CreateCommand();
                message.Transaction = (SqliteTransaction)transaction;
                message.CommandText =
                    """
                    UPDATE messages
                    SET state = 'Queued'
                    WHERE message_id = (
                        SELECT user_message_id FROM agent_runs WHERE run_id = $run_id
                    ) AND state = 'Working';
                    """;
                message.Parameters.AddWithValue("$run_id", runId);
                await message.ExecuteNonQueryAsync(cancellationToken);
                await AppendProgressAsync(connection, (SqliteTransaction)transaction, runId,
                    ChatProgressStage.Retrying, ChatProgressActivity.None,
                    ChatProgressOutcome.None, DateTimeOffset.UtcNow, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return changed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> HasActionProposalAsync(string runId, string method,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT EXISTS(SELECT 1 FROM action_proposals WHERE run_id = $run_id AND method = $method);";
            command.Parameters.AddWithValue("$run_id", runId);
            command.Parameters.AddWithValue("$method", method);
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture) != 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ChatRunScope?> ResolveCapabilityAsync(string capability,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(capability)) return null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            return await ResolveCapabilityAsync(connection, null, capability, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ChatActionProposal?> CreateActionProposalAsync(string capability,
        string method, string displayName, string description, JsonElement? parameters,
        TimeSpan lifetime, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var scope = await ResolveCapabilityAsync(connection, (SqliteTransaction)transaction,
                capability, cancellationToken);
            // A request the user stopped must not leave a new proposal behind.
            if (scope is null || Runs.Get(scope.RunId)?.StopRequested == true)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            var conversation = await GetConversationAsync(connection,
                (SqliteTransaction)transaction, scope.ConversationId, cancellationToken)
                ?? throw new InvalidOperationException("Run conversation disappeared.");
            var now = DateTimeOffset.UtcNow;
            var proposal = new ChatActionProposal(Guid.NewGuid().ToString("N"),
                scope.ConversationId, conversation.LastSequence + 1,
                Normalize(method, 200), Normalize(displayName, 200),
                Normalize(description, 1_000), parameters?.Clone(), now,
                now.Add(lifetime), ChatActionState.Pending);

            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                UPDATE conversations SET last_sequence = $sequence, updated_at_utc = $now
                WHERE conversation_id = $conversation_id;
                INSERT INTO action_proposals(action_id, conversation_id, run_id, sequence,
                    method, display_name, description, parameters_json, state,
                    created_at_utc, expires_at_utc)
                VALUES($action_id, $conversation_id, $run_id, $sequence, $method,
                    $display_name, $description, $parameters, 'Pending', $now, $expires);
                """;
            command.Parameters.AddWithValue("$sequence", proposal.Sequence);
            command.Parameters.AddWithValue("$now", Format(now));
            command.Parameters.AddWithValue("$conversation_id", scope.ConversationId);
            command.Parameters.AddWithValue("$action_id", proposal.ActionId);
            command.Parameters.AddWithValue("$run_id", scope.RunId);
            command.Parameters.AddWithValue("$method", proposal.Method);
            command.Parameters.AddWithValue("$display_name", proposal.DisplayName);
            command.Parameters.AddWithValue("$description", proposal.Description);
            command.Parameters.AddWithValue("$parameters",
                parameters is null ? DBNull.Value : parameters.Value.GetRawText());
            command.Parameters.AddWithValue("$expires", Format(proposal.ExpiresAtUtc));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return proposal;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ActionDecisionResult> DecideActionAsync(string agentId,
        ChatActionDecision decision, CancellationToken cancellationToken)
    {
        ValidateIdentity(decision.Identity);
        var responseText = decision.ResponseText is null
            ? null
            : Normalize(decision.ResponseText, ChatLimits.MessageCharacters + 1);
        if (responseText is not null
            && (responseText.Length > ChatLimits.MessageCharacters
                || !ChatActionReply.TryParse(responseText, out var responseApproved)
                || responseApproved != decision.Approved))
            return new ActionDecisionResult(false, null,
                "Reply with an exact yes/no response to decide this action.");
        var now = DateTimeOffset.UtcNow;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var conversation = await GetConversationAsync(connection,
                (SqliteTransaction)transaction, decision.ConversationId, cancellationToken);
            if (conversation is null || conversation.AgentId != agentId
                || conversation.UserSid != decision.Identity.UserSid)
                return new ActionDecisionResult(false, null,
                    "این اقدام متعلق به کاربر فعلی نیست.");

            await using var lookup = connection.CreateCommand();
            lookup.Transaction = (SqliteTransaction)transaction;
            lookup.CommandText =
                """
                SELECT action_id, conversation_id, sequence, method, display_name,
                    description, parameters_json, created_at_utc, expires_at_utc, state
                FROM action_proposals
                WHERE action_id = $action_id AND conversation_id = $conversation_id;
                """;
            lookup.Parameters.AddWithValue("$action_id", decision.ActionId);
            lookup.Parameters.AddWithValue("$conversation_id", decision.ConversationId);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return new ActionDecisionResult(false, null, "اقدام پیشنهادی پیدا نشد.");
            var proposal = ReadAction(reader);
            await reader.DisposeAsync();
            if (proposal.State != ChatActionState.Pending)
                return new ActionDecisionResult(false, proposal,
                    "درباره این اقدام قبلاً تصمیم گرفته شده است.");
            if (proposal.ExpiresAtUtc <= now)
            {
                await UpdateActionStateAsync(connection, (SqliteTransaction)transaction,
                    proposal.ActionId, ChatActionState.Expired, now, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new ActionDecisionResult(false, proposal with
                {
                    State = ChatActionState.Expired
                }, "زمان تأیید این اقدام به پایان رسیده است.");
            }

            var state = decision.Approved
                ? ChatActionState.Approved
                : ChatActionState.Rejected;
            await UpdateActionStateAsync(connection, (SqliteTransaction)transaction,
                proposal.ActionId, state, now, cancellationToken);
            ChatMessage? decisionMessage = null;
            if (responseText is not null)
            {
                var decisionMessageId = Guid.NewGuid().ToString("N");
                var decisionSequence = conversation.LastSequence + 1;
                var direction = responseText.Any(character =>
                    character is >= '\u0590' and <= '\u08ff') ? "rtl" : "ltr";
                await using var insertDecision = connection.CreateCommand();
                insertDecision.Transaction = (SqliteTransaction)transaction;
                insertDecision.CommandText =
                    """
                    UPDATE conversations SET last_sequence = $sequence, updated_at_utc = $now
                    WHERE conversation_id = $conversation_id;
                    INSERT INTO messages(message_id, conversation_id, sequence, role, body,
                        text_direction, state, created_at_utc, run_id)
                    VALUES($message_id, $conversation_id, $sequence, 'User', $body,
                        $direction, 'Completed', $now, NULL);
                    """;
                insertDecision.Parameters.AddWithValue("$sequence",
                    decisionSequence);
                insertDecision.Parameters.AddWithValue("$now", Format(now));
                insertDecision.Parameters.AddWithValue("$conversation_id",
                    conversation.ConversationId);
                insertDecision.Parameters.AddWithValue("$message_id",
                    decisionMessageId);
                insertDecision.Parameters.AddWithValue("$body", responseText);
                insertDecision.Parameters.AddWithValue("$direction", direction);
                await insertDecision.ExecuteNonQueryAsync(cancellationToken);
                decisionMessage = new ChatMessage(decisionMessageId,
                    conversation.ConversationId, decisionSequence, ChatMessageRole.User,
                    responseText, direction, ChatMessageState.Completed, now);
            }
            await transaction.CommitAsync(cancellationToken);
            return new ActionDecisionResult(true, proposal with { State = state },
                decision.Approved ? "اقدام تأیید شد." : "اقدام رد شد.",
                decisionMessage);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkActionExecutedAsync(string actionId, bool succeeded, string? error,
        CancellationToken cancellationToken)
    {
        var state = succeeded ? ChatActionState.Executed : ChatActionState.Failed;
        await ExecuteAsync(
            """
            UPDATE action_proposals SET state = $state, executed_at_utc = $now,
                execution_error = $error
            WHERE action_id = $id AND state = 'Approved';

            INSERT INTO tool_audit(event_id, run_id, conversation_id, agent_id,
                tool_name, outcome, detail, created_at_utc)
            SELECT $event_id, p.run_id, p.conversation_id, c.agent_id,
                'endpoint_action:' || p.method, $outcome, $error, $now
            FROM action_proposals p
            JOIN conversations c ON c.conversation_id = p.conversation_id
            WHERE p.action_id = $id AND p.state = $state AND changes() = 1;
            """,
            [("$state", state.ToString()), ("$now", Format(DateTimeOffset.UtcNow)),
                ("$error", (object?)Normalize(error ?? string.Empty, 2_000) ?? DBNull.Value),
                ("$id", actionId), ("$event_id", Guid.NewGuid().ToString("N")),
                ("$outcome", succeeded ? "executed" : "failed")],
            cancellationToken);
    }

    public Task WriteToolAuditAsync(ChatRunScope scope, string toolName,
        string outcome, string? detail, CancellationToken cancellationToken) =>
        ExecuteAsync(
            """
            INSERT INTO tool_audit(event_id, run_id, conversation_id, agent_id,
                tool_name, outcome, detail, created_at_utc)
            VALUES($event_id, $run_id, $conversation_id, $agent_id,
                $tool_name, $outcome, $detail, $now);
            """,
            [("$event_id", Guid.NewGuid().ToString("N")), ("$run_id", scope.RunId),
                ("$conversation_id", scope.ConversationId), ("$agent_id", scope.AgentId),
                ("$tool_name", Normalize(toolName, 200)), ("$outcome", Normalize(outcome, 100)),
                ("$detail", (object?)Normalize(detail ?? string.Empty, 2_000) ?? DBNull.Value),
                ("$now", Format(DateTimeOffset.UtcNow))],
            cancellationToken);

    public async Task<int> PurgeExpiredDataAsync(DateTimeOffset cutoff,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                DELETE FROM enrollment_codes
                WHERE expires_at_utc < $cutoff;

                DELETE FROM tool_audit
                WHERE created_at_utc < $cutoff
                  AND NOT EXISTS (
                    SELECT 1 FROM conversations c
                    WHERE c.conversation_id = tool_audit.conversation_id
                      AND c.updated_at_utc >= $cutoff
                  );

                CREATE TEMP TABLE IF NOT EXISTS expired_conversations (
                    conversation_id TEXT PRIMARY KEY
                );
                DELETE FROM expired_conversations;
                INSERT INTO expired_conversations(conversation_id)
                SELECT c.conversation_id
                FROM conversations c
                WHERE c.updated_at_utc < $cutoff
                  -- Unsolved cases stay until the helpdesk team resolves them.
                  AND c.helpdesk_status <> 'Escalated'
                  AND NOT EXISTS (
                    SELECT 1 FROM agent_runs r
                    WHERE r.conversation_id = c.conversation_id
                      AND r.state IN ('Queued', 'Running')
                  );

                DELETE FROM action_proposals
                WHERE conversation_id IN (SELECT conversation_id FROM expired_conversations);
                DELETE FROM helpdesk_trace_events
                WHERE conversation_id IN (SELECT conversation_id FROM expired_conversations);
                DELETE FROM chat_progress_events
                WHERE conversation_id IN (SELECT conversation_id FROM expired_conversations);
                DELETE FROM agent_runs
                WHERE conversation_id IN (SELECT conversation_id FROM expired_conversations);
                DELETE FROM messages
                WHERE conversation_id IN (SELECT conversation_id FROM expired_conversations);
                DELETE FROM conversations
                WHERE conversation_id IN (SELECT conversation_id FROM expired_conversations);
                """;
            command.Parameters.AddWithValue("$cutoff", Format(cutoff));
            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return affected;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task FinishRunAsync(string runId, string response, bool succeeded,
        CancellationToken cancellationToken, string? error = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var lookup = connection.CreateCommand();
            lookup.Transaction = (SqliteTransaction)transaction;
            lookup.CommandText =
                """
                SELECT r.conversation_id, r.user_message_id
                FROM agent_runs r
                JOIN conversations c ON c.conversation_id = r.conversation_id
                WHERE r.run_id = $run_id AND r.state = 'Running';
                """;
            lookup.Parameters.AddWithValue("$run_id", runId);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return;
            }
            var conversationId = reader.GetString(0);
            var userMessageId = reader.GetString(1);
            await reader.DisposeAsync();
            var now = DateTimeOffset.UtcNow;
            var progress = await AppendProgressAsync(connection,
                (SqliteTransaction)transaction, runId,
                succeeded ? ChatProgressStage.Completed : ChatProgressStage.Failed,
                ChatProgressActivity.None,
                succeeded ? ChatProgressOutcome.Success : ChatProgressOutcome.Failure,
                now, cancellationToken);
            if (progress is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return;
            }
            var messageSequence = progress.Sequence + 1;

            await using var update = connection.CreateCommand();
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText =
                """
                UPDATE conversations SET last_sequence = $message_sequence, updated_at_utc = $now
                WHERE conversation_id = $conversation_id;
                UPDATE messages SET state = $user_state WHERE message_id = $user_message_id;
                INSERT INTO messages(message_id, conversation_id, sequence, role, body,
                    text_direction, state, created_at_utc, run_id)
                VALUES($message_id, $conversation_id, $message_sequence, 'Assistant', $body,
                    'auto', $assistant_state, $now, $run_id);
                UPDATE agent_runs SET state = $run_state, capability_hash = NULL,
                    capability_expires_at_utc = NULL, updated_at_utc = $now, last_error = $error
                WHERE run_id = $run_id;
                """;
            update.Parameters.AddWithValue("$message_sequence", messageSequence);
            update.Parameters.AddWithValue("$now", Format(now));
            update.Parameters.AddWithValue("$conversation_id", conversationId);
            update.Parameters.AddWithValue("$user_state",
                (succeeded ? ChatMessageState.Completed : ChatMessageState.Failed).ToString());
            update.Parameters.AddWithValue("$user_message_id", userMessageId);
            update.Parameters.AddWithValue("$message_id", Guid.NewGuid().ToString("N"));
            update.Parameters.AddWithValue("$body", response);
            update.Parameters.AddWithValue("$assistant_state",
                (succeeded ? ChatMessageState.Completed : ChatMessageState.Failed).ToString());
            update.Parameters.AddWithValue("$run_state", succeeded ? "Completed" : "Failed");
            update.Parameters.AddWithValue("$run_id", runId);
            update.Parameters.AddWithValue("$error", error is null ? DBNull.Value : error);
            await update.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<ChatRunProgressEvent?> AppendProgressAsync(
        SqliteConnection connection, SqliteTransaction transaction, string runId,
        ChatProgressStage stage, ChatProgressActivity activity,
        ChatProgressOutcome outcome, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var lookup = connection.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText =
            """
            SELECT r.conversation_id, c.last_sequence
            FROM agent_runs r
            JOIN conversations c ON c.conversation_id = r.conversation_id
            WHERE r.run_id = $run_id;
            """;
        lookup.Parameters.AddWithValue("$run_id", runId);
        await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var conversationId = reader.GetString(0);
        var sequence = reader.GetInt64(1) + 1;
        await reader.DisposeAsync();

        var progress = new ChatRunProgressEvent(Guid.NewGuid().ToString("N"),
            conversationId, runId, sequence, stage, activity, outcome, now);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            UPDATE conversations SET last_sequence = $sequence, updated_at_utc = $now
            WHERE conversation_id = $conversation_id;
            INSERT INTO chat_progress_events(event_id, conversation_id, run_id, sequence,
                stage, activity, outcome, created_at_utc)
            VALUES($event_id, $conversation_id, $run_id, $sequence,
                $stage, $activity, $outcome, $now);
            """;
        insert.Parameters.AddWithValue("$sequence", progress.Sequence);
        insert.Parameters.AddWithValue("$now", Format(progress.CreatedAtUtc));
        insert.Parameters.AddWithValue("$conversation_id", progress.ConversationId);
        insert.Parameters.AddWithValue("$event_id", progress.EventId);
        insert.Parameters.AddWithValue("$run_id", progress.RunId);
        insert.Parameters.AddWithValue("$stage", progress.Stage.ToString());
        insert.Parameters.AddWithValue("$activity", progress.Activity.ToString());
        insert.Parameters.AddWithValue("$outcome", progress.Outcome.ToString());
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return progress;
    }

    private async Task<ChatConversation> FindOrCreateConversationAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string agentId, ChatIdentity identity,
        CancellationToken cancellationToken, string? requestedConversationId = null)
    {
        if (!string.IsNullOrWhiteSpace(requestedConversationId))
        {
            var requested = await GetConversationAsync(connection, transaction,
                requestedConversationId, cancellationToken);
            if (requested is null || requested.AgentId != agentId
                || requested.UserSid != identity.UserSid)
                throw new UnauthorizedAccessException();
            return requested;
        }

        await using var find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText =
            """
            SELECT conversation_id, agent_id, user_sid, user_name, session_id,
                created_at_utc, updated_at_utc, last_sequence
            FROM conversations
            WHERE agent_id = $agent_id AND user_sid = $user_sid AND is_active = 1;
            """;
        find.Parameters.AddWithValue("$agent_id", agentId);
        find.Parameters.AddWithValue("$user_sid", identity.UserSid);
        await using (var reader = await find.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
                return ReadConversation(reader);
        }

        var now = DateTimeOffset.UtcNow;
        var conversation = new ChatConversation(Guid.NewGuid().ToString("N"), agentId,
            identity.UserSid, Normalize(identity.UserName, 256), identity.SessionId,
            now, now, 0);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO conversations(conversation_id, agent_id, user_sid, user_name,
                session_id, created_at_utc, updated_at_utc, is_active, last_sequence)
            VALUES($conversation_id, $agent_id, $user_sid, $user_name, $session_id,
                $created, $updated, 1, 0);
            """;
        insert.Parameters.AddWithValue("$conversation_id", conversation.ConversationId);
        insert.Parameters.AddWithValue("$agent_id", conversation.AgentId);
        insert.Parameters.AddWithValue("$user_sid", conversation.UserSid);
        insert.Parameters.AddWithValue("$user_name", conversation.UserName);
        insert.Parameters.AddWithValue("$session_id", conversation.SessionId);
        insert.Parameters.AddWithValue("$created", Format(now));
        insert.Parameters.AddWithValue("$updated", Format(now));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return conversation;
    }

    private static async Task<ChatConversation?> GetConversationAsync(
        SqliteConnection connection, SqliteTransaction? transaction,
        string conversationId, CancellationToken cancellationToken,
        bool activeOnly = true)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT conversation_id, agent_id, user_sid, user_name, session_id,
                created_at_utc, updated_at_utc, last_sequence
            FROM conversations
            WHERE conversation_id = $conversation_id
              AND ($active_only = 0 OR is_active = 1);
            """;
        command.Parameters.AddWithValue("$conversation_id", conversationId);
        command.Parameters.AddWithValue("$active_only", activeOnly ? 1 : 0);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadConversation(reader) : null;
    }

    private static string ConversationTitle(string message)
    {
        var flattened = string.Join(' ', message
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (string.IsNullOrWhiteSpace(flattened)) return "گفتگوی پشتیبانی";
        var text = new StringInfo(flattened);
        return text.LengthInTextElements <= 80
            ? flattened
            : text.SubstringByTextElements(0, 80) + "…";
    }

    private static async Task<ChatRunScope?> ResolveCapabilityAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string capability, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT r.run_id, r.conversation_id, c.agent_id, c.user_sid,
                r.capability_expires_at_utc
            FROM agent_runs r
            JOIN conversations c ON c.conversation_id = r.conversation_id
            WHERE r.capability_hash = $hash AND r.state = 'Running';
            """;
        command.Parameters.AddWithValue("$hash", HashSecret(capability));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var scope = new ChatRunScope(reader.GetString(0), reader.GetString(1),
            reader.GetString(2), reader.GetString(3), Parse(reader.GetString(4)));
        return scope.ExpiresAtUtc > DateTimeOffset.UtcNow ? scope : null;
    }

    private async Task ExecuteAsync(string sql, (string Name, object? Value)[] parameters,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters)
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
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
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, string table,
        string column, string definition, CancellationToken cancellationToken)
    {
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await inspect.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        await reader.DisposeAsync();

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Task UpdateActionStateAsync(SqliteConnection connection,
        SqliteTransaction transaction, string actionId, ChatActionState state,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE action_proposals SET state = $state, decided_at_utc = $now
            WHERE action_id = $action_id AND state = 'Pending';
            """;
        command.Parameters.AddWithValue("$state", state.ToString());
        command.Parameters.AddWithValue("$now", Format(now));
        command.Parameters.AddWithValue("$action_id", actionId);
        return ExecuteAndDisposeAsync(command, cancellationToken);
    }

    private static async Task ExecuteAndDisposeAsync(
        SqliteCommand command, CancellationToken cancellationToken)
    {
        await using (command)
            await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ChatConversation ReadConversation(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetInt32(4), Parse(reader.GetString(5)),
            Parse(reader.GetString(6)), reader.GetInt64(7));

    private static ChatMessage ReadMessage(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
            Enum.Parse<ChatMessageRole>(reader.GetString(3)), reader.GetString(4),
            reader.GetString(5), Enum.Parse<ChatMessageState>(reader.GetString(6)),
            Parse(reader.GetString(7)), reader.IsDBNull(8) ? null : reader.GetString(8));

    private static ChatActionProposal ReadAction(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.IsDBNull(6) ? null : JsonSerializer.Deserialize<JsonElement>(reader.GetString(6)),
            Parse(reader.GetString(7)), Parse(reader.GetString(8)),
            Enum.Parse<ChatActionState>(reader.GetString(9)));

    private static ChatRunProgressEvent ReadProgressEvent(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetInt64(3), Enum.Parse<ChatProgressStage>(reader.GetString(4)),
            Enum.Parse<ChatProgressActivity>(reader.GetString(5)),
            Enum.Parse<ChatProgressOutcome>(reader.GetString(6)),
            Parse(reader.GetString(7)));

    private static void ValidateIdentity(ChatIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.UserSid)
            || !identity.UserSid.StartsWith("S-1-", StringComparison.Ordinal)
            || identity.UserSid.Length > 184)
            throw new ArgumentException("A valid Windows user SID is required.", nameof(identity));
    }

    private static string Normalize(string? value, int maximum) =>
        new string((value ?? string.Empty)
            .Where(character => character is '\r' or '\n' or '\t' || !char.IsControl(character))
            .Take(maximum).ToArray()).Trim();

    private static string CreateSecret(int byteCount) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string HashSecret(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
