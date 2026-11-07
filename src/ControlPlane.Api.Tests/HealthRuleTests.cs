using ControlPlane.Api.Actions;
using ControlPlane.Api.Health;
using ControlPlane.Api.Security;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace ControlPlane.Api.Tests;

public sealed class HealthCheckCatalogTests
{
    private static HealthFinding Run(string id, string json, string? option = null) =>
        HealthCheckCatalog.Find(id)!.Evaluate(JsonNode.Parse(json), option ?? HealthCheckCatalog.Find(id)!.DefaultOption);

    [Fact]
    public void EveryCheckUsesALookActionAndOnlySafeFixes()
    {
        foreach (var check in HealthCheckCatalog.All)
        {
            Assert.Equal(EndpointAgents.Shared.Actions.ActionTier.Look,
                EndpointAgents.Shared.Actions.EndpointActionCatalog.Find(check.LookActionId)!.Tier);
            foreach (var fix in check.Fixes)
                Assert.Equal(EndpointAgents.Shared.Actions.ActionTier.SafeFix,
                    EndpointAgents.Shared.Actions.EndpointActionCatalog.Find(fix)!.Tier);
            if (check.DefaultOption is { } option) Assert.Contains(check.Options, item => item.Value == option);
        }
        Assert.Equal(HealthCheckCatalog.All.Count, HealthCheckCatalog.All.Select(item => item.Id).Distinct().Count());
    }

    [Fact]
    public void ChecksJudgeTheActionAnswer()
    {
        Assert.False(Run("disk-space", """{"drives":[{"letter":"C:","freeGb":4.2}]}""").Passed);
        Assert.True(Run("disk-space", """{"drives":[{"letter":"C:","freeGb":4.2}]}""", "5").Passed is false);
        Assert.True(Run("disk-space", """{"drives":[{"letter":"C","freeGb":42}]}""").Passed);
        Assert.Equal("4.2 GB free on C:", Run("disk-space", """{"drives":[{"letter":"C:","freeGb":4.2}]}""").Text);
        Assert.False(Run("restart-overdue", """{"pending":true,"uptimeHours":200}""").Passed);
        Assert.True(Run("restart-overdue", """{"pending":false,"uptimeHours":20}""").Passed);
        Assert.False(Run("clock-sync", """{"clockOk":false,"clockOffsetSeconds":400}""").Passed);
        Assert.True(Run("time-zone", """{"matchesStandardZone":true}""").Passed);
        Assert.False(Run("defender-signatures", """{"defender":{"enabled":true,"signatureAgeDays":12}}""").Passed);
        Assert.True(Run("defender-signatures", """{"defender":{"enabled":true,"signatureAgeDays":12}}""", "14").Passed);
        Assert.False(Run("defender-signatures", """{"defender":{"enabled":false,"signatureAgeDays":0}}""").Passed);
        Assert.False(Run("service-running", """{"services":[{"name":"Spooler","state":"Stopped"}]}""").Passed);
        Assert.True(Run("disk-failing", """{"predictsFailure":false,"anyWarning":false}""").Passed);
        // A missing answer is never a pass.
        Assert.False(Run("disk-space", "{}").Passed);
    }

    [Fact]
    public void RulesOnlyTakeAnswersFromTheFixedLists()
    {
        var good = new HealthRuleInput("Disk", "disk-space", "10", "disk.cleanup_safe", "daily", ["PC-01"], "fix");
        Assert.Null(HealthRuleStore.Validate(good));
        Assert.NotNull(HealthRuleStore.Validate(good with { CheckId = "shell" }));
        Assert.NotNull(HealthRuleStore.Validate(good with { Option = "1" }));
        Assert.NotNull(HealthRuleStore.Validate(good with { FixActionId = "reboot.schedule" }));
        Assert.NotNull(HealthRuleStore.Validate(good with { Frequency = "hourly" }));
        Assert.NotNull(HealthRuleStore.Validate(good with { FixActionId = null }));
        Assert.NotNull(HealthRuleStore.Validate(good with { Machines = ["PC 01; rm"] }));
        Assert.NotNull(HealthRuleStore.Validate(good with { CheckId = "clock-sync", Option = null, FixActionId = "time.resync", OnFailure = "tell-user" }));
        Assert.Null(HealthRuleStore.Validate(good with { CheckId = "restart-overdue", Option = "7", FixActionId = null, OnFailure = "tell-user" }));
    }

