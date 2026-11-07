using ControlPlane.Api.Actions;
using ControlPlane.Api.Security;
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
    [InlineData("app", "Foxit PDF Reader*", true)]
    [InlineData("app", "*", false)]
    [InlineData("app", "calc.exe /c del", false)]
    [InlineData("device", @"USB\VID_04F9&PID_0042;PCI\VEN_8086&DEV_15B8", true)]
    [InlineData("device", @"..\..\windows", false)]
    [InlineData("device", "", false)]
    [InlineData("wifi", "CORP-STAFF", true)]
    [InlineData("wifi", "Guest \"x\"", false)]
    [InlineData("wifi", "a-very-long-network-name-over-32-chars", false)]
    [InlineData("site", "*.tax.gov.ir", true)]
    [InlineData("site", "bank.example.com", true)]
    [InlineData("site", "https://bank.example.com", false)]
    [InlineData("site", "*", false)]
    public void TargetAddressesAreChecked(string kind, string address, bool valid) =>
        Assert.Equal(valid, EndpointActionValidator.IsValidTarget(
            new ResolvedTarget("t1", kind, "Label", address)));

    [Fact]
    public void VpnAndListDetailsAreChecked()
    {
        ResolvedTarget Vpn(params (string Key, string Value)[] details) =>
            new("vpn1", "vpn", "Office VPN", "vpn.corp.example",
                Details: details.ToDictionary(item => item.Key, item => item.Value));
        Assert.True(EndpointActionValidator.IsValidTarget(Vpn(("type", "builtin"), ("connection", "Office VPN"))));
        Assert.True(EndpointActionValidator.IsValidTarget(Vpn(("type", "client"), ("service", "FA_Scheduler"))));
        Assert.False(EndpointActionValidator.IsValidTarget(Vpn(("type", "client"), ("service", "x & calc"))));
        Assert.False(EndpointActionValidator.IsValidTarget(Vpn(("type", "other"))));
        Assert.False(EndpointActionValidator.IsValidTarget(new ResolvedTarget("a1", "app", "Foxit",
            "Foxit*", Details: new Dictionary<string, string> { ["packageId"] = "Bad Id" })));
        Assert.True(EndpointActionValidator.MatchesAppPattern("Foxit PDF Reader*", "Foxit PDF Reader 2024.1"));
        Assert.False(EndpointActionValidator.MatchesAppPattern("Foxit PDF Reader*", "Foxit PDF Editor"));
        Assert.False(EndpointActionValidator.MatchesAppPattern("7-Zip", "7-Zip 23.01 (x64)"));
    }

    [Fact]
    public void RemovalAndApprovalTiersFollowTheDesign()
    {
        Assert.Equal(ActionTier.ItApproval, EndpointActionCatalog.Find("software.uninstall")!.Tier);
        Assert.True(ActionRunner.NeedsApproval(EndpointActionCatalog.Find("software.uninstall")!, null));
        Assert.False(ActionRunner.NeedsApproval(EndpointActionCatalog.Find("dns.flush")!, null));
        var switched = new ActionSetting("dns.flush", true, null, DateTimeOffset.UtcNow, "admin", true);
        Assert.True(ActionRunner.NeedsApproval(EndpointActionCatalog.Find("dns.flush")!, switched));
        Assert.False(ActionRunner.NeedsApproval(EndpointActionCatalog.Find("network.status")!,
            switched with { ActionId = "network.status" }));
    }

    [Fact]
    public void InventoriesAreMatchedWithTheHelpdeskLists()
    {
        EnvironmentTarget Entry(string id, string kind, string label, string address,
            Dictionary<string, string>? details = null) =>
            new(id, kind, label, address, [], [], null, null, null, DateTimeOffset.UtcNow, details);
        var targets = new[]
        {
            Entry("foxit", "app", "Foxit Reader", "Foxit PDF Reader*",
                new() { ["publisher"] = "Foxit*", ["packageId"] = "uninstall-foxit" }),
            Entry("brother-l2350", "device", "Brother HL-L2350DW", @"USB\VID_04F9&PID_0042",
                new() { ["packageId"] = "driver-brother-l2350" })
        };
        var software = JsonNode.Parse("""{"apps":[{"name":"Foxit PDF Reader","publisher":"Foxit Software Inc."},{"name":"7-Zip 23.01","publisher":"Igor Pavlov"}]}""");
        var apps = ActionRunner.Annotate("software.inventory", software, targets)!["apps"]!.AsArray();
        Assert.Equal("foxit", apps[0]!["removable"]!.GetValue<string>());
        Assert.Equal("uninstall-foxit", apps[0]!["uninstallPackageId"]!.GetValue<string>());
        Assert.Null(apps[1]!["removable"]);

        var drivers = JsonNode.Parse("""{"devices":[{"name":"Brother printer","hardwareIds":["USB\\VID_04F9&PID_0042&REV_0100"]},{"name":"Unknown","hardwareIds":["USB\\VID_1234&PID_0001"]}]}""");
        var devices = ActionRunner.Annotate("drivers.inventory", drivers, targets)!["devices"]!.AsArray();
        Assert.Equal("driver-brother-l2350", devices[0]!["catalogMatch"]!["packageId"]!.GetValue<string>());
        Assert.Null(devices[1]!["catalogMatch"]);
    }

    [Fact]
    public void WifiAndZonesAreMatchedWithTheMap()
    {
        EnvironmentTarget Entry(string id, string kind, string address, Dictionary<string, string>? details = null) =>
            new(id, kind, id, address, [], [], null, null, null, DateTimeOffset.UtcNow, details);
        var targets = new[]
        {
            Entry("staff-wifi", "wifi", "CORP-STAFF"),
            Entry("lab-wifi", "wifi", "CORP-LAB", new() { ["profile"] = "Lab profile" }),
            Entry("hr", "webapp", "https://hr.corp.local/login"),
            Entry("erp", "webapp", "https://erp.corp.local")
        };
        var wifi = ActionRunner.Annotate("wifi.status",
            JsonNode.Parse("""{"ssid":"CORP-STAFF","profiles":["CORP-STAFF","Home"]}"""), targets)!;
        var company = wifi["companyProfiles"]!.AsArray();
        Assert.True(company[0]!["saved"]!.GetValue<bool>());
        Assert.True(company[0]!["connected"]!.GetValue<bool>());
        Assert.False(company[1]!["saved"]!.GetValue<bool>());

        var zones = ActionRunner.Annotate("zones.status",
            JsonNode.Parse("""{"entries":[{"host":"*.hr.corp.local","zone":"Intranet","setByPolicy":true},{"host":"secret.corp.local","zone":"Trusted","setByPolicy":false}],"policyManaged":true}"""),
            targets)!;
        Assert.Null(zones["entries"]);
        Assert.Equal(2, zones["entryCount"]!.GetValue<int>());
        var sites = zones["sites"]!.AsArray();
        Assert.Equal("Intranet", sites[0]!["zone"]!.GetValue<string>());
        Assert.Equal("Internet", sites[1]!["zone"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("Unlocked in AD, please sign in again.", false)]
    [InlineData("123456-123456-123456-123456-123456-123456-123456-123456", true)]
    [InlineData("password: Spring2026", true)]
    [InlineData("رمز: abc123", true)]
    public void PersonNotesNeverCarrySecrets(string note, bool secret) =>
        Assert.Equal(secret, ApprovalService.LooksSecret(note));
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

public sealed class OrganizationKnowledgeTests
{
    private static readonly DateOnly Today = new(2026, 10, 14);

    [Fact]
    public void TemplateIsDetectedAndIgnored()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "deploy", "control-plane", "organization", "ORGANIZATION.template.md"));
        var parsed = OrganizationKnowledgeService.Parse(text, Today);
        Assert.True(parsed.IsTemplate);
        Assert.Null(parsed.LastReviewed);
    }

    [Theory]
    [InlineData("Last reviewed: 2026-09-01", false)]
    [InlineData("Last reviewed: 2026-01-01", true)]
    [InlineData("no date at all", true)]
    public void StaleWhenOldOrUndated(string text, bool stale) =>
        Assert.Equal(stale, OrganizationKnowledgeService.Parse(text, Today).Stale);

    [Fact]
    public void InternalNamesAreMaskedBeforeTheModelSeesThem()
    {
        var masker = new TargetMasker([],
            new Dictionary<string, string> { [ActionSettings.InternalDnsSuffixes] = "corp.local" });
        var text = masker.Mask(@"Mail is mail.corp.local; files at \\fs01\data.");
        Assert.DoesNotContain("corp.local", text);
        Assert.DoesNotContain("fs01", text);
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
    [InlineData("network.ipFromDhcp", "true", true)]
    [InlineData("network.ipFromDhcp", "yes", false)]
    [InlineData("power.standardPlan", "balanced", true)]
    [InlineData("power.standardPlan", "ultimate", false)]
    [InlineData("activation.kmsHost", "172.16.0.2", true)]
    [InlineData("activation.kmsHost", "kms.corp.local", true)]
    [InlineData("activation.kmsHost", "172.16.0.2:1688", false)]
    [InlineData("activation.kmsHost", "kms & calc.exe", false)]
    [InlineData("activation.kmsHost", "/skms evil", false)]
    [InlineData("activation.kmsPort", "1688", true)]
    [InlineData("activation.kmsPort", "0", false)]
    [InlineData("activation.kmsPort", "70000", false)]
    [InlineData("activation.kmsPort", "-1", false)]
    [InlineData("time.ntpServer", "time.corp.local", true)]
    [InlineData("time.ntpServer", "172.16.0.2", true)]
    [InlineData("time.ntpServer", "172.16.0.2:123", false)]
    [InlineData("time.ntpServer", "a.corp.local,0x9 b.corp.local", false)]
    [InlineData("time.ntpServer", "time & calc", false)]
    public void SettingsAreValidated(string key, string value, bool valid) =>
        Assert.Equal(valid, ActionEndpoints.ValidateSetting(key, value) is null);

    [Fact]
    public void KmsSettingIsReadTheSameWayOnBothSides()
    {
        Assert.False(KmsSettings.TryRead(new Dictionary<string, string>(), out _, out _));
        Assert.True(KmsSettings.TryRead(new Dictionary<string, string>
            { [ActionSettings.KmsHost] = " 172.16.0.2 " }, out var host, out var port));
        Assert.Equal("172.16.0.2", host);
        Assert.Equal(1688, port);
        Assert.True(KmsSettings.TryRead(new Dictionary<string, string>
        {
            [ActionSettings.KmsHost] = "kms.corp.local", [ActionSettings.KmsPort] = "1700"
        }, out _, out port));
        Assert.Equal(1700, port);
        Assert.False(KmsSettings.TryRead(new Dictionary<string, string>
        {
            [ActionSettings.KmsHost] = "kms.corp.local", [ActionSettings.KmsPort] = "abc"
        }, out _, out _));
        Assert.False(KmsSettings.TryRead(new Dictionary<string, string>
            { [ActionSettings.KmsHost] = "kms & calc" }, out _, out _));
    }

    [Fact]
    public void KmsActionsRunWithoutTheTrayAndUseOnlyTheSetting()
    {
        foreach (var id in new[] { "kms.check", "windows.kms_activate", "office.kms_activate" })
        {
            var definition = EndpointActionCatalog.Find(id)!;
            Assert.Equal(ActionAreas.Activation, definition.Area);
            Assert.Equal(ActionRunAs.System, definition.RunAs);
            Assert.Empty(definition.Parameters);
            Assert.Equal([ActionSettings.KmsHost, ActionSettings.KmsPort], definition.UsesSettings);
        }
        Assert.Equal(ActionTier.Look, EndpointActionCatalog.Find("kms.check")!.Tier);
        Assert.Equal(ActionTier.SafeFix, EndpointActionCatalog.Find("windows.kms_activate")!.Tier);
        Assert.Equal(ActionTier.SafeFix, EndpointActionCatalog.Find("office.kms_activate")!.Tier);
        Assert.False(ActionRunner.NeedsApproval(EndpointActionCatalog.Find("office.kms_activate")!, null));
    }
}

