using ControlPlane.Api.Chat;
using ControlPlane.Api.System;
using EndpointAgents.Shared.Agents;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;

namespace ControlPlane.Api.Tests;

/// <summary>Device identity cannot be borrowed, and the database keeps a daily copy.</summary>
public sealed class HardeningTests(ControlPlaneApiFactory factory) : IClassFixture<ControlPlaneApiFactory>
{
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RemoteCodeCannotTakeOverAnActiveDevice()
    {
        using var admin = await factory.CreateOperatorClientAsync(cancellationToken: TestCancellation);
        using var remote = factory.CreateRemoteClient();
        var agentId = Guid.NewGuid().ToString("D");
        var original = await RedeemAsync(admin, remote, agentId);
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        var originalToken = (await original.Content.ReadFromJsonAsync<EnrollmentRedemption>(TestCancellation))!
            .DeviceToken;

        // A second valid code asks for the same device id from the network.
        var secondCode = await IssueAsync(admin);
        var takeover = await remote.PostAsJsonAsync("/api/agent-enrollments/redeem",
            new EnrollmentRequest(secondCode, agentId, "other-machine"), TestCancellation);

        Assert.Equal(HttpStatusCode.Conflict, takeover.StatusCode);
        var store = factory.Services.GetRequiredService<ChatStore>();
        Assert.Equal(agentId, await store.ValidateDeviceTokenAsync(originalToken, TestCancellation));

        // The refused code was not used up, and works once the admin revoked the old device.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/devices/{agentId}/revoke",
            content: null, TestCancellation)).StatusCode);
        var again = await remote.PostAsJsonAsync("/api/agent-enrollments/redeem",
            new EnrollmentRequest(secondCode, agentId, "other-machine"), TestCancellation);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Null(await store.ValidateDeviceTokenAsync(originalToken, TestCancellation));
    }

    [Fact]
    public async Task TheServerItselfMayEnrollAgainOverAnActiveDevice()
    {
        using var admin = await factory.CreateOperatorClientAsync(cancellationToken: TestCancellation);
        using var local = factory.CreateClient();
        var agentId = Guid.NewGuid().ToString("D");
        Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(admin, local, agentId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(admin, local, agentId)).StatusCode);
    }

    [Fact]
    public async Task ConnectionWithoutTokenCannotRegisterAsAnEnrolledDevice()
    {
        using var admin = await factory.CreateOperatorClientAsync(cancellationToken: TestCancellation);
        using var remote = factory.CreateRemoteClient();
        var agentId = Guid.NewGuid().ToString("D");
        Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(admin, remote, agentId)).StatusCode);

        await using var impostor = Connect(token: null);
        await impostor.StartAsync(TestCancellation);
        await Assert.ThrowsAsync<HubException>(() => impostor.InvokeAsync("RegisterAgent",
            Registration(agentId.ToUpperInvariant()), TestCancellation));

        // Revoking does not open the door again: the id stays reserved for its token.
        await admin.PostAsync($"/api/devices/{agentId}/revoke", content: null, TestCancellation);
        await Assert.ThrowsAsync<HubException>(() => impostor.InvokeAsync("RegisterAgent",
            Registration(agentId), TestCancellation));
        Assert.DoesNotContain(factory.Services.GetRequiredService<ControlPlane.Api.Agents.AgentRegistry>()
            .GetOnlineAgents(), agent => agent.AgentId.Equals(agentId, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RegistrationTextIsShortenedAndCleaned()
    {
        var agentId = "legacy-" + Guid.NewGuid().ToString("N");
        await using var connection = Connect(token: null);
        await connection.StartAsync(TestCancellation);
        await connection.InvokeAsync("RegisterAgent", new AgentRegistration(agentId, "host",
            new string('m', 5_000) + "\u0007", "user", new string('9', 500), DateTimeOffset.UtcNow),
            TestCancellation);

        var online = Assert.Single(factory.Services.GetRequiredService<ControlPlane.Api.Agents.AgentRegistry>()
            .GetOnlineAgents(), agent => agent.AgentId == agentId);
        Assert.Equal(255, online.MachineName.Length);
        Assert.Equal(40, online.Version.Length);
    }

    [Fact]
    public async Task DailyBackupIsACompleteCopyAndOldCopiesAreRemoved()
    {
        var root = Path.Combine(Path.GetTempPath(), $"EndpointAgents-backup-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var database = Path.Combine(root, "control-plane.db");
            await using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                await connection.OpenAsync(TestCancellation);
                await using var create = connection.CreateCommand();
                create.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE devices(agent_id TEXT); INSERT INTO devices VALUES('pc-1');";
                await create.ExecuteNonQueryAsync(TestCancellation);
            }
            var clock = new TestClock(new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero));
            var worker = new DatabaseBackupWorker(
                Options.Create(new ChatOptions { DatabasePath = database }),
                Options.Create(new DatabaseBackupOptions { KeepCopies = 2 }),
                clock, NullLogger<DatabaseBackupWorker>.Instance);

            var first = await worker.BackUpIfDueAsync(TestCancellation);
            Assert.NotNull(first);
            Assert.Null(await worker.BackUpIfDueAsync(TestCancellation)); // once a day
            await using (var copy = new SqliteConnection($"Data Source={first};Mode=ReadOnly;Pooling=False"))
            {
                await copy.OpenAsync(TestCancellation);
                await using var read = copy.CreateCommand();
                read.CommandText = "SELECT agent_id FROM devices;";
                Assert.Equal("pc-1", await read.ExecuteScalarAsync(TestCancellation));
            }

            clock.Advance(TimeSpan.FromDays(1));
            Assert.NotNull(await worker.BackUpIfDueAsync(TestCancellation));
            clock.Advance(TimeSpan.FromDays(1));
            Assert.NotNull(await worker.BackUpIfDueAsync(TestCancellation));

            var kept = Directory.GetFiles(worker.BackupDirectory).Select(file => Path.GetFileName(file)!).Order().ToArray();
            Assert.Equal(["control-plane-20261102.db", "control-plane-20261103.db"], kept);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(TimeSpan by) => _now += by;
    }

    private HubConnection Connect(string? token) => new HubConnectionBuilder()
        .WithUrl(new Uri(factory.Server.BaseAddress, "agent-hub"), options =>
        {
            options.Transports = HttpTransportType.LongPolling;
            options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            if (token is not null) options.AccessTokenProvider = () => Task.FromResult<string?>(token);
        })
        .Build();

    private static AgentRegistration Registration(string agentId) =>
        new(agentId, "test-host", "test-machine", "tester", "1.0", DateTimeOffset.UtcNow);

    private static async Task<string> IssueAsync(HttpClient admin) =>
        (await (await admin.PostAsync("/api/agent-enrollments", content: null, TestCancellation))
            .Content.ReadFromJsonAsync<EnrollmentIssue>(TestCancellation))!.EnrollmentCode;

    private static async Task<HttpResponseMessage> RedeemAsync(HttpClient admin, HttpClient caller, string agentId) =>
        await caller.PostAsJsonAsync("/api/agent-enrollments/redeem",
            new EnrollmentRequest(await IssueAsync(admin), agentId, "test-machine"), TestCancellation);
}
