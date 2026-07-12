using ControlPlane.Api.Dashboard;

namespace ControlPlane.Api.Tests;

public sealed class ActivityStoreTests
{
    [Fact]
    public void KeepsOnlyNewestFiveHundredEvents()
    {
        var store = new ActivityStore();
        for (var index = 0; index < 510; index++)
            store.Add(Event(index.ToString(), index % 2 == 0 ? "a" : "b"));

        var events = store.Get(500, null);
        Assert.Equal(500, events.Count);
        Assert.Equal("10", events[0].EventId);
        Assert.Equal("509", events[^1].EventId);
    }

    [Fact]
    public void FiltersByAgentAndHonorsLimit()
    {
        var store = new ActivityStore();
        store.Add(Event("1", "a")); store.Add(Event("2", "b")); store.Add(Event("3", "a"));
        Assert.Equal(["1", "3"], store.Get(2, "a").Select(item => item.EventId));
    }

    private static DashboardActivity Event(string id, string agentId) =>
        new(id, DashboardEventTypes.AgentConnected, DateTimeOffset.UtcNow, agentId, "online");
}
