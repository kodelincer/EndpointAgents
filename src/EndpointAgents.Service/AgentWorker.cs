using EndpointAgents.Core.Commands;
using EndpointAgents.Shared.Agents;
using EndpointAgents.Shared.Chat;
using EndpointAgents.Shared.Rpc;
using EndpointAgents.Shared.Support;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace EndpointAgents.Service;

/// <summary>
/// Maintains the endpoint's SignalR connection to the control plane and executes
/// commands received through the <c>RunRpc</c> hub method.
/// </summary>
/// <remarks>
/// SignalR handles short interruptions through automatic reconnect, while the
/// outer loop handles an initial connection failure or a fully closed connection.
/// Registration is repeated after every successful connection because a reconnect
/// may have created a new server-side connection record.
/// </remarks>
public sealed class AgentWorker : BackgroundService
{
    private readonly ILogger<AgentWorker> _logger;
    private readonly CommandDispatcher _dispatcher;
    private readonly ControlPlaneOptions _options;
    private readonly SemaphoreSlim _commandSlots;
    private string _agentId;
    private readonly AgentCredentialProvider _credentialProvider;
    private AgentDeviceCredential? _credential;
    private readonly AgentRuntimeState _runtime;
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private HubConnection? _connection;
    private readonly ConcurrentDictionary<string, RunningCommand> _running = new();

    public AgentWorker(ILogger<AgentWorker> logger, CommandDispatcher dispatcher,
        IOptions<ControlPlaneOptions> options, AgentIdentityProvider identityProvider,
        AgentCredentialProvider credentialProvider, AgentRuntimeState runtime)
    {
        _logger = logger;
        _dispatcher = dispatcher;
        _options = options.Value;
        _agentId = identityProvider.GetOrCreate(_options.AgentId);
        _credentialProvider = credentialProvider;
        _runtime = runtime;
        _runtime.Initialize(_agentId, _options.Version);
        _commandSlots = new SemaphoreSlim(_options.MaxConcurrentCommands, _options.MaxConcurrentCommands);
    }

    /// <summary>
    /// Creates the connection, registers the RPC callback, and keeps the worker
    /// connected until the host requests shutdown.
    /// </summary>
    /// <param name="stoppingToken">Raised by the host when the service is stopping.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _credential = await _credentialProvider.GetOrEnrollAsync(_agentId, stoppingToken);
        if (_credential is not null && _credential.AgentId != _agentId)
        {
            _agentId = _credential.AgentId;
            _runtime.Initialize(_agentId, _options.Version);
        }
        _connection = CreateConnection(stoppingToken);
        RegisterRpcHandler(_connection, stoppingToken);
        _runtime.SetConnection(AgentConnectionStatus.Connecting);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // StartAsync is only called for a fully disconnected connection;
                    // automatic reconnect owns the intermediate reconnecting states.
                    if (_connection.State == HubConnectionState.Disconnected)
                    {
                        _runtime.SetConnection(AgentConnectionStatus.Connecting);
                        _logger.LogInformation("Connecting agent {AgentId} to control plane: {HubUrl}",
                            _agentId, _options.HubUrl);
                        await _connection.StartAsync(stoppingToken);
                        await RegisterAgentAsync(stoppingToken);
                        _runtime.SetConnection(AgentConnectionStatus.Connected);
                        _logger.LogInformation("Endpoint agent connected.");
                    }

