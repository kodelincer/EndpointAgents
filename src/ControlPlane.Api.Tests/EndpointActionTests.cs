using ControlPlane.Api.Actions;
using EndpointAgents.Shared.Actions;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ControlPlane.Api.Tests;

public sealed class EndpointActionCatalogTests
{
    [Fact]
    public void CatalogueIsConsistent()
    {
        var ids = EndpointActionCatalog.All.Select(item => item.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        foreach (var definition in EndpointActionCatalog.All)
        {
            Assert.Contains(definition.Area, ActionAreas.All);
            Assert.Matches(@"^[a-z_]+\.[a-z_]+$", definition.Id);
            Assert.False(string.IsNullOrWhiteSpace(definition.Name));
            Assert.False(string.IsNullOrWhiteSpace(definition.NameFa));
            Assert.False(string.IsNullOrWhiteSpace(definition.Description));
            Assert.InRange(definition.TimeoutSeconds, 1, 600);
            if (definition.Verify is { } verify)
                Assert.Equal(ActionTier.Look, EndpointActionCatalog.Find(verify.ActionId)?.Tier);
            if (definition.UndoActionId is { } undo)
                Assert.Equal(ActionTier.OperatorOnly, EndpointActionCatalog.Find(undo)?.Tier);
            foreach (var parameter in definition.Parameters)
            {
                if (parameter.Kind == ActionParameterKind.Choice)
                    Assert.NotEmpty(parameter.Choices ?? []);
                if (parameter.Kind == ActionParameterKind.Target)
                    Assert.All(parameter.TargetKinds ?? [], kind =>
                        Assert.Contains(kind, EnvironmentTargetKinds.All));
                if (parameter.Kind == ActionParameterKind.Integer)
                    Assert.True(parameter.Min < parameter.Max);
            }
            foreach (var key in definition.UsesSettings ?? [])
                Assert.Contains(key, ActionSettings.All);
        }
    }

    [Fact]
    public void TierLimitsDefaultToTheDesign()
    {
        EndpointActionDefinition Of(ActionTier tier) =>
            new("x.y", ActionAreas.Network, tier, ActionRunAs.System, "n", "n", "d", "r", []);
        Assert.Equal(30, Of(ActionTier.Look).EffectiveMaxPerHour);
        Assert.Equal(3, Of(ActionTier.SafeFix).EffectiveMaxPerHour);
        Assert.Equal(2, Of(ActionTier.Disruptive).EffectiveMaxPerHour);
        Assert.Equal(5, EndpointActionCatalog.Find("dns.flush")!.EffectiveMaxPerHour);
        Assert.Equal(ActionTier.Disruptive, EndpointActionCatalog.Find("explorer.restart")!.Tier);
        Assert.Equal(ActionTier.OperatorOnly, EndpointActionCatalog.Find("proxy.restore")!.Tier);
    }

    [Fact]
    public void ValidatorRejectsAnythingOutsideTheCatalogueTypes()
    {
        var port = EndpointActionCatalog.Find("net.test_port")!;
        Assert.True(EndpointActionValidator.TryValidate(port,
            new Dictionary<string, string?> { ["target"] = "hr-portal", ["port"] = "443" },
            out var normalized, out _));
        Assert.Equal("443", normalized["port"]);
        Assert.False(EndpointActionValidator.TryValidate(port,
            new Dictionary<string, string?> { ["target"] = "hr-portal", ["port"] = "70000" }, out _, out _));
        Assert.False(EndpointActionValidator.TryValidate(port,
            new Dictionary<string, string?> { ["target"] = "10.0.0.5", ["port"] = "443" }, out _, out _));
        Assert.False(EndpointActionValidator.TryValidate(port,
            new Dictionary<string, string?> { ["target"] = "hr-portal", ["port"] = "443", ["extra"] = "x" },
            out _, out var error));
        Assert.Contains("Unknown argument", error);
        Assert.False(EndpointActionValidator.TryValidate(port, null, out _, out _));
    }

    [Theory]
    [InlineData("host", "dc01.corp.local", true)]
    [InlineData("host", "dc01.corp.local:53", false)]
    [InlineData("host", "a b", false)]
    [InlineData("webapp", "https://hr.corp.local/login", true)]
    [InlineData("webapp", "file:///c:/windows", false)]
    [InlineData("webapp", "https://user:pw@hr.corp.local", false)]
    [InlineData("share", @"\\fs01\finance", true)]
    [InlineData("share", @"C:\Windows", false)]
    [InlineData("printer", @"\\print01\HP-2F", true)]
    public void TargetAddressesAreChecked(string kind, string address, bool valid) =>
        Assert.Equal(valid, EndpointActionValidator.IsValidTarget(
            new ResolvedTarget("t1", kind, "Label", address)));
}

public sealed class TargetMaskerTests
{
    private static EnvironmentTarget Target(string id, string kind, string label, string address,
        params string[] expected) =>
        new(id, kind, label, address, [], expected, null, null, null, DateTimeOffset.UtcNow);

