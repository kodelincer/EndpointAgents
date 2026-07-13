using ControlPlane.Api.System;
using EndpointAgents.Shared.Rpc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;

namespace ControlPlane.Api.Tests;

public sealed class ApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    private readonly ControlPlaneRuntimeState _runtimeState;
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    public ApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
        _runtimeState = factory.Services.GetRequiredService<ControlPlaneRuntimeState>();
        _runtimeState.SetMaintenanceMode(false);
    }

    [Fact]
    public async Task ReturnsAgentCommandAndActivitySnapshots()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/agents", TestCancellation)).StatusCode);
        var commands = await _client.GetFromJsonAsync<Command[]>("/api/commands", TestCancellation);
        Assert.Contains(commands!, command => command.Method == "endpoint.get_info");
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/activity?limit=10", TestCancellation)).StatusCode);
    }

    [Fact]
    public async Task RejectsUnknownCatalogMethod()
    {
        var response = await _client.PostAsJsonAsync("/api/agents/missing/commands",
            RpcRequest.Create("test", "endpoint.unknown"), TestCancellation);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ReturnsNotFoundAndRecordsOfflineCommand()
    {
        var response = await _client.PostAsJsonAsync("/api/agents/missing/commands",
            RpcRequest.Create("test", "endpoint.get_info"), TestCancellation);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var events = await _client.GetFromJsonAsync<Event[]>("/api/activity?agentId=missing&limit=10", TestCancellation);
        Assert.Contains(events!, item => item.Type == "command.completed" && item.Status == "error");
    }

    [Fact]
    public async Task ExposesLiveReadyAndSystemStatus()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health/live", TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health/ready", TestCancellation)).StatusCode);
        var status = await _client.GetFromJsonAsync<SystemStatus>("/api/system/status", TestCancellation);
        Assert.NotNull(status);
        Assert.False(status.MaintenanceMode);
        Assert.True(status.UptimeSeconds >= 0);
    }

    [Fact]
    public async Task MaintenanceMakesApiUnreadyAndRejectsCommands()
    {
        _runtimeState.SetMaintenanceMode(true);
        try
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable,
                (await _client.GetAsync("/health/ready", TestCancellation)).StatusCode);
            var response = await _client.PostAsJsonAsync("/api/agents/test/commands",
                RpcRequest.Create("maintenance-test", "endpoint.get_info"), TestCancellation);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            _runtimeState.SetMaintenanceMode(false);
        }
    }

    [Fact]
    public async Task MaintenanceMutationRejectsRequestsWithoutLoopbackPeer()
    {
        var response = await _client.PostAsJsonAsync("/api/system/maintenance",
            new { enabled = true }, TestCancellation);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private sealed record Command(string Method);
    private sealed record Event(string Type, string Status);
    private sealed record SystemStatus(bool MaintenanceMode, long UptimeSeconds);
}