public sealed class TimeCorrectionTests
{
    [Fact]
    public void NtpSettingIsReadTheSameWayOnBothSides()
    {
        Assert.False(NtpSettings.TryRead(new Dictionary<string, string>(), out _));
        Assert.True(NtpSettings.TryRead(new Dictionary<string, string>
            { [ActionSettings.NtpServer] = " time.corp.local " }, out var host));
        Assert.Equal("time.corp.local", host);
        Assert.False(NtpSettings.TryRead(new Dictionary<string, string>
            { [ActionSettings.NtpServer] = "time.corp.local,0x9" }, out _));
        Assert.False(NtpSettings.TryRead(new Dictionary<string, string>
            { [ActionSettings.NtpServer] = "a b" }, out _));
    }

    [Fact]
    public void SetNtpIsASafeFixThatRunsWithoutTheTrayAndUsesOnlyTheSetting()
    {
        var definition = EndpointActionCatalog.Find("time.set_ntp")!;
        Assert.Equal(ActionTier.SafeFix, definition.Tier);
        Assert.Equal(ActionRunAs.System, definition.RunAs);
        Assert.Empty(definition.Parameters);
        Assert.Equal([ActionSettings.NtpServer], definition.UsesSettings);
        Assert.Equal("time.status", definition.Verify!.ActionId);
        Assert.False(ActionRunner.NeedsApproval(definition, null));
        Assert.Contains(ActionSettings.NtpServer,
            EndpointActionCatalog.Find("time.status")!.UsesSettings!);
    }
}