    [Fact]
    public void ReplacesKnownAddressesSharesAndInternalNames()
    {
        var masker = new TargetMasker(
            [Target("hr", "webapp", "HR portal", "https://hr.corp.local/app", "10.1.2.3"),
                Target("fin", "share", "Finance share", @"\\fs01.corp.local\finance")],
            new Dictionary<string, string> { [ActionSettings.InternalDnsSuffixes] = "corp.local" });

        var text = masker.Mask(@"Opened https://hr.corp.local/app via hr.corp.local (10.1.2.3); " +
            @"\\fs01.corp.local\finance and \\other\x; mail.corp.local is slow");
        Assert.DoesNotContain("corp.local", text);
        Assert.DoesNotContain("10.1.2.3", text);
        Assert.Contains("[HR portal]", text);
        Assert.Contains("[Finance share]", text);
        Assert.Contains("[internal-share]", text);
        Assert.Contains("[internal-host]", text);

        var node = masker.Mask(JsonNode.Parse("""{"a":["hr.corp.local",{"b":"x"}],"n":3}"""));
        Assert.Equal("[HR portal]", node!["a"]![0]!.GetValue<string>());
        Assert.Equal(3, node["n"]!.GetValue<int>());
    }

    [Fact]
    public void PortsAreLimitedToTheTarget()
    {
        var web = new ResolvedTarget("hr", "webapp", "HR", "https://hr.corp.local:8443/");
        Assert.True(ActionRunner.PortAllowed(web, 8443));
        Assert.False(ActionRunner.PortAllowed(web, 3389));
        Assert.True(ActionRunner.PortAllowed(web with { Ports = [3389] }, 3389));
    }
}

public sealed class SkillLintTests
{
    [Fact]
    public void GoodSkillHasNoErrors()
    {
        var issues = SkillService.Lint("vpn-after-password-change", "VPN fails after a password change.",
            "1. Run `network.triage` with run_endpoint_action.\n2. Run `kerberos.purge`.\n" +
            "3. Ask the user whether it is solved; if not, escalate_to_helpdesk.");
        Assert.DoesNotContain(issues, issue => issue.Severity == "error");
        Assert.Empty(issues);
    }

    [Fact]
    public void FlagsUnknownToolsActionsCommandsAndPermissionWording()
    {
        var issues = SkillService.Lint("Bad Name", "",
            "Call mcp_controlplane_run_shell. Run `dns.nuke` and `proxy.restore`. " +
            "Then powershell.exe -c x, without asking. Open \\\\fs01\\share or 10.0.0.5.");
        var errors = issues.Where(issue => issue.Severity == "error").Select(issue => issue.Message).ToArray();
        Assert.Contains(errors, message => message.StartsWith("Name:"));
        Assert.Contains(errors, message => message.StartsWith("Description"));
        Assert.Contains(errors, message => message.Contains("run_shell"));
        Assert.Contains(errors, message => message.Contains("dns.nuke"));
        Assert.Contains(errors, message => message.Contains("proxy.restore"));
        var warnings = issues.Where(issue => issue.Severity == "warning").Select(issue => issue.Message).ToArray();
        Assert.Contains(warnings, message => message.Contains("powershell"));
        Assert.Contains(warnings, message => message.Contains("without asking"));
        Assert.Contains(warnings, message => message.Contains("IP address"));
    }

    [Fact]
    public void FileNamesInBackticksAreNotActions() =>
        Assert.DoesNotContain(SkillService.Lint("abc", "d", "See `config.yaml`. Ask the user if solved, escalate."),
            issue => issue.Severity == "error");

    [Fact]
    public void RenderAddsTheHeader() =>
        Assert.StartsWith("---\nname: hd-abc\ndescription: d\n---\n\nbody",
            SkillService.Render("abc", "d", "body"));
}

public sealed class EnvironmentValidationTests
{
    [Fact]
    public void TargetInputIsValidated()
    {
        var good = new EnvironmentTargetInput("fin", "share", "Finance", @"\\fs01\finance", null, ["10.0.0.9"], "F:", null, null);
        Assert.Null(ActionEndpoints.ValidateTarget(good));
        Assert.NotNull(ActionEndpoints.ValidateTarget(good with { Id = "Fin Share" }));
        Assert.NotNull(ActionEndpoints.ValidateTarget(good with { Kind = "webapp" }));
        Assert.NotNull(ActionEndpoints.ValidateTarget(good with { ExpectedAddresses = ["nope"] }));
        Assert.NotNull(ActionEndpoints.ValidateTarget(good with { Kind = "host", Address = "fs01", DriveLetter = "F:" }));
        Assert.NotNull(ActionEndpoints.ValidateTarget(good with { Ports = [0] }));
    }

