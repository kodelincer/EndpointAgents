using EndpointAgents.Platform.Windows.Ibsng;
using EndpointAgents.Service;
using EndpointAgents.Shared.LocalIpc;
using EndpointAgents.Tray;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EndpointAgents.Shared.Windows;

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
        var client = new TrayCommandClient(Options.Create(new IbsngOptions
        {
            TrayConnectTimeoutSeconds = 1,
            TrayResponseTimeoutSeconds = 1
        }), new TrayChannelRegistry());
        var request = new TrayIpcRequest("missing", TrayIpcProtocol.LaunchIbsngMethod,
            @"C:\missing.exe", new string('A', 64), "192.168.10.4");

        var response = await client.SendAsync(987654, request,
            TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Equal("TrayUnavailable", response.State);
    }

    [Fact]
    public async Task AttachedTrayAnswersOverTheServiceConnection()
    {
        await using var harness = await ServiceHarness.StartAsync(new FakeConsent(true));
        var request = new TrayIpcRequest("unknown", "unknown.method", @"C:\missing.exe",
            new string('A', 64), null);

        var response = await harness.Client.SendAsync(harness.SessionId, request,
            TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Equal("UnknownMethod", response.State);
        Assert.Equal("unknown", response.Id);
    }

    [Fact]
    public async Task ConsentTasksAskThroughTheTrayPrompt()
    {
        var consent = new FakeConsent(false);
        await using var harness = await ServiceHarness.StartAsync(consent);
        var request = new TrayIpcRequest("consent", TrayIpcProtocol.WindowsBugfixConsentMethod,
            string.Empty, string.Empty, null,
            WindowsBugfixConsent: new WindowsBugfixConsentRequest(
                WindowsRemediationPaths.ArtifactId, "Repair", "Fixes activation."));

        var response = await harness.Client.SendAsync(harness.SessionId, request,
            TestContext.Current.CancellationToken);

        Assert.Equal("UserDeclined", response.State);
        Assert.Equal("windows.bugfix", Assert.Single(consent.Asked).Kind);
    }

    [Fact]
    public async Task DisconnectedTrayFailsPendingTaskImmediately()
    {
        var consent = new FakeConsent(null);
        await using var harness = await ServiceHarness.StartAsync(consent, responseSeconds: 60);
        var request = new TrayIpcRequest("pending", TrayIpcProtocol.WindowsBugfixConsentMethod,
            string.Empty, string.Empty, null,
            WindowsBugfixConsent: new WindowsBugfixConsentRequest(
                WindowsRemediationPaths.ArtifactId, "Repair", "Fixes activation."));

        var sending = harness.Client.SendAsync(harness.SessionId, request,
            TestContext.Current.CancellationToken);
        await consent.WaitUntilAskedAsync();
        harness.StopTray();
        var response = await sending.WaitAsync(TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Equal("TrayUnavailable", response.State);
    }

    [Fact]
    public async Task TrayRejectsMalformedTaskFromService()
    {
        var pipeName = $"EndpointAgents.Tests.{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var shutdown = new CancellationTokenSource();
        var tray = new TrayServiceChannel(new TrayTaskDispatcher(new FakeConsent(true)), pipeName)
            .RunAsync(shutdown.Token);
        await server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true)
            { AutoFlush = true };
        var attach = JsonSerializer.Deserialize<ServiceIpcRequest>(
            (await reader.ReadLineAsync(TestContext.Current.CancellationToken))!)!;
        await writer.WriteLineAsync(JsonSerializer.Serialize(
            new ServiceIpcResponse(attach.Id, true, "Attached")).AsMemory(),
            TestContext.Current.CancellationToken);

        await writer.WriteLineAsync("{invalid-json".AsMemory(), TestContext.Current.CancellationToken);
        var response = JsonSerializer.Deserialize<TrayIpcResponse>(
            (await reader.ReadLineAsync(TestContext.Current.CancellationToken))!);
        shutdown.Cancel();
        await tray;

        Assert.Equal(ServiceIpcProtocol.AttachTrayMethod, attach.Method);
        Assert.NotNull(response);
        Assert.False(response.Success);
        Assert.Equal("InvalidRequest", response.State);
    }

    [Fact]
    public void AttachPolicyRefusesSessionZeroAndOtherUsers()
    {
        var sessionId = Process.GetCurrentProcess().SessionId;
        using var self = System.Security.Principal.WindowsIdentity.GetCurrent();

        Assert.NotNull(TrayAttachPolicy.Check(new(0, self.User!.Value, self.Name)));
        Assert.NotNull(TrayAttachPolicy.Check(new(sessionId, "S-1-5-21-1-2-3-500", "other")));
        Assert.Null(TrayAttachPolicy.Check(new(sessionId, self.User!.Value, self.Name)));
    }

    /// <summary>Plays the service side of the attach handshake with the real policy and channel.</summary>
    private sealed class ServiceHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _trayShutdown = new();
        private readonly CancellationTokenSource _serviceShutdown = new();
        private Task _tray = Task.CompletedTask;
        private Task _service = Task.CompletedTask;

        public required TrayCommandClient Client { get; init; }
        public int SessionId { get; private set; }

        public static async Task<ServiceHarness> StartAsync(IUserConsentPrompt consent,
            int responseSeconds = 5)
        {
            var registry = new TrayChannelRegistry();
            var harness = new ServiceHarness
            {
                Client = new TrayCommandClient(Options.Create(new IbsngOptions
                {
                    TrayConnectTimeoutSeconds = 5,
                    TrayResponseTimeoutSeconds = responseSeconds
                }), registry)
            };
            var pipeName = $"EndpointAgents.Tests.{Guid.NewGuid():N}";
            var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            harness._tray = new TrayServiceChannel(new TrayTaskDispatcher(consent), pipeName)
                .RunAsync(harness._trayShutdown.Token);
            await server.WaitForConnectionAsync(TestContext.Current.CancellationToken);

            var caller = TrayAttachPolicy.Identify(server);
            Assert.Null(TrayAttachPolicy.Check(caller));
            harness.SessionId = caller.SessionId;
            var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
            var attach = JsonSerializer.Deserialize<ServiceIpcRequest>(
                (await LineFraming.ReadLineAsync(reader, 4096,
                    TestContext.Current.CancellationToken))!)!;
            Assert.Equal(ServiceIpcProtocol.AttachTrayMethod, attach.Method);
            var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true)
                { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(
                new ServiceIpcResponse(attach.Id, true, "Attached")).AsMemory(),
                TestContext.Current.CancellationToken);

            var channel = new TrayChannel(server, caller.SessionId, caller.UserSid, caller.UserName);
            registry.Register(channel);
            harness._service = channel.RunAsync(harness._serviceShutdown.Token);
            return harness;
        }

        public void StopTray() => _trayShutdown.Cancel();

        public async ValueTask DisposeAsync()
        {
            _trayShutdown.Cancel();
            _serviceShutdown.Cancel();
            await Task.WhenAll(_tray, _service).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class FakeConsent(bool? answer) : IUserConsentPrompt
    {
        private readonly TaskCompletionSource _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<UserConsentPrompt> Asked { get; } = [];

        public Task WaitUntilAskedAsync() => _asked.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public async Task<bool> ConfirmAsync(UserConsentPrompt prompt, CancellationToken cancellationToken)
        {
            Asked.Add(prompt);
            _asked.TrySetResult();
            if (answer is { } value) return value;
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return false;
        }
    }
}
