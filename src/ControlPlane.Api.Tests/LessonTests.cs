using ControlPlane.Api.Actions;
using ControlPlane.Api.Chat;
using ControlPlane.Api.Lessons;
using ControlPlane.Api.Security;
using EndpointAgents.Shared.Chat;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace ControlPlane.Api.Tests;

public sealed class LessonTests(ControlPlaneApiFactory factory) : IClassFixture<ControlPlaneApiFactory>
{
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASolvedChatLeavesALessonThatPeopleDecideAbout()
    {
        _ = factory.CreateClient();
        var chat = factory.Services.GetRequiredService<ChatStore>();
        var actions = factory.Services.GetRequiredService<ActionStore>();
        var lessons = factory.Services.GetRequiredService<LessonStore>();
        var agentId = Guid.NewGuid().ToString("D");
        var identity = new ChatIdentity("S-1-5-21-111-222-333-9002", "DOMAIN\\lesson-user", 3);
        var conversation = await chat.OpenConversationAsync(agentId, identity, TestCancellation);
        await chat.SubmitMessageAsync(agentId, new ChatMessageSubmission(Guid.NewGuid().ToString("N"),
            conversation.ConversationId, "چاپگر چاپ نمی‌کند", "rtl", identity), TestCancellation);
        var look = Guid.NewGuid().ToString("N");
        await actions.StartRunAsync(look, "printer.list", agentId, conversation.ConversationId, ActionRunSources.Agent, "{}", TestCancellation);
        await actions.CompleteRunAsync(look, "Ok", "2 printers.", null, null, null, TestCancellation);
        var fix = Guid.NewGuid().ToString("N");
        await actions.StartRunAsync(fix, "spooler.restart_and_clear", agentId, conversation.ConversationId, ActionRunSources.Agent, "{}", TestCancellation);
        await actions.CompleteRunAsync(fix, "Done", "Spooler restarted.", null, null, null, TestCancellation);

        await lessons.CollectAsync(TestCancellation);
        Assert.DoesNotContain(await lessons.ListAsync(null, 500, TestCancellation),
            item => item.ConversationId == conversation.ConversationId); // not solved yet
        Assert.True(await chat.PatchHelpdeskConversationAsync(conversation.ConversationId,
            new HelpdeskConversationPatch(HelpdeskStatus.Resolved, null, null), TestCancellation));
        await lessons.CollectAsync(TestCancellation);
        await lessons.CollectAsync(TestCancellation); // never twice

        var lesson = Assert.Single(await lessons.ListAsync(null, 500, TestCancellation),
            item => item.ConversationId == conversation.ConversationId);
        Assert.Equal("چاپگر چاپ نمی‌کند", lesson.Symptom);
        Assert.Equal(["printer.list", "spooler.restart_and_clear"], lesson.Tried.Select(step => step.ActionId));
        Assert.Equal("spooler.restart_and_clear", lesson.FixedBy);
        Assert.Equal(LessonStatuses.New, lesson.Status);

        var member = await factory.CreateOperatorClientAsync(OperatorRoles.Member, TestCancellation);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PatchAsJsonAsync($"/api/lessons/{lesson.LessonId}",
            new { status = "Kept" }, TestCancellation)).StatusCode);
        var reviewer = await factory.CreateOperatorClientAsync(OperatorRoles.Reviewer, TestCancellation);
        Assert.Equal(HttpStatusCode.BadRequest, (await reviewer.PatchAsJsonAsync($"/api/lessons/{lesson.LessonId}",
            new { status = "Published" }, TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await reviewer.PatchAsJsonAsync($"/api/lessons/{lesson.LessonId}",
            new { status = "Drafted", note = "Good printer steps" }, TestCancellation)).StatusCode);

        var page = await member.GetFromJsonAsync<JsonObject>("/api/lessons?status=Drafted", TestCancellation);
        var mine = page!["lessons"]!.AsArray().Single(item => item!["lessonId"]!.GetValue<string>() == lesson.LessonId)!;
        Assert.Equal("Good printer steps", mine["note"]!.GetValue<string>());
        Assert.Contains(page["fixes"]!.AsArray(), item => item!["fixedBy"]!.GetValue<string>() == "spooler.restart_and_clear");
    }
}
