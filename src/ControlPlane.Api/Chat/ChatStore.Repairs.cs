using System.Text.Json;

namespace ControlPlane.Api.Chat;

/// <summary>One repair the agent ran in a conversation, kept so it tries the next one.</summary>
public sealed record RepairAttempt(
    string ToolName,
    string ArtifactId,
    string Outcome,
    string? Error,
    DateTimeOffset AttemptedAtUtc);

public static class RepairAttemptOutcomes
{
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

public sealed partial class ChatStore
{
    public const string RepairAttemptEventType = "agent.repair_attempt";
    public const string EscalationEventType = "agent.escalation";
    public const int MaxEscalationReasonCharacters = 200;
    public const int MaxEscalationSummaryCharacters = 1_000;

    /// <summary>Records a repair attempt in the conversation's helpdesk timeline.</summary>
    public Task RecordRepairAttemptAsync(ChatRunScope scope, string toolName,
        string artifactId, bool succeeded, string? error,
        CancellationToken cancellationToken) =>
        AppendAgentTraceAsync(scope.ConversationId, RepairAttemptEventType,
            succeeded ? "Success" : "Error",
            new RepairAttempt(Normalize(toolName, 100), Normalize(artifactId, 100),
                succeeded ? RepairAttemptOutcomes.Completed : RepairAttemptOutcomes.Failed,
                error is null ? null : Normalize(error, 500), DateTimeOffset.UtcNow),
            cancellationToken);

    /// <summary>Repairs already run in this conversation, oldest first.</summary>
    public async Task<IReadOnlyList<RepairAttempt>> GetRepairAttemptsAsync(
        string conversationId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT payload_json FROM helpdesk_trace_events
                WHERE conversation_id = $id AND event_type = $type
                ORDER BY created_at_utc;
                """;
            command.Parameters.AddWithValue("$id", Normalize(conversationId, 100));
            command.Parameters.AddWithValue("$type", RepairAttemptEventType);
            var attempts = new List<RepairAttempt>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (JsonSerializer.Deserialize<RepairAttempt>(reader.GetString(0)) is { } attempt)
                    attempts.Add(attempt);
            return attempts;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Hands the conversation to the helpdesk team: it moves to the Escalated status with
    /// the agent's reason and summary, and the attempts so far stay in its timeline.
    /// </summary>
    public async Task<bool> EscalateConversationAsync(ChatRunScope scope, string reason,
        string summary, CancellationToken cancellationToken)
    {
        reason = Normalize(reason, MaxEscalationReasonCharacters);
        summary = Normalize(summary, MaxEscalationSummaryCharacters);
        if (string.IsNullOrWhiteSpace(reason)) return false;
        var now = DateTimeOffset.UtcNow;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var update = connection.CreateCommand();
            update.CommandText =
                """
                UPDATE conversations SET helpdesk_status = 'Escalated',
                    escalation_reason = $reason, escalation_summary = $summary,
                    escalated_at_utc = $now, resolved_at_utc = NULL, updated_at_utc = $now
                WHERE conversation_id = $id AND agent_id = $agent AND is_active = 1;
                """;
            update.Parameters.AddWithValue("$reason", reason);
            update.Parameters.AddWithValue("$summary", summary);
            update.Parameters.AddWithValue("$now", Format(now));
            update.Parameters.AddWithValue("$id", scope.ConversationId);
            update.Parameters.AddWithValue("$agent", scope.AgentId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
        }
        finally
        {
            _gate.Release();
        }
        await AppendAgentTraceAsync(scope.ConversationId, EscalationEventType, "Escalated",
            new { reason, summary, escalatedAtUtc = now }, cancellationToken);
        return true;
    }

    private Task AppendAgentTraceAsync(string conversationId, string eventType,
        string outcome, object payload, CancellationToken cancellationToken) =>
        ExecuteAsync(
            """
            INSERT INTO helpdesk_trace_events(event_id, conversation_id, sequence, direction,
                event_type, outcome, payload_json, created_at_utc)
            SELECT $event_id, $id, NULL, 'Agent', $type, $outcome, $payload, $now
            WHERE EXISTS(SELECT 1 FROM conversations WHERE conversation_id = $id);
            """,
            [("$event_id", Guid.NewGuid().ToString("N")), ("$id", conversationId),
                ("$type", eventType), ("$outcome", outcome),
                ("$payload", JsonSerializer.Serialize(payload)),
                ("$now", Format(DateTimeOffset.UtcNow))],
            cancellationToken);
}
