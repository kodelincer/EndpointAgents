using EndpointAgents.Shared.Rpc;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Json;

namespace ControlPlane.Api.Tests;

public sealed class ApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;
    public ApiTests(WebApplicationFactory<Program> factory) => _client = factory.CreateClient();

    [Fact]
    public async Task ReturnsAgentCommandAndActivitySnapshots()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/agents")).StatusCode);
        var commands = await _client.GetFromJsonAsync<Command[]>("/api/commands");
        Assert.Contains(commands!, command => command.Method == "endpoint.get_info");
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/activity?limit=10")).StatusCode);
    }

    [Fact]
    public async Task RejectsUnknownCatalogMethod()
    {
        var response = await _client.PostAsJsonAsync("/api/agents/missing/commands",
            RpcRequest.Create("test", "endpoint.unknown"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ReturnsNotFoundAndRecordsOfflineCommand()
    {
        var response = await _client.PostAsJsonAsync("/api/agents/missing/commands",
            RpcRequest.Create("test", "endpoint.get_info"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var events = await _client.GetFromJsonAsync<Event[]>("/api/activity?agentId=missing&limit=10");
        Assert.Contains(events!, item => item.Type == "command.completed" && item.Status == "error");
    }

    private sealed record Command(string Method);
    private sealed record Event(string Type, string Status);
}
