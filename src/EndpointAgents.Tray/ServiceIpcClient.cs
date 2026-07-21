using EndpointAgents.Shared.LocalIpc;
using EndpointAgents.Shared.Ibsng;
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
