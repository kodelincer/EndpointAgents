using EndpointAgents.Platform.Windows.Actions;
using EndpointAgents.Platform.Windows.Actions.Infrastructure;
using EndpointAgents.Shared.Actions;

namespace EndpointAgents.Platform.Windows.Tests;

public sealed class EndpointActionInfrastructureTests
{
    [Fact]
    public void DnsQueryAndAnswerRoundTrip()
    {
        var query = NetworkProbes.BuildQuery(0x1234, "dc01.corp.local");
        Assert.Equal(0x12, query[0]);
        Assert.Equal(0x34, query[1]);

        // Reply: same header with QR set, one answer that points back to the question name.
        var reply = query.ToList();
        reply[2] = 0x81; reply[3] = 0x80;
        reply[6] = 0; reply[7] = 1;
        reply.AddRange([0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 10, 1, 2, 3]);
        var answer = NetworkProbes.ParseAnswer("10.0.0.1", 0x1234, reply.ToArray());
        Assert.True(answer.Answered);
        Assert.Equal(["10.1.2.3"], answer.Addresses);

        var notFound = query.ToArray();
        notFound[2] = 0x81; notFound[3] = 0x83;
        Assert.Equal("NameNotFound", NetworkProbes.ParseAnswer("x", 0x1234, notFound).Error);
        Assert.Equal("BadReply", NetworkProbes.ParseAnswer("x", 0x9999, notFound).Error);
    }

    [Fact]
    public void EverySystemAndUserActionIsCataloguedOnce()
    {
        var system = EndpointActionSet.CreateSystemActions().Select(action => action.Id).ToArray();
        var user = EndpointActionSet.CreateUserActions().Select(action => action.Id).ToArray();
        Assert.Equal(system.Length, system.Distinct().Count());
        Assert.Empty(system.Intersect(user));
        foreach (var id in system)
            Assert.Equal(ActionRunAs.System, EndpointActionCatalog.Find(id)?.RunAs);
        foreach (var id in user)
            Assert.Equal(ActionRunAs.User, EndpointActionCatalog.Find(id)?.RunAs);
        var runnable = system.Concat(user).ToHashSet();
        Assert.All(EndpointActionCatalog.All, item => Assert.Contains(item.Id, runnable));
    }
}
