using EndpointAgents.Service;
using EndpointAgents.Shared.Support;
using EndpointAgents.Tray;

namespace EndpointAgents.Platform.Windows.Tests;

public sealed class AgentRuntimeStateTests
{
    [Fact]
    public void TracksConnectionPauseAndCommandProblems()
    {
        var state = new AgentRuntimeState();
        state.Initialize("agent-1", "1.2.3");
        state.SetConnection(AgentConnectionStatus.Connected);
        state.SetPaused(true);
        state.BeginCommand("endpoint.get_info");
        state.CompleteCommand("endpoint.get_info", false, "Command.500", "Collection failed.");

        var snapshot = state.GetSnapshot();

        Assert.Equal(AgentConnectionStatus.Connected, snapshot.ConnectionStatus);
        Assert.Equal(AgentFunctionMode.Paused, snapshot.FunctionMode);
        Assert.Equal("endpoint.get_info", snapshot.LastCommandMethod);
        Assert.False(snapshot.LastCommandSucceeded);
        Assert.Equal("Command.500", snapshot.LastProblemCode);
        Assert.Equal(0, snapshot.ActiveCommands);
    }

    [Fact]
    public void SuccessfulCommandClearsAnEarlierCommandProblem()
    {
        var state = new AgentRuntimeState();
        state.Initialize("agent-1", "1.0");
        state.BeginCommand("first");
        state.CompleteCommand("first", false, "Command.500", "failed");
        state.BeginCommand("second");
        state.CompleteCommand("second", true);

        Assert.Null(state.GetSnapshot().LastProblemCode);
    }

    [Fact]
    public void ResolverKeepsPausedAgentConnectedButShowsPauseState()
    {
        var now = DateTimeOffset.UtcNow;
        var runtime = new AgentRuntimeSnapshot("agent-1", "pc-1", "1.0",
            AgentConnectionStatus.Connected, AgentFunctionMode.Paused, now, now,
            null, null, null, 0, null, null, null);

        var status = TrayStatusResolver.Resolve(EndpointServiceState.Running, runtime, 0, now);

        Assert.Equal(TrayHealthState.Paused, status.Health);
        Assert.Contains("متصل", status.Detail);
    }

    [Fact]
    public void ResolverShowsErrorWhenRunningServiceStatusPipeRepeatedlyFails()
    {
        var status = TrayStatusResolver.Resolve(EndpointServiceState.Running, null, 2,
            DateTimeOffset.UtcNow);

        Assert.Equal(TrayHealthState.Error, status.Health);
        Assert.Contains("کانال وضعیت", status.Detail);
    }

    [Fact]
    public void ResolverTreatsInteractiveServiceHostAsOperational()
    {
        var now = DateTimeOffset.UtcNow;
        var runtime = new AgentRuntimeSnapshot("agent-1", "pc-1", "1.0",
            AgentConnectionStatus.Connected, AgentFunctionMode.Active, now, now,
            null, null, null, 0, null, null, null);

        var status = TrayStatusResolver.Resolve(EndpointServiceState.Interactive,
            runtime, 0, now);

        Assert.Equal(TrayHealthState.Ready, status.Health);
        Assert.Same(runtime, status.Runtime);
    }

    [Theory]
    [InlineData(EndpointServiceState.NotInstalled)]
    [InlineData(EndpointServiceState.Stopped)]
    [InlineData(EndpointServiceState.Unknown)]
    public void IpcMakesNonManagedServiceStateInteractive(EndpointServiceState scmState)
    {
        var state = EndpointServiceStateResolver.ResolveObservedState(scmState,
            ipcAvailable: true);

        Assert.Equal(EndpointServiceState.Interactive, state);
        Assert.True(EndpointServiceStateResolver.IsOperational(state));
    }

    [Theory]
    [InlineData(EndpointServiceState.Running)]
    [InlineData(EndpointServiceState.StartPending)]
    [InlineData(EndpointServiceState.StopPending)]
    public void IpcDoesNotReplaceManagedOrPendingServiceState(EndpointServiceState scmState)
    {
        var state = EndpointServiceStateResolver.ResolveObservedState(scmState,
            ipcAvailable: true);

        Assert.Equal(scmState, state);
    }
}