public sealed class ActionRunnerTests : IClassFixture<ControlPlaneApiFactory>
{
    private readonly ControlPlaneApiFactory _factory;
    private readonly ActionRunner _runner;
    private readonly ActionStore _store;
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    public ActionRunnerTests(ControlPlaneApiFactory factory)
    {
        _factory = factory;
        _ = factory.CreateClient();
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
    public async Task ApprovalTierWaitsForAHelpdeskMember()
    {
        var agent = Guid.NewGuid().ToString("D");
        await _store.UpsertTargetAsync(new EnvironmentTargetInput("anydesk", "app", "AnyDesk",
            "AnyDesk*", null, null, null, null, null), TestCancellation);
        var first = await _runner.RunAsync(Agent(agent), "software.uninstall",
            new Dictionary<string, string?> { ["target"] = "anydesk" }, false, TestCancellation,
            "User asked to remove AnyDesk.");
        Assert.True(first.Accepted);
        Assert.Equal(ActionStatus.AwaitingApproval, first.Status);
        var again = await _runner.RunAsync(Agent(agent), "software.uninstall",
            new Dictionary<string, string?> { ["target"] = "anydesk" }, false, TestCancellation);
        Assert.Equal(first.Data!["approvalId"]!.GetValue<string>(), again.Data!["approvalId"]!.GetValue<string>());
        Assert.Empty(await _store.GetRecentRunsAsync(agent, null, 10, TestCancellation));

        var pending = (await _store.GetApprovalsAsync(true, 500, TestCancellation))
            .Single(item => item.AgentId == agent);
        Assert.Equal("Remove software: AnyDesk", pending.Title);
        Assert.Equal("User asked to remove AnyDesk.", pending.Reason);

        var member = await _factory.CreateOperatorClientAsync(OperatorRoles.Member, TestCancellation);
        var approved = await member.PostAsJsonAsync($"/api/approvals/{pending.ApprovalId}/approve",
            new { note = "ok" }, TestCancellation);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var decided = await _store.GetApprovalAsync(pending.ApprovalId, TestCancellation);
        // The test PC is offline, so the approved run is recorded as failed.
        Assert.Equal(ApprovalStatus.Failed, decided!.Status);
        Assert.Single(await _store.GetRecentRunsAsync(agent, null, 10, TestCancellation));
        Assert.Equal(HttpStatusCode.Conflict, (await member.PostAsJsonAsync(
            $"/api/approvals/{pending.ApprovalId}/decline", new { note = "late" }, TestCancellation)).StatusCode);
    }

    [Fact]
    public async Task PersonRequestIsMarkedDoneWithoutRunningAnything()
    {
        var agent = Guid.NewGuid().ToString("D");
        var request = await _store.CreateApprovalAsync(ApprovalKinds.Person, "unlock-account",
            "Unlock the user's account", agent, null, null, "{}", "Account locked after a password change.",
            TestCancellation);
        var member = await _factory.CreateOperatorClientAsync(OperatorRoles.Member, TestCancellation);
        Assert.Equal(HttpStatusCode.Conflict, (await member.PostAsJsonAsync(
            $"/api/approvals/{request.ApprovalId}/approve", new { note = "password: Temp-1234" },
            TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.PostAsJsonAsync(
            $"/api/approvals/{request.ApprovalId}/approve", new { note = "Unlocked, sign in again." },
            TestCancellation)).StatusCode);
        var decided = await _store.GetApprovalAsync(request.ApprovalId, TestCancellation);
        Assert.Equal(ApprovalStatus.Done, decided!.Status);
        Assert.Empty(await _store.GetRecentRunsAsync(agent, null, 10, TestCancellation));
        Assert.Contains(PersonRequestKinds.All, kind => kind.Id == "bitlocker-key");
    }

    [Fact]
    public async Task AdminCanRequireApprovalForAFix()
    {
        var agent = Guid.NewGuid().ToString("D");
        await _store.SaveActionSettingAsync("net.register_dns",
            new ActionSettingInput(true, null, RequiresApproval: true), "test", TestCancellation);
        var outcome = await _runner.RunAsync(Agent(agent), "net.register_dns", null, false,
            TestCancellation);
        Assert.Equal(ActionStatus.AwaitingApproval, outcome.Status);
        var request = (await _store.GetApprovalsAsync(true, 500, TestCancellation))
            .Single(item => item.AgentId == agent);
        var member = await _factory.CreateOperatorClientAsync(OperatorRoles.Member, TestCancellation);
        Assert.Equal(HttpStatusCode.OK, (await member.PostAsJsonAsync(
            $"/api/approvals/{request.ApprovalId}/decline", new { note = "Not now" }, TestCancellation)).StatusCode);
        Assert.Equal(ApprovalStatus.Declined, (await _store.GetApprovalAsync(request.ApprovalId,
            TestCancellation))!.Status);
        await _store.SaveActionSettingAsync("net.register_dns", new ActionSettingInput(true, null),
            "test", TestCancellation);
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
        var _client = await _factory.CreateOperatorClientAsync(OperatorRoles.Admin, TestCancellation);
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

    [Fact]
    public async Task RolesLimitWhoChangesTheMapAndPublishes()
    {
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/capabilities", TestCancellation)).StatusCode);

        var member = await _factory.CreateOperatorClientAsync(OperatorRoles.Member, TestCancellation);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/capabilities", TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync("/api/environment/targets/x1",
            new { kind = "host", label = "X", address = "x.corp.local" }, TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync("/api/actions/dns.flush/settings",
            new { enabled = false }, TestCancellation)).StatusCode);

        var draft = await member.PostAsJsonAsync("/api/skills", new
        {
            name = "member-draft",
            description = "A member's draft.",
            body = "1. Run `network.status`. 2. Ask the user if solved, else escalate."
        }, TestCancellation);
        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/api/skills/member-draft/publish",
            new { note = "" }, TestCancellation)).StatusCode);
        var reviewer = await _factory.CreateOperatorClientAsync(OperatorRoles.Reviewer, TestCancellation);
        Assert.Equal(HttpStatusCode.OK, (await reviewer.PostAsJsonAsync("/api/skills/member-draft/publish",
            new { note = "" }, TestCancellation)).StatusCode);
    }
}

