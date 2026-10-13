using EndpointAgents.Shared.LocalIpc;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace EndpointAgents.Tray;

/// <summary>
/// Keeps one outgoing connection to the service and runs the user-session tasks it sends back.
/// The Tray never listens on a pipe of its own: the service owns the only endpoint and decides,
/// from the identity Windows reports for this connection, which session and user it serves.
/// </summary>
public sealed class TrayServiceChannel(TrayTaskDispatcher dispatcher,
    string pipeName = ServiceIpcProtocol.PipeName)
{
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(10);

    /// <summary>Raised on every attach or detach; true while the service can reach this Tray.</summary>
    public event Action<bool>? AttachedChanged;

    public bool IsAttached { get; private set; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        var reportedFailure = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(cancellationToken);
                delay = TimeSpan.FromSeconds(1);
                reportedFailure = false;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The service is stopped or restarting; keep retrying quietly, log once.
                if (!reportedFailure)
                    TrayEventLogger.Warning("کانال فرمان سرویس در دسترس نیست و دوباره تلاش می‌شود: " +
                        $"{ex.GetType().Name}: {ex.Message}");
                reportedFailure = true;
            }
            finally
            {
                SetAttached(false);
            }

            try { await Task.Delay(delay, cancellationToken); }
            catch (OperationCanceledException) { break; }
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaximumRetryDelay.Ticks));
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            await pipe.ConnectAsync(connectTimeout.Token);
        }

        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            { AutoFlush = true };
        var writeGate = new SemaphoreSlim(1, 1);

        var attach = new ServiceIpcRequest(Guid.NewGuid().ToString("N"),
            ServiceIpcProtocol.AttachTrayMethod);
        await writer.WriteLineAsync(JsonSerializer.Serialize(attach).AsMemory(), cancellationToken);
        var reply = await LineFraming.ReadLineAsync(reader, ServiceIpcProtocol.MaxMessageCharacters,
            cancellationToken) ?? throw new EndOfStreamException("The service closed the channel.");
        var attached = JsonSerializer.Deserialize<ServiceIpcResponse>(reply);
        if (attached is not { Success: true })
            throw new UnauthorizedAccessException(attached?.Message ?? "The service refused the Tray.");

        SetAttached(true);
        TrayEventLogger.Information("برنامه ناحیه اعلان به کانال فرمان سرویس وصل شد.");

        using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = new List<Task>();
        try
        {
            while (true)
            {
                var line = await LineFraming.ReadLineAsync(reader,
                    TrayIpcProtocol.MaxMessageCharacters, connection.Token);
                if (line is null) break;
                running.RemoveAll(task => task.IsCompleted);
                running.Add(HandleAsync(line, writer, writeGate, connection.Token));
            }
        }
        finally
        {
            // Pending prompts are withdrawn: the service has already given up on them.
            connection.Cancel();
            try { await Task.WhenAll(running); } catch { }
        }
    }

    private async Task HandleAsync(string line, StreamWriter writer, SemaphoreSlim writeGate,
        CancellationToken cancellationToken)
    {
        TrayIpcResponse response;
        TrayIpcRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize<TrayIpcRequest>(line);
        }
        catch (JsonException) { }

        if (request is null || string.IsNullOrWhiteSpace(request.Id))
        {
            TrayEventLogger.Warning("یک درخواست نامعتبر از سرویس رد شد.");
            response = new TrayIpcResponse(request?.Id ?? string.Empty, false, "InvalidRequest");
        }
        else
        {
            response = await dispatcher.DispatchAsync(request, cancellationToken);
        }

        await writeGate.WaitAsync(cancellationToken);
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(response).AsMemory(),
                cancellationToken);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private void SetAttached(bool value)
    {
        if (IsAttached == value) return;
        IsAttached = value;
        AttachedChanged?.Invoke(value);
    }
}
