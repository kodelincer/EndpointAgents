using ControlPlane.Api.Chat;
using ControlPlane.Api.Hermes;
using ControlPlane.Api.Security;
using EndpointAgents.Shared.Chat;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;

namespace ControlPlane.Api.Tests;

public sealed class HermesQueueTests : IClassFixture<ControlPlaneApiFactory>, IAsyncLifetime
{
    private readonly ControlPlaneApiFactory _factory;
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        $"EndpointAgents-queue-{Guid.NewGuid():N}");
    private ChatStore _store = null!;
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    public HermesQueueTests(ControlPlaneApiFactory factory) => _factory = factory;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _store = new ChatStore(Options.Create(new ChatOptions
        {
            DatabasePath = Path.Combine(_root, "chat.db")
        }));
        await _store.InitializeAsync(TestCancellation);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task RefusesWhenFullOrUserAlreadyWaitsAndReportsPositions()
    {
        var limits = new ChatQueueLimits(MaxQueueLength: 2);
        var alice = await OpenAsync(1);
        var bob = await OpenAsync(2);
        var carol = await OpenAsync(3);

        Assert.True((await SendAsync(alice, "first", limits)).Accepted);
        var second = await SendAsync(alice, "second", limits);
        Assert.False(second.Accepted);
        Assert.Equal(ChatStore.AlreadyWaitingMessage, second.Message);
        Assert.True((await SendAsync(bob, "bob", limits)).Accepted);
        var full = await SendAsync(carol, "carol", limits);
        Assert.False(full.Accepted);
        Assert.Equal(ChatStore.QueueFullMessage, full.Message);

        Assert.Equal(1, (await DeltaAsync(alice)).QueuePosition);
        Assert.Equal(2, (await DeltaAsync(bob)).QueuePosition);
        Assert.Null((await DeltaAsync(carol)).QueuePosition);
        Assert.Equal(new HermesQueueSnapshot(0, 2, null),
            (await _store.GetQueueSnapshotAsync(TestCancellation)) with
            {
                OldestWaitingSinceUtc = null
            });

        var lease = Assert.IsType<ChatRunLease>(await _store.LeaseNextRunAsync(
            5, TimeSpan.FromMinutes(5), TestCancellation));
        Assert.Equal(alice.Conversation, lease.ConversationId);
        Assert.Null((await DeltaAsync(alice)).QueuePosition);
        Assert.Equal(1, (await DeltaAsync(bob)).QueuePosition);
        Assert.True((await SendAsync(alice, "follow-up while running", limits)).Accepted);
        Assert.False((await SendAsync(carol, "carol again", limits)).Accepted);
    }

    [Fact]
    public async Task AnswersRequestsThatWaitedTooLongAsBusy()
    {
        var user = await OpenAsync(4);
        Assert.True((await SendAsync(user, "slow day", null)).Accepted);

        Assert.Equal(0, await _store.ExpireWaitingRunsAsync(
            DateTimeOffset.UtcNow.AddMinutes(-10), TestCancellation));
        Assert.Equal(1, await _store.ExpireWaitingRunsAsync(
            DateTimeOffset.UtcNow.AddMinutes(1), TestCancellation));

        var delta = await DeltaAsync(user);
        Assert.Null(delta.QueuePosition);
        Assert.Equal(ChatMessageState.Failed, delta.Messages[0].State);
        Assert.Equal(ChatStore.QueueExpiredMessage, delta.Messages[^1].Body);
        Assert.Null(await _store.LeaseNextRunAsync(5, TimeSpan.FromMinutes(5),
            TestCancellation));
    }

    [Fact]
    public async Task SettingsSurviveReloadAndRejectOutOfRangeValues()
    {
        var options = Options.Create(new HermesOptions { MaxConcurrentRuns = 3 });
        var queue = new HermesQueue(_store, options);
        await queue.LoadAsync(TestCancellation);
        Assert.Equal(new HermesQueueSettings(3, 50, 10), queue.Current);

        Assert.NotNull(await queue.SaveAsync(new HermesQueueSettings(0, 50, 10),
            TestCancellation));
        Assert.Null(await queue.SaveAsync(new HermesQueueSettings(6, 20, 5),
            TestCancellation));

        var reloaded = new HermesQueue(_store, options);
        await reloaded.LoadAsync(TestCancellation);
        Assert.Equal(new HermesQueueSettings(6, 20, 5), reloaded.Current);
    }

    [Fact]
    public async Task DashboardReadsAndUpdatesQueueSettings()
    {
        // Only admins read or change the queue limits.
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(
            "/api/settings/hermes-queue", TestCancellation)).StatusCode);
        using var reviewer = await _factory.CreateOperatorClientAsync(OperatorRoles.Reviewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await reviewer.PutAsJsonAsync("/api/settings/hermes-queue",
            new HermesQueueSettings(4, 50, 10), TestCancellation)).StatusCode);

        using var client = await _factory.CreateOperatorClientAsync();
        var status = await client.GetFromJsonAsync<HermesQueueStatus>(
            "/api/settings/hermes-queue", TestCancellation);
        Assert.NotNull(status);

        var bad = await client.PutAsJsonAsync("/api/settings/hermes-queue",
            new HermesQueueSettings(4, 0, 10), TestCancellation);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var saved = await client.PutAsJsonAsync("/api/settings/hermes-queue",
            status!.Settings with { MaxQueueLength = 75 }, TestCancellation);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(75, (await saved.Content.ReadFromJsonAsync<HermesQueueStatus>(
            TestCancellation))!.Settings.MaxQueueLength);
    }

    private async Task<(string Agent, string Conversation, ChatIdentity Identity)> OpenAsync(
        int user)
    {
        var agentId = Guid.NewGuid().ToString("D");
        var identity = new ChatIdentity($"S-1-5-21-700-800-900-{1000 + user}",
            $"DOMAIN\\queue-user-{user}", user);
        var conversation = await _store.OpenConversationAsync(agentId, identity,
            TestCancellation);
        return (agentId, conversation.ConversationId, identity);
    }

    private Task<ChatMessageReceipt> SendAsync(
        (string Agent, string Conversation, ChatIdentity Identity) user, string text,
        ChatQueueLimits? limits) =>
        _store.SubmitMessageAsync(user.Agent, new ChatMessageSubmission(
            Guid.NewGuid().ToString("N"), user.Conversation, text, "ltr", user.Identity),
            TestCancellation, limits);

    private async Task<ChatDelta> DeltaAsync(
        (string Agent, string Conversation, ChatIdentity Identity) user) =>
        Assert.IsType<ChatDelta>(await _store.GetDeltaAsync(user.Agent,
            new ChatDeltaRequest(user.Conversation, 0, user.Identity), TestCancellation));
}