    [Theory]
    [InlineData("proxy.mode", "pac", true)]
    [InlineData("proxy.mode", "auto", false)]
    [InlineData("proxy.server", "proxy.corp.local:8080", true)]
    [InlineData("proxy.server", "proxy.corp.local", false)]
    [InlineData("trust.rootThumbprints", "0123456789abcdef0123456789abcdef01234567", true)]
    [InlineData("trust.rootThumbprints", "1234", false)]
    [InlineData("network.internalSuffixes", "corp.local, .shirazmetro.local", true)]
    [InlineData("unknown.key", "x", false)]
    [InlineData("proxy.pacUrl", "", true)]
    public void SettingsAreValidated(string key, string value, bool valid) =>
        Assert.Equal(valid, ActionEndpoints.ValidateSetting(key, value) is null);
}

public sealed class ActionRunnerTests : IClassFixture<ControlPlaneApiFactory>
{
    private readonly ActionRunner _runner;
    private readonly ActionStore _store;
    private readonly HttpClient _client;
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    public ActionRunnerTests(ControlPlaneApiFactory factory)
    {
        _client = factory.CreateClient();
        _runner = factory.Services.GetRequiredService<ActionRunner>();
        _store = factory.Services.GetRequiredService<ActionStore>();
    }

    private static ActionRunContext Agent(string agentId) =>
        new(agentId, "S-1-5-21-1-2-3-1001", null, ActionRunSources.Agent);

    [Fact]
    public async Task RejectsBeforeReachingThePc()
    {
        var agent = Guid.NewGuid().ToString("D");
        Assert.Contains("Unknown action", (await _runner.RunAsync(Agent(agent), "shell.run", null,
            false, TestCancellation)).Error);
        Assert.Contains("helpdesk only", (await _runner.RunAsync(Agent(agent), "proxy.restore", null,
            false, TestCancellation)).Error);
        Assert.Contains("userConfirmed", (await _runner.RunAsync(Agent(agent), "explorer.restart", null,
            false, TestCancellation)).Error);
        Assert.Contains("environment-map", (await _runner.RunAsync(Agent(agent), "dns.resolve",
            new Dictionary<string, string?> { ["target"] = "no-such-target" }, false,
            TestCancellation)).Error);
    }

    [Fact]
    public async Task KillSwitchAndHourlyLimitStopTheAction()
    {
        var agent = Guid.NewGuid().ToString("D");
        await _store.SaveActionSettingAsync("arp.clear", new ActionSettingInput(false, null), "test",
            TestCancellation);
        Assert.Contains("switched off", (await _runner.RunAsync(Agent(agent), "arp.clear", null,
            false, TestCancellation)).Error);

        var limit = EndpointActionCatalog.Find("dns.flush")!.EffectiveMaxPerHour;
        for (var index = 0; index < limit; index++)
            await _store.StartRunAsync(Guid.NewGuid().ToString("N"), "dns.flush", agent, null,
                ActionRunSources.Agent, "{}", TestCancellation);
        var limited = await _runner.RunAsync(Agent(agent), "dns.flush", null, false, TestCancellation);
        Assert.False(limited.Accepted);
        Assert.Contains($"{limit} times", limited.Error);
    }

    [Fact]
    public async Task OfflineEndpointIsRecordedAsFailed()
    {
        var agent = Guid.NewGuid().ToString("D");
        var outcome = await _runner.RunAsync(Agent(agent), "network.status", null, false,
            TestCancellation);
        Assert.True(outcome.Accepted);
        Assert.Equal(ActionStatus.Failed, outcome.Status);
        var runs = await _store.GetRecentRunsAsync(agent, null, 10, TestCancellation);
        Assert.Equal("Failed", Assert.Single(runs).Status);
    }

    [Fact]
    public async Task EnvironmentMapAndCapabilitiesRoutesWork()
    {
        var put = await _client.PutAsJsonAsync("/api/environment/targets/hr-portal",
            new { kind = "webapp", label = "HR portal", address = "https://hr.corp.local", ports = new[] { 443 } },
            TestCancellation);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var bad = await _client.PutAsJsonAsync("/api/environment/targets/bad",
            new { kind = "webapp", label = "Bad", address = "ftp://x" }, TestCancellation);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        using var capabilities = JsonDocument.Parse(await _client.GetStringAsync("/api/capabilities",
            TestCancellation));
        var actions = capabilities.RootElement.GetProperty("actions").EnumerateArray().ToArray();
        Assert.Equal(EndpointActionCatalog.All.Count, actions.Length);
        Assert.Contains(capabilities.RootElement.GetProperty("tools").EnumerateArray(),
            tool => tool.GetString() == "run_endpoint_action");

        var draft = await _client.PostAsJsonAsync("/api/skills", new
        {
            name = "hr-portal-down",
            description = "The HR portal does not open.",
            body = "1. Run `web.probe` on hr-portal. 2. Ask the user if solved, else escalate."
        }, TestCancellation);
        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);
        var submit = await _client.PostAsync("/api/skills/hr-portal-down/submit", null, TestCancellation);
        Assert.Equal(HttpStatusCode.NoContent, submit.StatusCode);
        var publish = await _client.PostAsJsonAsync("/api/skills/hr-portal-down/publish",
            new { note = "ok" }, TestCancellation);
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        var skill = await _store.GetSkillAsync("hr-portal-down", TestCancellation);
        Assert.Equal(SkillStatus.Published, skill!.Status);
        Assert.Equal(1, skill.PublishedVersion);
    }
}
