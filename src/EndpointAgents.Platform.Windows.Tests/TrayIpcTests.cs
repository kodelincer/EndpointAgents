using EndpointAgents.Platform.Windows.Ibsng;
using EndpointAgents.Service;
using EndpointAgents.Shared.LocalIpc;
using EndpointAgents.Tray;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;

namespace EndpointAgents.Platform.Windows.Tests;

public sealed class TrayIpcTests
{
    [Fact]
    public async Task LauncherRecognizesAlreadyRunningApprovedProcess()
    {
        var path = Environment.ProcessPath!;
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream,
            TestContext.Current.CancellationToken));
        var request = new TrayIpcRequest("running", TrayIpcProtocol.LaunchIbsngMethod,
            path, hash, "192.168.10.4");

        var response = await new IbsngDialerLauncher().LaunchAsync(request,
            TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        Assert.Equal("AlreadyRunning", response.State);
        Assert.Equal(Environment.ProcessId, response.ProcessId);
    }

    [Fact]
    public async Task ReconcilerKeepsOneApprovedCanonicalProcess()
    {
        var path = Environment.ProcessPath!;
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream,
            TestContext.Current.CancellationToken));
        var request = new TrayIpcRequest("reconcile", TrayIpcProtocol.ReconcileIbsngMethod,
            path, hash, null, Path.GetFileName(path));

        var response = await new IbsngDialerLauncher().LaunchAsync(request,
            TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        Assert.Equal("ProcessesReconciled", response.State);
        Assert.Equal(Environment.ProcessId, response.ProcessId);
        Assert.Empty(response.StoppedProcessIds ?? []);
    }

    [Fact]
    public async Task InspectorReportsCanonicalProcessWithoutChangingIt()
    {
        var path = Environment.ProcessPath!;
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream,
            TestContext.Current.CancellationToken));
        var request = new TrayIpcRequest("inspect", TrayIpcProtocol.InspectIbsngMethod,
            path, hash, null, Path.GetFileName(path));

        var response = await new IbsngDialerLauncher().LaunchAsync(request,
            TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        Assert.Equal("ProcessesInspected", response.State);
        Assert.Equal(Environment.ProcessId, response.ProcessId);
        Assert.Contains(Environment.ProcessId, response.CandidateProcessIds ?? []);
        Assert.Empty(response.StoppedProcessIds ?? []);
    }

    [Fact]
    public async Task MissingTrayReturnsExplicitUnavailableState()
    {
        var prefix = $"EndpointAgents.Tests.{Guid.NewGuid():N}";
        var client = new TrayCommandClient(Options.Create(new IbsngOptions
        {
            TrayPipePrefix = prefix,
            TrayConnectTimeoutSeconds = 1,
            TrayResponseTimeoutSeconds = 1
        }));
        var request = new TrayIpcRequest("missing", TrayIpcProtocol.LaunchIbsngMethod,
            @"C:\missing.exe", new string('A', 64), "192.168.10.4");

        var response = await client.SendAsync(987654, request,
            TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Equal("TrayUnavailable", response.State);
    }

    [Fact]
    public async Task SecuredPipeReturnsStructuredResponse()
    {
        var sessionId = Process.GetCurrentProcess().SessionId;
        var prefix = $"EndpointAgents.Tests.{Guid.NewGuid():N}";
        using var shutdown = new CancellationTokenSource();
        var serverTask = new TrayIpcServer(sessionId, prefix).RunAsync(shutdown.Token);
        var client = new TrayCommandClient(Options.Create(new IbsngOptions
        {
            TrayPipePrefix = prefix,
            TrayConnectTimeoutSeconds = 3,
            TrayResponseTimeoutSeconds = 3
        }));
        var request = new TrayIpcRequest("unknown", "unknown.method", @"C:\missing.exe",
            new string('A', 64), null);

        var response = await client.SendAsync(sessionId, request,
            TestContext.Current.CancellationToken);
        shutdown.Cancel();
        await serverTask;

        Assert.False(response.Success);
        Assert.Equal("UnknownMethod", response.State);
    }

    [Fact]
    public async Task SecuredPipeRejectsMalformedMessage()
    {
        var sessionId = Process.GetCurrentProcess().SessionId;
        var prefix = $"EndpointAgents.Tests.{Guid.NewGuid():N}";
        using var shutdown = new CancellationTokenSource();
        var serverTask = new TrayIpcServer(sessionId, prefix).RunAsync(shutdown.Token);
        var pipeName = TrayIpcProtocol.GetPipeName(prefix, sessionId);
        await using var pipe = new NamedPipeClientStream(".", pipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(TestContext.Current.CancellationToken);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, leaveOpen: true);

        await writer.WriteLineAsync("{invalid-json".AsMemory(),
            TestContext.Current.CancellationToken);
        var line = await reader.ReadLineAsync(TestContext.Current.CancellationToken);
        var response = JsonSerializer.Deserialize<TrayIpcResponse>(line!);
        shutdown.Cancel();
        await serverTask;

        Assert.NotNull(response);
        Assert.False(response.Success);
        Assert.Equal("InvalidRequest", response.State);
    }
}
