using ControlPlane.Api.Support;
using EndpointAgents.Shared.Support;

namespace ControlPlane.Api.Tests;

public sealed class SupportRequestStoreTests
{
    [Fact]
    public void KeepsNewestTwoHundredRequests()
    {
        var store = new SupportRequestStore();
        for (var index = 0; index < 210; index++)
            Assert.True(store.TryAdd(Request(index.ToString(), $"agent-{index}"), out _));

        var requests = store.Get(200, null);
        Assert.Equal(200, requests.Count);
        Assert.Equal("10", requests[0].RequestId);
        Assert.Equal("209", requests[^1].RequestId);
    }

    [Fact]
    public void RateLimitsRepeatedRequestsFromSameAgent()
    {
        var store = new SupportRequestStore();
        Assert.True(store.TryAdd(Request("one", "agent-1"), out _));
        Assert.False(store.TryAdd(Request("two", "agent-1"), out var rejection));
        Assert.Contains("few seconds", rejection);
    }

    private static SupportRequest Request(string id, string agentId)
    {
        var now = DateTimeOffset.UtcNow;
        var runtime = new AgentRuntimeSnapshot(agentId, "pc", "1.0",
            AgentConnectionStatus.Connected, AgentFunctionMode.Active, now, now,
            null, null, null, 0, null, null, null);
        return new SupportRequest(id, id, now, "Subject", "Message", "ltr",
            agentId, "pc", "DOMAIN\\user", 1, runtime, []);
    }
}

