using ControlPlane.Api.System;
using EndpointAgents.Shared.Rpc;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;
using ControlPlane.Api.Files;
using ControlPlane.Api.Chat;

namespace ControlPlane.Api.Tests;

public sealed class ApiTests : IClassFixture<ControlPlaneApiFactory>
{
    private readonly HttpClient _client;
    private readonly ControlPlaneApiFactory _factory;
    private readonly ControlPlaneRuntimeState _runtimeState;
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    public ApiTests(ControlPlaneApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _runtimeState = factory.Services.GetRequiredService<ControlPlaneRuntimeState>();
        _runtimeState.SetMaintenanceMode(false);
    }

    [Fact]
    public async Task ReturnsServiceIdentityWithoutServingFrontendRoutes()
    {
        var service = await _client.GetFromJsonAsync<ServiceIdentity>("/", TestCancellation);
        Assert.Equal("EndpointAgents.ControlPlane.Api", service!.Service);
        Assert.Equal("running", service.Status);

        var frontendRoute = await _client.GetAsync("/agents/", TestCancellation);
        Assert.Equal(HttpStatusCode.NotFound, frontendRoute.StatusCode);
        Assert.NotEqual("text/html", frontendRoute.Content.Headers.ContentType?.MediaType);

        var missingApi = await _client.GetAsync("/api/missing", TestCancellation);
        Assert.Equal(HttpStatusCode.NotFound, missingApi.StatusCode);
        Assert.NotEqual("text/html", missingApi.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("http://localhost:3000")]
    [InlineData("http://127.0.0.1:3000")]
    public async Task AllowsConfiguredNextJsOriginToNegotiateDashboardHub(string origin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/dashboard-hub/negotiate?negotiateVersion=1");
        request.Headers.Add("Origin", origin);

        using var response = await _client.SendAsync(request, TestCancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(origin, response.Headers.GetValues("Access-Control-Allow-Origin"));
        Assert.Contains("true", response.Headers.GetValues("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task ReturnsAgentCommandAndActivitySnapshots()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/agents", TestCancellation)).StatusCode);
        var commands = await _client.GetFromJsonAsync<Command[]>("/api/commands", TestCancellation);
        Assert.Contains(commands!, command => command.Method == "endpoint.get_info");
        Assert.Contains(commands!, command => command.Method == "software.office_inventory");
        Assert.Contains(commands!, command => command.Method == "software.office_activation_status");
        Assert.Contains(commands!, command => command.Method == "system.windows_activation_status");
        Assert.Contains(commands!, command => command.Method == "system.windows_activation_bugfix_run");
        Assert.Contains(commands!, command => command.Method == "system.windows_hello_sample_run");
        Assert.Contains(commands!, command => command.Method == "software.office_remediation_run");
        Assert.Contains(commands!, command => command.Method == "network.ibsng_diagnose");
        Assert.Contains(commands!, command => command.Method == "network.ibsng_ensure_dialer");
        Assert.Contains(commands!, command => command.Method == "network.ibsng_launch");
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/activity?limit=10", TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/support-requests?limit=10", TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/api/helpdesk/conversations?limit=10", TestCancellation)).StatusCode);
    }

    [Fact]
    public async Task ExposesDownloadableFileInventory()
    {
        var response = await _client.GetAsync("/api/files", TestCancellation);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var inventory = await response.Content.ReadFromJsonAsync<FileInventory>(TestCancellation);
        Assert.NotNull(inventory);
        Assert.Equal("IBSngDialer-pap(1).exe", inventory!.Repository.ExpectedPattern);
        Assert.EndsWith("office-artifacts.json", inventory.Repository.OfficeManifestPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("office-bugfix-managers.json",
            inventory.Repository.OfficeBugfixManifestPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("windowsbugfixer.bat",
            inventory.Repository.WindowsBugfixFilePath,
            StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("windows-hello-sample.bat",
            inventory.Repository.WindowsHelloSampleFilePath,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(inventory.Repository.ProtectedAclRequired);
        Assert.Equal("GET", inventory.Repository.DownloadMethod);
    }

    [Fact]
    public async Task RejectsUnknownCatalogMethod()
    {
        var response = await _client.PostAsJsonAsync("/api/agents/missing/commands",
            RpcRequest.Create("test", "endpoint.unknown"), TestCancellation);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("network.ibsng_diagnose")]
    [InlineData("network.ibsng_launch")]
    public async Task RejectsSensitiveCommandParametersBeforeTheyEnterActivityHistory(string method)
    {
        var parameters = JsonSerializer.SerializeToElement(new { password = "must-not-be-recorded" });
        var response = await _client.PostAsJsonAsync("/api/agents/sensitive-missing/commands",
            RpcRequest.Create($"sensitive-{method}", method, parameters), TestCancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var events = await _client.GetFromJsonAsync<Event[]>(
            "/api/activity?agentId=sensitive-missing&limit=10", TestCancellation);
        Assert.DoesNotContain(events!, item => item.Type == "command.completed");
    }

    [Theory]
    [InlineData("_chatApprovalId")]
    [InlineData("_controlPlaneAuthorizationId")]
    public async Task PublicCommandApiRejectsReservedAuthorizationMarker(string marker)
    {
        var parameters = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["artifactId"] = "windows-hello-sample",
            [marker] = Guid.NewGuid().ToString("N")
        });
        var response = await _client.PostAsJsonAsync("/api/agents/missing/commands",
            RpcRequest.Create("forged-chat-approval",
                "system.windows_hello_sample_run", parameters), TestCancellation);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ReturnsNotFoundAndRecordsOfflineCommand()
    {
        var response = await _client.PostAsJsonAsync("/api/agents/missing/commands",
            RpcRequest.Create("test", "endpoint.get_info"), TestCancellation);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var events = await _client.GetFromJsonAsync<Event[]>("/api/activity?agentId=missing&limit=10", TestCancellation);
        Assert.Contains(events!, item => item.Type == "command.completed" && item.Status == "error");
    }

    [Fact]
    public async Task ExposesLiveReadyAndSystemStatus()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health/live", TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health/ready", TestCancellation)).StatusCode);
        var status = await _client.GetFromJsonAsync<SystemStatus>("/api/system/status", TestCancellation);
        Assert.NotNull(status);
        Assert.False(status.MaintenanceMode);
        Assert.True(status.UptimeSeconds >= 0);
    }

    [Fact]
    public async Task MaintenanceMakesApiUnreadyAndRejectsCommands()
    {
        _runtimeState.SetMaintenanceMode(true);
        try
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable,
                (await _client.GetAsync("/health/ready", TestCancellation)).StatusCode);
            var response = await _client.PostAsJsonAsync("/api/agents/test/commands",
                RpcRequest.Create("maintenance-test", "endpoint.get_info"), TestCancellation);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            _runtimeState.SetMaintenanceMode(false);
        }
    }

    [Fact]
    public async Task LocalConsoleCanPauseAndResumeCommands()
    {
        var pauseResponse = await _client.PostAsJsonAsync("/api/system/maintenance",
            new { enabled = true }, TestCancellation);
        Assert.Equal(HttpStatusCode.OK, pauseResponse.StatusCode);
        Assert.True((await pauseResponse.Content.ReadFromJsonAsync<MaintenanceResult>(TestCancellation))!
            .MaintenanceMode);

        var resumeResponse = await _client.PostAsJsonAsync("/api/system/maintenance",
            new { enabled = false }, TestCancellation);
        Assert.Equal(HttpStatusCode.OK, resumeResponse.StatusCode);
        Assert.False((await resumeResponse.Content.ReadFromJsonAsync<MaintenanceResult>(TestCancellation))!
            .MaintenanceMode);
    }

    [Fact]
    public async Task RemotePeerCannotReachOperatorSurface()
    {
        using var remote = _factory.CreateRemoteClient();

        Assert.Equal(HttpStatusCode.Forbidden,
            (await remote.GetAsync("/", TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await remote.GetAsync("/api/agents", TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await remote.GetAsync("/api/support-requests", TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await remote.GetAsync("/api/helpdesk/conversations", TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await remote.PostAsync("/dashboard-hub/negotiate?negotiateVersion=1",
                content: null, TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await remote.PostAsJsonAsync("/api/system/maintenance",
                new { enabled = true }, TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await remote.PostAsJsonAsync("/api/agents/remote/commands",
                RpcRequest.Create("remote-test", "endpoint.get_info"), TestCancellation)).StatusCode);
    }

    [Fact]
    public async Task InternalMcpRejectsMissingSecretAndRemotePeers()
    {
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.GetAsync("/internal/mcp", TestCancellation)).StatusCode);

        using var remote = _factory.CreateRemoteClient();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await remote.GetAsync("/internal/mcp", TestCancellation)).StatusCode);
    }

    [Fact]
    public async Task InternalMcpPublishesTypedEndpointActionParameters()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/mcp")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/list",
                @params = new { }
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            "test-controlplane-mcp-0123456789abcdef");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", "2025-06-18");

        using var response = await _client.SendAsync(request, TestCancellation);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestCancellation);
        var json = body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("data: ",
                StringComparison.Ordinal))?["data: ".Length..] ?? body;
        using var document = JsonDocument.Parse(json);
        var tools = document.RootElement.GetProperty("result").GetProperty("tools");
        var proposal = tools.EnumerateArray().Single(tool =>
            tool.GetProperty("name").GetString() == "propose_endpoint_action");
        var schema = proposal.GetProperty("inputSchema");
        Assert.Contains("parameters", schema.GetProperty("required")
            .EnumerateArray().Select(item => item.GetString()));
        var parameters = schema.GetProperty("properties")
            .GetProperty("parameters");
        Assert.Equal("object", parameters.GetProperty("type").GetString());
        Assert.Contains("artifactId", parameters.GetProperty("required")
            .EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("string", parameters.GetProperty("properties")
            .GetProperty("artifactId").GetProperty("type").GetString());

        Assert.Contains(tools.EnumerateArray(), tool =>
            tool.GetProperty("name").GetString() == "office_runtime_status");
        var officeRepair = tools.EnumerateArray().Single(tool =>
            tool.GetProperty("name").GetString() == "run_office_activation_repair");
        var repairRequired = officeRepair.GetProperty("inputSchema")
            .GetProperty("required").EnumerateArray()
            .Select(item => item.GetString()).ToArray();
        Assert.Contains("runCapability", repairRequired);
        Assert.Contains("artifactId", repairRequired);

        var windowsRepair = tools.EnumerateArray().Single(tool =>
            tool.GetProperty("name").GetString() == "run_windows_activation_repair");
        var windowsRepairRequired = windowsRepair.GetProperty("inputSchema")
            .GetProperty("required").EnumerateArray()
            .Select(item => item.GetString()).ToArray();
        Assert.Contains("runCapability", windowsRepairRequired);
        Assert.DoesNotContain("artifactId", windowsRepairRequired);
    }

    [Fact]
    public async Task RemotePeerCanReachAgentHealthAndArtifactSurfaces()
    {
        using var remote = _factory.CreateRemoteClient();

        Assert.Equal(HttpStatusCode.OK,
            (await remote.GetAsync("/health/live", TestCancellation)).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden,
            (await remote.PostAsync("/agent-hub/negotiate?negotiateVersion=1",
                content: null, TestCancellation)).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden,
            (await remote.GetAsync("/api/files/ibsng-dialer", TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await remote.GetAsync("/api/files/office/catalog", TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await remote.GetAsync("/api/files/office/bugfix/catalog",
                TestCancellation)).StatusCode);
    }

    [Fact]
    public async Task EnrollmentCodesAreOneTimeAndIssueValidDeviceTokens()
    {
        var issueResponse = await _client.PostAsync(
            "/api/agent-enrollments", content: null, TestCancellation);
        Assert.Equal(HttpStatusCode.OK, issueResponse.StatusCode);
        var issue = await issueResponse.Content.ReadFromJsonAsync<EnrollmentIssue>(
            TestCancellation);
        Assert.NotNull(issue);

        using var remote = _factory.CreateRemoteClient();
        var requestedAgentId = Guid.NewGuid().ToString("D");
        var first = await remote.PostAsJsonAsync("/api/agent-enrollments/redeem",
            new EnrollmentRequest(issue!.EnrollmentCode, requestedAgentId, "test-machine"),
            TestCancellation);
        var reused = await remote.PostAsJsonAsync("/api/agent-enrollments/redeem",
            new EnrollmentRequest(issue.EnrollmentCode, requestedAgentId, "test-machine"),
            TestCancellation);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);
        var credential = await first.Content.ReadFromJsonAsync<EnrollmentRedemption>(
            TestCancellation);
        var store = _factory.Services.GetRequiredService<ChatStore>();
        Assert.Equal(requestedAgentId, await store.ValidateDeviceTokenAsync(
            credential!.DeviceToken, TestCancellation));

        Assert.Equal(HttpStatusCode.NoContent,
            (await _client.PostAsync($"/api/devices/{requestedAgentId}/revoke",
                content: null, TestCancellation)).StatusCode);
        Assert.Null(await store.ValidateDeviceTokenAsync(
            credential.DeviceToken, TestCancellation));
    }

    [Fact]
    public async Task RemotePeerDownloadsOnlyCataloguedHashVerifiedOfficeArtifact()
    {
        var officeRoot = Path.Combine(_factory.FileRepositoryRoot, "Office");
        Directory.CreateDirectory(officeRoot);
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        await File.WriteAllBytesAsync(Path.Combine(officeRoot, "approved.ps1"), bytes,
            TestCancellation);
        await File.WriteAllTextAsync(Path.Combine(officeRoot, "office-artifacts.json"),
            JsonSerializer.Serialize(new OfficeArtifactManifest
            {
                Version = 1,
                Artifacts =
                [
                    new OfficeArtifactManifestEntry
                    {
                        ArtifactId = "approved-office-fix",
                        DisplayName = "Approved Office remediation",
                        Description = "Integration test artifact",
                        FileName = "approved.ps1",
                        Sha256 = hash,
                        Kind = "PowerShellScript",
                        SupportedProductFamilies = ["Microsoft365Apps"],
                        ActivationStates = ["Unlicensed"]
                    }
                ]
            }), TestCancellation);

        using var remote = _factory.CreateRemoteClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await remote.GetAsync("/api/files/office/approved-office-fix",
                TestCancellation)).StatusCode);
        var credential = await EnrollRemoteAsync(remote, "office-artifact-test");
        remote.DefaultRequestHeaders.Authorization =
            new global::System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", credential.DeviceToken);
        var approved = await remote.GetAsync("/api/files/office/approved-office-fix",
            TestCancellation);
        var unknown = await remote.GetAsync("/api/files/office/not-catalogued",
            TestCancellation);

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal(hash, approved.Headers.GetValues("X-Artifact-Sha256").Single());
        Assert.Equal(bytes, await approved.Content.ReadAsByteArrayAsync(TestCancellation));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        await _factory.Services.GetRequiredService<ChatStore>()
            .RevokeDeviceAsync(credential.AgentId, TestCancellation);
    }

    [Fact]
    public async Task RemotePeerDownloadsOnlyCataloguedHashVerifiedOfficeBugfix()
    {
        var bytes = "@echo off\r\nexit /b 0\r\n"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var fileName = "office-activation-repair.bat";
        await File.WriteAllBytesAsync(
            Path.Combine(_factory.FileRepositoryRoot, fileName), bytes,
            TestCancellation);
        await File.WriteAllTextAsync(Path.Combine(_factory.FileRepositoryRoot,
            "office-bugfix-managers.json"), JsonSerializer.Serialize(
            new OfficeBugfixManifest
            {
                Version = 1,
                Artifacts =
                [
                    new OfficeBugfixManifestEntry
                    {
                        ArtifactId = "office-activation-repair",
                        DisplayName = "Office activation repair",
                        Description = "Integration test Office BAT",
                        FileName = fileName,
                        Sha256 = hash,
                        MinOfficeVersion = "16.0",
                        MaxOfficeVersionExclusive = "17.0"
                    }
                ]
            }), TestCancellation);

        using var remote = _factory.CreateRemoteClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await remote.GetAsync(
                "/api/files/office/bugfix/office-activation-repair",
                TestCancellation)).StatusCode);
        var credential = await EnrollRemoteAsync(remote, "office-bugfix-test");
        remote.DefaultRequestHeaders.Authorization =
            new global::System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", credential.DeviceToken);
        var approved = await remote.GetAsync(
            "/api/files/office/bugfix/office-activation-repair", TestCancellation);
        var unknown = await remote.GetAsync(
            "/api/files/office/bugfix/not-catalogued", TestCancellation);

        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal(hash, approved.Headers.GetValues("X-Artifact-Sha256").Single());
        Assert.Equal(bytes, await approved.Content.ReadAsByteArrayAsync(TestCancellation));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var store = _factory.Services.GetRequiredService<ChatStore>();
        await store.RevokeDeviceAsync(credential.AgentId, TestCancellation);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await remote.GetAsync(
                "/api/files/office/bugfix/office-activation-repair",
                TestCancellation)).StatusCode);
    }

    [Fact]
    public async Task WindowsBugfixDownloadRequiresEnrolledDeviceToken()
    {
        var windows = Directory.CreateDirectory(
            Path.Combine(_factory.FileRepositoryRoot, "Windows")).FullName;
        var bytes = "@echo off\r\nexit /b 0\r\n"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        await File.WriteAllBytesAsync(Path.Combine(windows, "windowsbugfixer.bat"),
            bytes, TestCancellation);

        using var remote = _factory.CreateRemoteClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await remote.GetAsync(
                "/api/files/windows/bugfix/windows-activation-repair",
                TestCancellation)).StatusCode);

        var redemption = await EnrollRemoteAsync(remote, "windows-test");
        remote.DefaultRequestHeaders.Authorization =
            new global::System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", redemption.DeviceToken);

        var response = await remote.GetAsync(
            "/api/files/windows/bugfix/windows-activation-repair", TestCancellation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(hash, response.Headers.GetValues("X-Artifact-Sha256").Single());
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync(TestCancellation));

        var store = _factory.Services.GetRequiredService<ChatStore>();
        await store.RevokeDeviceAsync(redemption.AgentId, TestCancellation);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await remote.GetAsync(
                "/api/files/windows/bugfix/windows-activation-repair",
                TestCancellation)).StatusCode);
    }

    private sealed record Command(string Method);

    private async Task<EnrollmentRedemption> EnrollRemoteAsync(HttpClient remote,
        string machineName)
    {
        var issue = await (await _client.PostAsync("/api/agent-enrollments",
            content: null, TestCancellation)).Content.ReadFromJsonAsync<EnrollmentIssue>(
            TestCancellation);
        var response = await remote.PostAsJsonAsync("/api/agent-enrollments/redeem",
            new EnrollmentRequest(issue!.EnrollmentCode, Guid.NewGuid().ToString("D"),
                machineName), TestCancellation);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EnrollmentRedemption>(
            TestCancellation))!;
    }

    private sealed record ServiceIdentity(string Service, string Status);
    private sealed record Event(string Type, string Status);
    private sealed record SystemStatus(bool MaintenanceMode, long UptimeSeconds);
    private sealed record MaintenanceResult(bool MaintenanceMode);
    private sealed record FileInventory(FileItem[] Files, FileRepository Repository);
    private sealed record FileItem(string ArtifactId, string FileName, string DownloadPath);
    private sealed record FileRepository(string ExpectedPattern, string OfficeManifestPath,
        string OfficeBugfixManifestPath, string WindowsBugfixFilePath,
        string WindowsHelloSampleFilePath, bool ProtectedAclRequired,
        string DownloadMethod);
}
