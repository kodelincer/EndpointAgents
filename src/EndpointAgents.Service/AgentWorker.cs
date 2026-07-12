using EndpointAgents.Core.Commands;
using EndpointAgents.Shared.Agents;
using EndpointAgents.Shared.Rpc;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;

namespace EndpointAgents.Service;

/*
 * This service now knows how to connect, reconnect, 
 * register itself, receive commands, run the dispatcher, 
 * and return JSON results.
 */

public sealed class AgentWorker : BackgroundService
{
    private readonly ILogger<AgentWorker> _logger;
    private readonly CommandDispatcher _dispatcher;
    private readonly ControlPlaneOptions _options;

    private HubConnection? _connection;

    public AgentWorker(
        ILogger<AgentWorker> logger,
        CommandDispatcher dispatcher,
        IOptions<ControlPlaneOptions> options)
    {
        _logger = logger;
        _dispatcher = dispatcher;
        _options = options.Value;

        if (string.IsNullOrWhiteSpace(_options.AgentId))
        {
            _options.AgentId = Environment.MachineName;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _connection = CreateConnection();

        RegisterRpcHandler(_connection, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_connection.State == HubConnectionState.Disconnected)
                {
                    _logger.LogInformation("Connecting to control plane: {HubUrl}", _options.HubUrl);

                    await _connection.StartAsync(stoppingToken);
                    await RegisterAgentAsync(stoppingToken);

                    _logger.LogInformation("Endpoint agent connected.");
                }

                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Control plane connection failed. Retrying soon.");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    private HubConnection CreateConnection()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(_options.HubUrl)
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.Zero,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(30)
            })
            .Build();

        connection.Reconnecting += exception =>
        {
            _logger.LogWarning(exception, "Connection to control plane lost. Reconnecting...");
            return Task.CompletedTask;
        };

        connection.Reconnected += async connectionId =>
        {
            _logger.LogInformation("Reconnected to control plane. ConnectionId: {ConnectionId}", connectionId);
            await RegisterAgentAsync(CancellationToken.None);
        };

        connection.Closed += exception =>
        {
            _logger.LogWarning(exception, "Connection to control plane closed.");
            return Task.CompletedTask;
        };

        return connection;
    }

    private void RegisterRpcHandler(HubConnection connection, CancellationToken stoppingToken)
    {
        connection.On<RpcRequest, RpcResponse>("RunRpc", async request =>
        {
            _logger.LogInformation(
                "Received command {Method} with request id {RequestId}",
                request.Method,
                request.Id);

            var context = new CommandExecutionContext(
                AgentId: _options.AgentId,
                RequestedBy: null,
                TicketId: null,
                ReceivedAtUtc: DateTimeOffset.UtcNow);

            return await _dispatcher.DispatchAsync(request, context, stoppingToken);
        });
    }

    private async Task RegisterAgentAsync(CancellationToken cancellationToken)
    {
        if (_connection is null)
        {
            return;
        }

        var registration = new AgentRegistration(
            AgentId: _options.AgentId,
            Hostname: Environment.MachineName,
            MachineName: Environment.MachineName,
            UserName: Environment.UserName,
            Version: _options.Version,
            StartedAtUtc: DateTimeOffset.UtcNow);

        await _connection.InvokeAsync("RegisterAgent", registration, cancellationToken);
    }
}
