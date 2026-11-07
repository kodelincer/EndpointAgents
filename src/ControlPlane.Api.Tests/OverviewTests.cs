using ControlPlane.Api.Actions;
using ControlPlane.Api.Chat;
using ControlPlane.Api.Dashboard;
using ControlPlane.Api.Security;
using EndpointAgents.Shared.Chat;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace ControlPlane.Api.Tests;

public sealed class OverviewTests(ControlPlaneApiFactory factory) : IClassFixture<ControlPlaneApiFactory>
{
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheOverviewCountsChatsAndFixesForSignedInMembersOnly()
    {
        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/overview", TestCancellation)).StatusCode);

        var chat = factory.Services.GetRequiredService<ChatStore>();
        var actions = factory.Services.GetRequiredService<ActionStore>();
        var agentId = Guid.NewGuid().ToString("D");
        var identity = new ChatIdentity("S-1-5-21-111-222-333-9100", "DOMAIN\\overview-user", 2);
        var conversation = await chat.OpenConversationAsync(agentId, identity, TestCancellation);
        await chat.SubmitMessageAsync(agentId, new ChatMessageSubmission(Guid.NewGuid().ToString("N"),
            conversation.ConversationId, "اینترنت قطع است", "rtl", identity), TestCancellation);
        Assert.True(await chat.PatchHelpdeskConversationAsync(conversation.ConversationId,
            new HelpdeskConversationPatch(HelpdeskStatus.Resolved, null, null), TestCancellation));
        var worked = Guid.NewGuid().ToString("N");
        await actions.StartRunAsync(worked, "printer.list", agentId, conversation.ConversationId, ActionRunSources.Agent, "{}", TestCancellation);
        await actions.CompleteRunAsync(worked, "Ok", "2 printers.", null, null, null, TestCancellation);
        var failed = Guid.NewGuid().ToString("N");
        await actions.StartRunAsync(failed, "spooler.restart_and_clear", agentId, conversation.ConversationId, ActionRunSources.Agent, "{}", TestCancellation);
        await actions.CompleteRunAsync(failed, "Failed", "Access denied.", null, null, null, TestCancellation);

        var member = await factory.CreateOperatorClientAsync(OperatorRoles.Member, TestCancellation);
        var overview = await member.GetFromJsonAsync<JsonObject>("/api/overview?days=7&utcOffsetMinutes=210", TestCancellation);
        Assert.NotNull(overview);
        Assert.Equal(7, overview["days"]!.GetValue<int>());
        var chats = overview["chats"]!;
        Assert.True(chats["started"]!.GetValue<int>() >= 1);
        Assert.True(chats["solved"]!.GetValue<int>() >= 1);
        Assert.Equal(7, chats["daily"]!.AsArray().Count);
        var today = chats["daily"]!.AsArray().Last()!;
        Assert.True(today["values"]!["solved"]!.GetValue<int>() >= 1);

        var operations = overview["operations"]!;
        Assert.True(operations["worked"]!.GetValue<int>() >= 1);
        Assert.True(operations["failed"]!.GetValue<int>() >= 1);
        Assert.Contains(operations["top"]!.AsArray(), item => item!["id"]!.GetValue<string>() == "spooler.restart_and_clear"
            && item["failed"]!.GetValue<int>() >= 1);
        Assert.Null(overview["problems"]);
        Assert.NotNull(overview["devices"]!["windows"]);
    }

    [Theory]
    [InlineData("{\"productName\":\"Windows 11 Pro\",\"build\":\"22631\"}", "win11")]
    [InlineData("{\"ProductName\":\"Windows 10 Pro\",\"Build\":\"19045\"}", "win10")]
    [InlineData("{\"productName\":\"Windows 10 Pro\"}", "win10")]
    [InlineData("{\"productName\":\"Windows 11 Enterprise\"}", "win11")]
    [InlineData("not json", "unknown")]
    [InlineData(null, "unknown")]
    public void WindowsFamilyComesFromTheBuildOrTheName(string? json, string expected) =>
        Assert.Equal(expected, OverviewStore.WindowsFamily(json));

    [Fact]
    public void EveryDayHasEverySeriesEvenWhenNothingHappened()
    {
        var days = OverviewStore.Daily(["2026-11-01", "2026-11-02"], ["worked", "failed"],
            [("2026-11-02", "failed"), ("2026-11-02", "failed"), ("2026-10-30", "worked")]);
        Assert.Equal(0, days[0].Values["worked"]);
        Assert.Equal(2, days[1].Values["failed"]);
    }
}
