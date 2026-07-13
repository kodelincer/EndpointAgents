using ControlPlane.WebHost.Management;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace ControlPlane.WebHost.Tests;

public sealed class ControlPlaneManagementServiceTests
{
    [Theory]
    [InlineData(ManagedServiceState.Stopped, ServiceAction.Start)]
    [InlineData(ManagedServiceState.Running, ServiceAction.EnterMaintenance)]
    [InlineData(ManagedServiceState.Running, ServiceAction.Stop)]
    [InlineData(ManagedServiceState.Running, ServiceAction.Restart)]
    public async Task ExposesActionsAllowedByCurrentState(ManagedServiceState state,
        ServiceAction expectedAction)
    {
        var service = Create(new FakeServiceController { State = state }, new FakeApiHandler());
        var status = await service.GetStatusAsync(CancellationToken.None);
        Assert.Contains(expectedAction, status.AvailableActions);
    }

    [Fact]
    public async Task StartTransitionsStoppedServiceToRunning()
    {
        var controller = new FakeServiceController { State = ManagedServiceState.Stopped };
        var result = await Create(controller, new FakeApiHandler())
            .ExecuteAsync(ServiceAction.Start, CancellationToken.None);
        Assert.True(result.Accepted);
        Assert.Equal(ManagedServiceState.Running, controller.State);
    }

    [Fact]
    public async Task MaintenanceChangesReadinessWithoutStoppingService()
    {
        var api = new FakeApiHandler();
        var service = Create(new FakeServiceController(), api);
        var entered = await service.ExecuteAsync(ServiceAction.EnterMaintenance, CancellationToken.None);
        Assert.True(entered.Accepted);
        Assert.Equal(ApiHealthState.Maintenance, entered.Status.ApiHealth);
        Assert.Equal(ManagedServiceState.Running, entered.Status.ServiceState);

        var resumed = await service.ExecuteAsync(ServiceAction.ExitMaintenance, CancellationToken.None);
        Assert.True(resumed.Accepted);
        Assert.Equal(ApiHealthState.Healthy, resumed.Status.ApiHealth);
    }

    [Fact]
    public async Task RejectsASecondActionWhileOneIsRunning()
    {
        var controller = new FakeServiceController { State = ManagedServiceState.Stopped };
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.StartBlocker = blocker.Task;
        var service = Create(controller, new FakeApiHandler());

        var first = service.ExecuteAsync(ServiceAction.Start, CancellationToken.None);
        await controller.StartCalled.Task;
        var second = await service.ExecuteAsync(ServiceAction.Start, CancellationToken.None);
        Assert.False(second.Accepted);
        Assert.Contains("already running", second.Message);

        blocker.SetResult();
        Assert.True((await first).Accepted);
    }

    [Fact]
    public async Task ReportsRunningServiceAsUnreachableWhenProbeFails()
    {
        var service = Create(new FakeServiceController(), new FakeApiHandler { Unreachable = true });
        var status = await service.GetStatusAsync(CancellationToken.None);
        Assert.Equal(ManagedServiceState.Running, status.ServiceState);
        Assert.Equal(ApiHealthState.Unreachable, status.ApiHealth);
    }

    private static ControlPlaneManagementService Create(FakeServiceController controller,
        FakeApiHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5052") };
        var factory = new FakeHttpClientFactory(client);
        var options = Options.Create(new ManagementOptions { ActionTimeoutSeconds = 5 });
        return new ControlPlaneManagementService(controller, factory, options,
            NullLogger<ControlPlaneManagementService>.Instance);
    }

    private sealed class FakeServiceController : IWindowsServiceController
    {
        public ManagedServiceState State { get; set; } = ManagedServiceState.Running;
        public Task? StartBlocker { get; set; }
        public TaskCompletionSource StartCalled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ManagedServiceState> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(State);

        public async Task StartAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            StartCalled.TrySetResult();
            if (StartBlocker is not null) await StartBlocker.WaitAsync(cancellationToken);
            State = ManagedServiceState.Running;
        }

        public Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            State = ManagedServiceState.Stopped;
            return Task.CompletedTask;
        }

        public async Task RestartAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            await StopAsync(timeout, cancellationToken);
            await StartAsync(timeout, cancellationToken);
        }
    }

    private sealed class FakeHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class FakeApiHandler : HttpMessageHandler
    {
        public bool Maintenance { get; private set; }
        public bool Unreachable { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Unreachable) throw new HttpRequestException("offline");
            var path = request.RequestUri?.AbsolutePath;
            if (path == "/api/system/maintenance")
            {
                var body = await request.Content!.ReadFromJsonAsync<MaintenanceBody>(cancellationToken);
                Maintenance = body?.Enabled == true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { maintenanceMode = Maintenance }) };
            }
            if (path == "/health/live") return new HttpResponseMessage(HttpStatusCode.OK);
            if (path == "/health/ready")
                return new HttpResponseMessage(Maintenance ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
            if (path == "/api/system/status")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new ApiSystemStatus("1.0.0", "test", DateTimeOffset.UtcNow,
                        60, Maintenance, 2, 4))
                };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private sealed record MaintenanceBody(bool Enabled);
    }
}
