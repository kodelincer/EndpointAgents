using EndpointAgents.Core.Commands;
using EndpointAgents.Shared.Agents;
using EndpointAgents.Shared.Rpc;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

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
    private readonly string _agentId;
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private HubConnection? _connection;

    public AgentWorker(ILogger<AgentWorker> logger, CommandDispatcher dispatcher,
        IOptions<ControlPlaneOptions> options, AgentIdentityProvider identityProvider)
    {
        _logger = logger;
        _dispatcher = dispatcher;
        _options = options.Value;
        _agentId = identityProvider.GetOrCreate(_options.AgentId);
        _commandSlots = new SemaphoreSlim(_options.MaxConcurrentCommands, _options.MaxConcurrentCommands);
    }

    /// <summary>
    /// Creates the connection, registers the RPC callback, and keeps the worker
    /// connected until the host requests shutdown.
    /// </summary>
    /// <param name="stoppingToken">Raised by the host when the service is stopping.</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _connection = CreateConnection(stoppingToken);
        RegisterRpcHandler(_connection, stoppingToken);

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
                        _logger.LogInformation("Connecting agent {AgentId} to control plane: {HubUrl}",
                            _agentId, _options.HubUrl);
                        await _connection.StartAsync(stoppingToken);
                        await RegisterAgentAsync(stoppingToken);
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
            .WithUrl(_options.HubUrl)
            .WithAutomaticReconnect(new JitteredRetryPolicy())
            .Build();

        connection.Reconnecting += exception =>
        {
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
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal service shutdown.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to register the agent after reconnecting.");
            }
        };

        connection.Closed += exception =>
        {
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
    }

    private async Task<RpcResponse> ExecuteCommandAsync(RpcRequest request,
        CancellationToken stoppingToken)
    {
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
            _logger.LogInformation("Received command {Method} with request id {RequestId}",
                request.Method, request.Id);

            // Link command cancellation to service shutdown while retaining an
            // independent per-command timeout.
            var context = new CommandExecutionContext(_agentId, null, null, DateTimeOffset.UtcNow);
            using var commandTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            commandTimeout.CancelAfter(TimeSpan.FromSeconds(_options.CommandTimeoutSeconds));

            var response = await _dispatcher.DispatchAsync(request, context, commandTimeout.Token);
            // Distinguish a command timeout from normal service shutdown.
            if (commandTimeout.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                return RpcResponse.Failure(request.Id, 408,
                    $"Command exceeded the {_options.CommandTimeoutSeconds}-second timeout.");
            }

            return response;
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
            _logger.LogInformation("Control plane connection stopped.");
        }
    }

    /// <summary>Returns a randomized delay for the outer connection loop.</summary>
    private static TimeSpan GetRetryDelay() =>
        TimeSpan.FromMilliseconds(Random.Shared.Next(8_000, 12_001));
}
