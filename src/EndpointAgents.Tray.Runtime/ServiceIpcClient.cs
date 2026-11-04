using EndpointAgents.Shared.LocalIpc;
using EndpointAgents.Shared.Ibsng;
using EndpointAgents.Shared.Chat;
using EndpointAgents.Shared.Support;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace EndpointAgents.Tray;

public sealed class ServiceIpcClient
{
    public async Task<AgentRuntimeSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(new ServiceIpcRequest(Guid.NewGuid().ToString("N"),
            ServiceIpcProtocol.StatusMethod), cancellationToken);
        return ReadPayload<AgentRuntimeSnapshot>(response);
    }

    public Task<ServiceIpcResponse> SetPausedAsync(bool paused,
        CancellationToken cancellationToken) =>
        SendAsync(new ServiceIpcRequest(Guid.NewGuid().ToString("N"),
            paused ? ServiceIpcProtocol.PauseMethod : ServiceIpcProtocol.ResumeMethod),
            cancellationToken);

    public async Task<SupportRequestReceipt> SubmitSupportAsync(SupportRequestDraft draft,
        CancellationToken cancellationToken)
    {
        var request = new ServiceIpcRequest(Guid.NewGuid().ToString("N"),
            ServiceIpcProtocol.SubmitSupportMethod, JsonSerializer.SerializeToElement(draft));
        var response = await SendAsync(request, cancellationToken);
        if (response.Payload is { } payload)
            return payload.Deserialize<SupportRequestReceipt>()
                ?? new SupportRequestReceipt(string.Empty, DateTimeOffset.UtcNow, false,
                    response.Message ?? "The support request was not accepted.");
        return new SupportRequestReceipt(string.Empty, DateTimeOffset.UtcNow, false,
            response.Message ?? "The support request was not accepted.");
    }

    public async Task<ChatConversation> OpenChatAsync(
        int sessionId, CancellationToken cancellationToken)
    {
        var identity = new ChatIdentity("S-1-0-0", Environment.UserName, sessionId);
        var request = new ServiceIpcRequest(Guid.NewGuid().ToString("N"),
            ServiceIpcProtocol.OpenChatMethod, JsonSerializer.SerializeToElement(identity));
        return ReadPayload<ChatConversation>(
            await SendAsync(request, cancellationToken));
    }

    public async Task<IReadOnlyList<ChatConversationSummary>> ListChatsAsync(
        int sessionId, CancellationToken cancellationToken)
    {
        var list = new ChatConversationListRequest(
            new ChatIdentity("S-1-0-0", Environment.UserName, sessionId), 50);
        var request = new ServiceIpcRequest(Guid.NewGuid().ToString("N"),
            ServiceIpcProtocol.ListChatsMethod, JsonSerializer.SerializeToElement(list));
        return ReadPayload<IReadOnlyList<ChatConversationSummary>>(
            await SendAsync(request, cancellationToken, TimeSpan.FromSeconds(20)));
    }

    public async Task<ChatMessageReceipt> SendChatMessageAsync(
        ChatMessageSubmission submission, CancellationToken cancellationToken)
    {
        var request = new ServiceIpcRequest(Guid.NewGuid().ToString("N"),
            ServiceIpcProtocol.SendChatMessageMethod,
            JsonSerializer.SerializeToElement(submission));
        return ReadPayload<ChatMessageReceipt>(
            await SendAsync(request, cancellationToken));
    }

    public async Task<ChatDelta> GetChatDeltaAsync(
        ChatDeltaRequest delta, CancellationToken cancellationToken)
    {
        var request = new ServiceIpcRequest(Guid.NewGuid().ToString("N"),
            ServiceIpcProtocol.GetChatDeltaMethod, JsonSerializer.SerializeToElement(delta));
        return ReadPayload<ChatDelta>(
            await SendAsync(request, cancellationToken, TimeSpan.FromSeconds(20)));
    }

    public async Task<ChatConversationCloseReceipt> CloseChatAsync(
        ChatConversationCloseRequest close, CancellationToken cancellationToken)
    {
        var request = new ServiceIpcRequest(Guid.NewGuid().ToString("N"),
            ServiceIpcProtocol.CloseChatMethod, JsonSerializer.SerializeToElement(close));
        return ReadPayload<ChatConversationCloseReceipt>(
            await SendAsync(request, cancellationToken));
    }

    public async Task<EndpointAgents.Shared.Problems.ChatNotFixedReceipt> ReportChatNotFixedAsync(
        EndpointAgents.Shared.Problems.ChatNotFixedRequest report, CancellationToken cancellationToken)
    {
        var request = new ServiceIpcRequest(Guid.NewGuid().ToString("N"),
            ServiceIpcProtocol.ChatNotFixedMethod, JsonSerializer.SerializeToElement(report));
        return ReadPayload<EndpointAgents.Shared.Problems.ChatNotFixedReceipt>(
            await SendAsync(request, cancellationToken));
    }

    /// <summary>Hands the Tray's saved crash reports to the service, which sends them on.</summary>
    public async Task ReportProblemsAsync(IReadOnlyList<EndpointAgents.Shared.Problems.ProblemReport> reports,
        CancellationToken cancellationToken)
    {
        var request = new ServiceIpcRequest(Guid.NewGuid().ToString("N"),
            ServiceIpcProtocol.ReportProblemsMethod, JsonSerializer.SerializeToElement(reports));
        var response = await SendAsync(request, cancellationToken);
        if (!response.Success) throw new InvalidOperationException(response.Message ?? "The service refused the reports.");
    }

    public async Task<ChatRunStopReceipt> StopChatRunAsync(
        ChatRunStopRequest stop, CancellationToken cancellationToken)
    {
        var request = new ServiceIpcRequest(Guid.NewGuid().ToString("N"),
            ServiceIpcProtocol.StopChatRunMethod, JsonSerializer.SerializeToElement(stop));
        return ReadPayload<ChatRunStopReceipt>(
            await SendAsync(request, cancellationToken));
    }

    public async Task<ChatActionDecisionReceipt> RespondToChatActionAsync(
        ChatActionDecision decision, CancellationToken cancellationToken)
    {
        var request = new ServiceIpcRequest(Guid.NewGuid().ToString("N"),
            ServiceIpcProtocol.RespondToChatActionMethod,
            JsonSerializer.SerializeToElement(decision));
        return ReadPayload<ChatActionDecisionReceipt>(
            await SendAsync(request, cancellationToken, TimeSpan.FromMinutes(11)));
    }

    /// <summary>Runs the local read-only IBSng diagnostic pipeline through the service.</summary>
    public async Task<IbsngDiagnosticReport> DiagnoseIbsngAsync(IbsngDiagnosticRequest request,
        CancellationToken cancellationToken)
    {
        var message = new ServiceIpcRequest(Guid.NewGuid().ToString("N"),
            ServiceIpcProtocol.DiagnoseIbsngMethod, JsonSerializer.SerializeToElement(request));
        return ReadPayload<IbsngDiagnosticReport>(await SendAsync(message, cancellationToken,
            TimeSpan.FromSeconds(45)));
    }

    /// <summary>Asks the service to open only the configured, approved IBSng dialer.</summary>
    public async Task OpenIbsngAsync(CancellationToken cancellationToken)
    {
        var message = new ServiceIpcRequest(Guid.NewGuid().ToString("N"),
            ServiceIpcProtocol.OpenIbsngMethod);
        var response = await SendAsync(message, cancellationToken, TimeSpan.FromSeconds(35));
        if (!response.Success)
            throw new InvalidOperationException(response.Message ?? response.State);
    }

    private static T ReadPayload<T>(ServiceIpcResponse response)
    {
        if (!response.Success || response.Payload is not { } payload)
            throw new InvalidOperationException(response.Message ?? response.State);
        return payload.Deserialize<T>()
            ?? throw new InvalidDataException("The service returned an unreadable response.");
    }

    private static async Task<ServiceIpcResponse> SendAsync(ServiceIpcRequest request,
        CancellationToken cancellationToken, TimeSpan? responseTimeout = null)
    {
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(3));
        await using var pipe = new NamedPipeClientStream(".", ServiceIpcProtocol.PipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Impersonation);
        await pipe.ConnectAsync(connectTimeout.Token);
        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationTimeout.CancelAfter(responseTimeout ?? TimeSpan.FromSeconds(10));
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(),
            operationTimeout.Token);
        var line = await reader.ReadLineAsync(operationTimeout.Token);
        return JsonSerializer.Deserialize<ServiceIpcResponse>(line
            ?? throw new EndOfStreamException("The service closed the local status pipe."))
            ?? throw new InvalidDataException("The service returned an empty local response.");
    }
}
