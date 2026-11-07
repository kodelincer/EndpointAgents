using ControlPlane.Api.Actions;
using ControlPlane.Api.Chat;
using ControlPlane.Api.Inventory;
using ControlPlane.Api.Security;
using EndpointAgents.Shared.Actions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace ControlPlane.Api.Tests;

public sealed class InventoryStoreTests : IDisposable
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "inventory-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task FactsAreKeptUntilTheyAreTooOld()
    {
        var clock = new Clock(DateTimeOffset.Parse("2026-11-03T08:00:00Z"));
        var store = new InventoryStore(Options.Create(new ChatOptions { DatabasePath = Path.Combine(_root, "cp.db") }), clock);
        await store.InitializeAsync(TestCancellation);
        var hardware = InventoryKeys.System("hardware");
        var software = InventoryKeys.Action("software.inventory");
        await store.SaveAsync("a1", "PC-01", hardware, JsonNode.Parse("""{"memoryGb":16}"""), TestCancellation);
        await store.SaveAsync("a1", "PC-01", software, JsonNode.Parse("""{"count":3}"""), TestCancellation);
        // Network facts are never kept.
        await store.SaveAsync("a1", "PC-01", InventoryKeys.System("network"), JsonNode.Parse("{}"), TestCancellation);

        clock.Now = clock.Now.AddDays(2);
        Assert.NotNull(await store.GetFreshAsync("a1", hardware, TestCancellation));
        Assert.Null(await store.GetFreshAsync("a1", software, TestCancellation));
        clock.Now = clock.Now.AddDays(6);
        Assert.Null(await store.GetFreshAsync("a1", hardware, TestCancellation));

        Assert.Equal(2, (await store.GetDeviceAsync("a1", TestCancellation)).Count);
        var device = Assert.Single(await store.ListDevicesAsync(TestCancellation));
        Assert.Equal("PC-01", device.MachineName);
    }
}

public sealed class InventoryApiTests(ControlPlaneApiFactory factory) : IClassFixture<ControlPlaneApiFactory>
{
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheAgentGetsAFreshStoredAnswerInsteadOfAskingThePc()
    {
        _ = factory.CreateClient();
        var inventory = factory.Services.GetRequiredService<InventoryStore>();
        var runner = factory.Services.GetRequiredService<ActionRunner>();
        var agent = Guid.NewGuid().ToString("D");
        await inventory.SaveAsync(agent, "PC-07", InventoryKeys.Action("software.inventory"),
            JsonNode.Parse("""{"programs":[{"name":"7-Zip"}]}"""), TestCancellation);

        var fromAgent = await runner.RunAsync(new ActionRunContext(agent, null, null, ActionRunSources.Agent),
            "software.inventory", null, false, TestCancellation);
        Assert.Equal(ActionStatus.Ok, fromAgent.Status);
        Assert.Contains("not asked again", fromAgent.Summary);
        Assert.Equal("7-Zip", fromAgent.Data!["programs"]![0]!["name"]!.GetValue<string>());

        // The helpdesk always asks the PC itself (the test PC is offline, so it fails).
        var fromOperator = await runner.RunAsync(new ActionRunContext(agent, null, null, ActionRunSources.Operator),
            "software.inventory", null, false, TestCancellation);
        Assert.Equal(ActionStatus.Failed, fromOperator.Status);

        var member = await factory.CreateOperatorClientAsync(OperatorRoles.Member, TestCancellation);
        var devices = await member.GetFromJsonAsync<JsonArray>("/api/inventory", TestCancellation);
        Assert.Contains(devices!, item => item!["agentId"]!.GetValue<string>() == agent);
        var facts = await member.GetFromJsonAsync<JsonArray>($"/api/inventory/{agent}", TestCancellation);
        Assert.True(Assert.Single(facts!)!["fresh"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.BadRequest, (await member.PostAsync($"/api/inventory/{agent}/refresh", null, TestCancellation)).StatusCode);
    }
}