public sealed class HermesAssetConsistencyTests
{
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EndpointAgents.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void ReviewedSkillsOnlyNameRealToolsAndActions()
    {
        // Only the skills the sync script promotes into Hermes (ibsng-support is not promoted).
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "sync-hermes-profile.ps1"));
        var list = script[script.IndexOf("$Skills = @(", StringComparison.Ordinal)..];
        list = list[..list.IndexOf(')')];
        var skills = global::System.Text.RegularExpressions.Regex.Matches(list, "\"([a-z-]+)\"")
            .Select(match => Path.Combine(RepoRoot(), "deploy", "hermes", "skills",
                match.Groups[1].Value, "SKILL.md"))
            .ToArray();
        Assert.Contains(skills, file => file.Contains("endpoint-actions", StringComparison.Ordinal));
        foreach (var file in skills)
        {
            var text = File.ReadAllText(file);
            var errors = SkillService.Lint("repo-skill", "d", text)
                .Where(issue => issue.Severity == "error"
                    && !issue.Message.Contains("'---' header", StringComparison.Ordinal))
                .Select(issue => issue.Message);
            Assert.True(!errors.Any(), $"{file}: {string.Join("; ", errors)}");
        }
    }

    [Fact]
    public void HermesAllowListMatchesTheControlPlaneTools()
    {
        var config = File.ReadAllLines(Path.Combine(RepoRoot(), "deploy", "hermes", "config.yaml.example"));
        var start = Array.FindIndex(config, line => line.Trim() == "include:");
        var included = config.Skip(start + 1).TakeWhile(line => line.TrimStart().StartsWith("- "))
            .Select(line => line.Trim()[2..].Trim()).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(SkillService.KnownTools.Order(), included.Order());
    }
}