    [Fact]
    public void ChecksAreSpreadOverTimeAndRepeatPerPeriod()
    {
        var created = DateTimeOffset.Parse("2026-11-03T08:00:00Z");
        var rule = new HealthRule("r1", "n", "disk-space", "10", null, "daily", [], "report", true, "a", created, created.AddDays(7));
        var jitter = HealthRuleRunner.Jitter("r1", "agent", "daily");
        Assert.InRange(jitter.TotalMinutes, 0, 29);
        Assert.Equal(jitter, HealthRuleRunner.Jitter("r1", "agent", "daily"));
        Assert.False(HealthRuleRunner.IsDue(rule, "agent", null, created + jitter - TimeSpan.FromSeconds(1)));
        Assert.True(HealthRuleRunner.IsDue(rule, "agent", null, created + jitter));
        Assert.False(HealthRuleRunner.IsDue(rule, "agent", created.AddHours(1), created.AddHours(20)));
        Assert.True(HealthRuleRunner.IsDue(rule, "agent", created.AddHours(1), created.AddHours(25)));
        Assert.False(HealthRuleRunner.IsDue(rule with { Enabled = false }, "agent", null, created.AddDays(2)));
        Assert.True(HealthRuleRunner.Targets(rule, "pc-01"));
        Assert.True(HealthRuleRunner.Targets(rule with { Machines = ["PC-01"] }, "pc-01"));
        Assert.False(HealthRuleRunner.Targets(rule with { Machines = ["PC-02"] }, "pc-01"));
        Assert.True(rule.ReportOnly(created.AddDays(6)));
        Assert.False(rule.ReportOnly(created.AddDays(7)));
    }
}

public sealed class HealthRuleApiTests(ControlPlaneApiFactory factory) : IClassFixture<ControlPlaneApiFactory>
{
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AdminsMakeRulesAndOfflinePcsAreRecordedAsNotChecked()
    {
        var admin = await factory.CreateOperatorClientAsync(OperatorRoles.Admin, TestCancellation);
        var member = await factory.CreateOperatorClientAsync(OperatorRoles.Member, TestCancellation);
        var input = new HealthRuleInput("Restart reminder", "restart-overdue", "7", null, "weekly", [], "tell-user");

        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/api/health/rules", input, TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/health/rules",
            input with { CheckId = "anything" }, TestCancellation)).StatusCode);
        var created = await (await admin.PostAsJsonAsync("/api/health/rules", input, TestCancellation))
            .Content.ReadFromJsonAsync<HealthRule>(TestCancellation);
        Assert.NotNull(created);
        Assert.True(created.ReportOnly(DateTimeOffset.UtcNow));
        Assert.Equal(7, Math.Round((created.ReportOnlyUntilUtc - created.CreatedAtUtc).TotalDays));

        var checks = await member.GetFromJsonAsync<JsonArray>("/api/health/checks", TestCancellation);
        Assert.Contains(checks!, item => item!["id"]!.GetValue<string>() == "restart-overdue");

        var runner = factory.Services.GetRequiredService<HealthRuleRunner>();
        var result = await runner.RunAsync(created, Guid.NewGuid().ToString("D"), "PC-OFFLINE", TestCancellation);
        Assert.Equal(HealthOutcomes.Error, result.Outcome);

        var summaries = await member.GetFromJsonAsync<JsonArray>("/api/health/rules", TestCancellation);
        var mine = summaries!.Single(item => item!["rule"]!["ruleId"]!.GetValue<string>() == created.RuleId)!;
        Assert.Equal(1, mine["errors"]!.GetValue<int>());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PatchAsJsonAsync($"/api/health/rules/{created.RuleId}",
            new { enabled = false }, TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/health/rules/{created.RuleId}", TestCancellation)).StatusCode);
    }

    [Fact]
    public async Task OnlyHealthRulesMayShowATrayNotice()
    {
        var runner = factory.Services.GetRequiredService<ActionRunner>();
        var arguments = new Dictionary<string, string?> { ["notice"] = "restart-needed" };
        var agent = Guid.NewGuid().ToString("D");
        var fromAgent = await runner.RunAsync(new ActionRunContext(agent, null, null, ActionRunSources.Agent),
            "notice.show", arguments, false, TestCancellation);
        Assert.Contains("helpdesk only", fromAgent.Error);
        var fromRule = await runner.RunAsync(new ActionRunContext(agent, null, null, ActionRunSources.HealthRule),
            "notice.show", arguments, false, TestCancellation);
        Assert.True(fromRule.Accepted); // reaches dispatch; the test PC is offline
        Assert.Contains("not an allowed value", (await runner.RunAsync(new ActionRunContext(agent, null, null, ActionRunSources.HealthRule),
            "notice.show", new Dictionary<string, string?> { ["notice"] = "free text" }, false, TestCancellation)).Error);
    }
}