                    // Avoid a tight polling loop while periodically checking whether
                    // a fully closed connection needs to be started again.
                    await Task.Delay(GetRetryDelay(), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _runtime.SetConnection(AgentConnectionStatus.Disconnected, ex);
                    var retryDelay = GetRetryDelay();
                    _logger.LogWarning(ex,
                        "Control plane connection failed. Retrying in {RetryDelay}.", retryDelay);
                    await Task.Delay(retryDelay, stoppingToken);
                }
            }
        }
        finally
        {
            await ShutdownConnectionAsync();
        }
    }

    private HubConnection CreateConnection(CancellationToken stoppingToken)
    {
        // Attach callbacks before StartAsync so commands cannot arrive before the
        // worker is ready to process them.
        var connection = new HubConnectionBuilder()
            .WithUrl(_options.HubUrl, connectionOptions =>
            {
                if (_credential is not null)
                    connectionOptions.AccessTokenProvider =
                        () => Task.FromResult<string?>(_credential.DeviceToken);
            })
            .WithAutomaticReconnect(new JitteredRetryPolicy())
            .Build();

        connection.Reconnecting += exception =>
        {
            _runtime.SetConnection(AgentConnectionStatus.Reconnecting, exception);
            _logger.LogWarning(exception, "Connection to control plane lost. Reconnecting...");
            return Task.CompletedTask;
        };

        connection.Reconnected += async connectionId =>
        {
            if (stoppingToken.IsCancellationRequested) return;

            try
            {
                // The control plane may discard the old registration when the
                // transport disconnects, so register again after every reconnect.
                _logger.LogInformation("Reconnected to control plane. ConnectionId: {ConnectionId}",
                    connectionId);
                await RegisterAgentAsync(stoppingToken);
                _runtime.SetConnection(AgentConnectionStatus.Connected);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal service shutdown.
            }
            catch (Exception ex)
            {
                _runtime.SetConnection(AgentConnectionStatus.Faulted, ex);
                _logger.LogWarning(ex, "Failed to register the agent after reconnecting.");
            }
        };

        connection.Closed += exception =>
        {
            _runtime.SetConnection(AgentConnectionStatus.Disconnected, exception);
            if (!stoppingToken.IsCancellationRequested)
                _logger.LogWarning(exception, "Connection to control plane closed.");
            return Task.CompletedTask;
        };

        return connection;
    }

    private void RegisterRpcHandler(HubConnection connection, CancellationToken stoppingToken)
    {
        // The returned task becomes the RPC result sent back to the caller.
        connection.On<RpcRequest, RpcResponse>("RunRpc",
            request => ExecuteCommandAsync(request, stoppingToken));
        connection.On<string>(RpcInterruptPolicy.CancelMethod, InterruptCommand);
    }

    /// <summary>
    /// The control plane's Stop. Only read-only checks are interrupted; a repair that already
    /// started keeps running to its end whatever the caller asks.
    /// </summary>
    private void InterruptCommand(string requestId)
    {
        if (!_running.TryGetValue(requestId, out var running)
            || !RpcInterruptPolicy.CanInterrupt(running.Method))
            return;
        running.Interrupted = true;
        try { running.Source.Cancel(); }
        catch (ObjectDisposedException) { }
        _logger.LogInformation("Stopping read-only command {Method} with request id {RequestId}.",
            running.Method, requestId);
    }

    private sealed class RunningCommand(string method, CancellationTokenSource source)
    {
        public string Method { get; } = method;
        public CancellationTokenSource Source { get; } = source;
        public volatile bool Interrupted;
    }

    private async Task<RpcResponse> ExecuteCommandAsync(RpcRequest request,
        CancellationToken stoppingToken)
    {
        if (_runtime.IsPaused)
        {
            _logger.LogWarning("Rejected command {Method} with request id {RequestId}: functions paused.",
                request.Method, request.Id);
            return RpcResponse.Failure(request.Id, 503,
                "Endpoint functions are paused by a local administrator.");
        }

        // Do not queue unlimited work: return an explicit busy response immediately.
        if (!await _commandSlots.WaitAsync(TimeSpan.Zero, stoppingToken))
        {
            _logger.LogWarning(
                "Rejected command {Method} with request id {RequestId}: concurrency limit reached.",
                request.Method, request.Id);
            return RpcResponse.Failure(request.Id, 429,
                "The endpoint agent is busy. Try again later.");
        }

        try
        {
            _runtime.BeginCommand(request.Method);
            _logger.LogInformation("Received command {Method} with request id {RequestId}",
                request.Method, request.Id);

            // Link command cancellation to service shutdown while retaining an
            // independent per-command timeout.
            var context = new CommandExecutionContext(_agentId, null, null, DateTimeOffset.UtcNow);
            using var commandTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            commandTimeout.CancelAfter(TimeSpan.FromSeconds(_options.CommandTimeoutSeconds));
            var running = new RunningCommand(request.Method, commandTimeout);
            _running[request.Id] = running;

            RpcResponse response;
            try
            {
                response = await _dispatcher.DispatchAsync(request, context, commandTimeout.Token);
            }
            finally
            {
                _running.TryRemove(request.Id, out _);
            }
            // Distinguish a command timeout from normal service shutdown.
            if (running.Interrupted)
            {
                response = RpcResponse.Failure(request.Id, 499, "Command was cancelled.");
            }
            else if (commandTimeout.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                response = RpcResponse.Failure(request.Id, 408,
                    $"Command exceeded the {_options.CommandTimeoutSeconds}-second timeout.");
            }

            _runtime.CompleteCommand(request.Method, response.Error is null,
                response.Error is null ? null : $"Command.{response.Error.Code}",
                response.Error?.Message);
            return response;
        }
        catch (Exception ex)
        {
            _runtime.CompleteCommand(request.Method, false,
                "Command." + ex.GetType().Name, ex.Message);
            throw;
        }
        finally
        {
            _commandSlots.Release();
        }
    }

    private async Task RegisterAgentAsync(CancellationToken cancellationToken)
    {
        if (_connection is null) return;

        // These values let the control plane identify where this logical agent runs.
        var registration = new AgentRegistration(_agentId, Environment.MachineName,
            Environment.MachineName, Environment.UserName, _options.Version, _startedAtUtc);
        await _connection.InvokeAsync("RegisterAgent", registration, cancellationToken);
    }

    public async Task<SupportRequestReceipt> SubmitSupportRequestAsync(
        SupportRequestDraft draft, string userName, CancellationToken cancellationToken)
    {
        var connection = _connection;
        if (connection is null || connection.State != HubConnectionState.Connected)
        {
            return new SupportRequestReceipt(string.Empty, DateTimeOffset.UtcNow, false,
                "The endpoint is not connected to the control plane. Your message was not sent.");
        }

        var snapshot = _runtime.GetSnapshot();
        var submission = new SupportRequestSubmission(draft.ClientRequestId, draft.Subject,
            draft.MessageText, draft.TextDirection, snapshot.AgentId, snapshot.MachineName,
            userName, draft.SessionId, DateTimeOffset.UtcNow, snapshot, draft.LogExcerpts,
            draft.IbsngDiagnostics);
        try
        {
            return await connection.InvokeAsync<SupportRequestReceipt>("SubmitSupportRequest",
                submission, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _runtime.RecordProblem("Support." + ex.GetType().Name, ex.Message);
            _logger.LogWarning(ex, "Failed to submit a local helpdesk request.");
            return new SupportRequestReceipt(string.Empty, DateTimeOffset.UtcNow, false,
                "The control plane did not accept the support request. Try again after reconnecting.");
        }
    }

    public Task<ChatConversation> OpenChatAsync(ChatIdentity identity,
        CancellationToken cancellationToken) =>
        GetConnectedConnection().InvokeAsync<ChatConversation>(
            "OpenChat", identity, cancellationToken);

    public Task<IReadOnlyList<ChatConversationSummary>> ListChatsAsync(
        ChatConversationListRequest request, CancellationToken cancellationToken) =>
        GetConnectedConnection().InvokeAsync<IReadOnlyList<ChatConversationSummary>>(
            "ListChats", request, cancellationToken);

    public Task<ChatMessageReceipt> SendChatMessageAsync(ChatMessageSubmission submission,
        CancellationToken cancellationToken) =>
        GetConnectedConnection().InvokeAsync<ChatMessageReceipt>(
            "SendChatMessage", submission, cancellationToken);

    public Task<ChatDelta> GetChatDeltaAsync(ChatDeltaRequest request,
        CancellationToken cancellationToken) =>
        GetConnectedConnection().InvokeAsync<ChatDelta>(
            "GetChatDelta", request, cancellationToken);

    public Task<ChatConversationCloseReceipt> CloseChatAsync(
        ChatConversationCloseRequest request, CancellationToken cancellationToken) =>
        GetConnectedConnection().InvokeAsync<ChatConversationCloseReceipt>(
            "CloseChat", request, cancellationToken);

    public Task<ChatRunStopReceipt> StopChatRunAsync(
        ChatRunStopRequest request, CancellationToken cancellationToken) =>
        GetConnectedConnection().InvokeAsync<ChatRunStopReceipt>(
            "StopChatRun", request, cancellationToken);

    public Task<ChatActionDecisionReceipt> RespondToChatActionAsync(
        ChatActionDecision decision, CancellationToken cancellationToken) =>
        GetConnectedConnection().InvokeAsync<ChatActionDecisionReceipt>(
            "RespondToChatAction", decision, cancellationToken);

    private HubConnection GetConnectedConnection()
    {
        var connection = _connection;
        if (connection is null || connection.State != HubConnectionState.Connected)
            throw new InvalidOperationException(
                "The endpoint is not connected to the control plane.");
        return connection;
    }

    private async Task ShutdownConnectionAsync()
    {
        if (_connection is null) return;

        try
        {
            // Keep shutdown bounded so a stalled transport cannot prevent the
            // Windows service from stopping indefinitely.
            using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _connection.StopAsync(shutdownTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Timed out while stopping the control plane connection.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed while stopping the control plane connection.");
        }
        finally
        {
            await _connection.DisposeAsync();
            _connection = null;
            _runtime.SetConnection(AgentConnectionStatus.Disconnected);
            _logger.LogInformation("Control plane connection stopped.");
        }
    }

    /// <summary>Returns a randomized delay for the outer connection loop.</summary>
    private static TimeSpan GetRetryDelay() =>
        TimeSpan.FromMilliseconds(Random.Shared.Next(8_000, 12_001));
}
