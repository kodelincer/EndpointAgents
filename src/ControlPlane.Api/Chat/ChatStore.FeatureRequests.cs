namespace ControlPlane.Api.Chat;

public static class FeatureRequestStatus
{
    public const string New = "New";
    public const string Planned = "Planned";
    public const string Done = "Done";
    public const string Declined = "Declined";

    public static bool IsValid(string? value) => value is New or Planned or Done or Declined;
}

/// <summary>Something a user asked for that the support agent cannot do yet.</summary>
public sealed record FeatureRequest(
    string RequestId,
    string ConversationId,
    string AgentId,
    string UserName,
    string MachineName,
    string Summary,
    string UserWords,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record FeatureRequestPatch(string Status);

public sealed partial class ChatStore
{
    public const int MaxFeatureSummaryCharacters = 300;
    public const int MaxFeatureUserWordsCharacters = 500;

    /// <summary>Saves a request the user agreed to send to the development team.</summary>
    public async Task<FeatureRequest?> RecordFeatureRequestAsync(ChatRunScope scope,
        string summary, string userWords, CancellationToken cancellationToken)
    {
        summary = Normalize(summary, MaxFeatureSummaryCharacters);
        userWords = Normalize(userWords, MaxFeatureUserWordsCharacters);
        if (string.IsNullOrWhiteSpace(summary)) return null;
        var now = DateTimeOffset.UtcNow;
        var requestId = Guid.NewGuid().ToString("N");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO feature_requests(request_id, conversation_id, agent_id, user_name,
                    summary, user_words, status, created_at_utc, updated_at_utc)
                SELECT $id, c.conversation_id, c.agent_id, c.user_name, $summary, $words,
                    'New', $now, $now
                FROM conversations c
                WHERE c.conversation_id = $conversation AND c.agent_id = $agent;
                """;
            insert.Parameters.AddWithValue("$id", requestId);
            insert.Parameters.AddWithValue("$summary", summary);
            insert.Parameters.AddWithValue("$words", userWords);
            insert.Parameters.AddWithValue("$now", Format(now));
            insert.Parameters.AddWithValue("$conversation", scope.ConversationId);
            insert.Parameters.AddWithValue("$agent", scope.AgentId);
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0) return null;
        }
        finally
        {
            _gate.Release();
        }
        await AppendAgentTraceAsync(scope.ConversationId, "agent.feature_request", "Recorded",
            new { requestId, summary }, cancellationToken);
        return (await GetFeatureRequestsAsync(500, cancellationToken))
            .FirstOrDefault(item => item.RequestId == requestId);
    }

    /// <summary>Newest first.</summary>
    public async Task<IReadOnlyList<FeatureRequest>> GetFeatureRequestsAsync(int limit,
        CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 500);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT f.request_id, f.conversation_id, f.agent_id, f.user_name,
                    COALESCE(d.machine_name, ''), f.summary, f.user_words, f.status,
                    f.created_at_utc, f.updated_at_utc
                FROM feature_requests f LEFT JOIN devices d ON d.agent_id = f.agent_id
                ORDER BY f.created_at_utc DESC LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", limit);
            var result = new List<FeatureRequest>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(new FeatureRequest(reader.GetString(0), reader.GetString(1),
                    reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetString(5), reader.GetString(6), reader.GetString(7),
                    Parse(reader.GetString(8)), Parse(reader.GetString(9))));
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> SetFeatureRequestStatusAsync(string requestId, string status,
        CancellationToken cancellationToken)
    {
        if (!FeatureRequestStatus.IsValid(status)) return false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var update = connection.CreateCommand();
            update.CommandText =
                """
                UPDATE feature_requests SET status = $status, updated_at_utc = $now
                WHERE request_id = $id;
                """;
            update.Parameters.AddWithValue("$status", status);
            update.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
            update.Parameters.AddWithValue("$id", Normalize(requestId, 100));
            return await update.ExecuteNonQueryAsync(cancellationToken) > 0;
        }
        finally
        {
            _gate.Release();
        }
    }
}
