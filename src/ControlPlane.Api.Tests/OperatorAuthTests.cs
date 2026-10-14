using ControlPlane.Api.Security;
using EndpointAgents.Shared.Rpc;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace ControlPlane.Api.Tests;

public sealed class OperatorAuthTests(ControlPlaneApiFactory factory)
    : IClassFixture<ControlPlaneApiFactory>
{
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnonymousCallersCannotReachOperatorApis()
    {
        using var client = factory.CreateClient();
        foreach (var path in new[] { "/api/agents", "/api/helpdesk/conversations",
                     "/api/feature-requests", "/api/system/status", "/api/operators/",
                     "/api/auth/session" })
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await client.GetAsync(path, TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsync("/dashboard-hub/negotiate?negotiateVersion=1", null,
                TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/", TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync("/health/live", TestCancellation)).StatusCode);
    }

    [Fact]
    public async Task MembersHandleConversationsButNotAdminActions()
    {
        using var member = await factory.CreateOperatorClientAsync(OperatorRoles.Member,
            TestCancellation);

        var session = await member.GetFromJsonAsync<OperatorSession>("/api/auth/session",
            TestCancellation);
        Assert.Equal(OperatorRoles.Member, session!.Role);
        Assert.Contains("helpdesk.reply", session.Permissions);
        Assert.DoesNotContain("commands.run", session.Permissions);

        Assert.Equal(HttpStatusCode.OK,
            (await member.GetAsync("/api/helpdesk/conversations", TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await member.PostAsync("/dashboard-hub/negotiate?negotiateVersion=1", null,
                TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PostAsJsonAsync("/api/agents/any/commands",
                RpcRequest.Create("member", "endpoint.get_info"), TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PostAsJsonAsync("/api/system/maintenance", new { enabled = true },
                TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.PostAsync("/api/devices/any/revoke", null, TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.GetAsync("/api/operators/", TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PatchAsJsonAsync(
            "/api/feature-requests/unknown", new { status = "Planned" },
            TestCancellation)).StatusCode);
    }

    [Fact]
    public async Task ReviewersTriageFeatureRequests()
    {
        using var reviewer = await factory.CreateOperatorClientAsync(OperatorRoles.Reviewer,
            TestCancellation);
        // Authorized: the unknown id reaches the handler, which rejects it.
        Assert.Equal(HttpStatusCode.BadRequest, (await reviewer.PatchAsJsonAsync(
            "/api/feature-requests/unknown", new { status = "Planned" },
            TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await reviewer.PostAsJsonAsync("/api/system/maintenance", new { enabled = false },
                TestCancellation)).StatusCode);
    }

    [Fact]
    public async Task WrongPasswordsAreRejectedThenThrottled()
    {
        var store = factory.Services.GetRequiredService<OperatorUserStore>();
        var userName = $"throttle{Guid.NewGuid():N}"[..20];
        await store.CreateAsync(userName, "Throttle", OperatorRoles.Member,
            "right-password-123", mustChangePassword: false, TestCancellation);
        using var client = factory.CreateClient();

        for (var i = 0; i < OperatorSignInThrottle.MaxFailures; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(
                "/api/auth/sign-in", new { userName, password = "wrong-password-1" },
                TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync(
            "/api/auth/sign-in", new { userName, password = "right-password-123" },
            TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(
            "/api/auth/sign-in", new { userName = "nobody-here", password = "whatever-123" },
            TestCancellation)).StatusCode);
    }

    [Fact]
    public async Task NewAccountsMustReplaceTheirTemporaryPassword()
    {
        using var admin = await factory.CreateOperatorClientAsync(cancellationToken: TestCancellation);
        var userName = $"new{Guid.NewGuid():N}"[..16];
        var created = await (await admin.PostAsJsonAsync("/api/operators/",
            new { userName, displayName = "کارشناس تست", role = OperatorRoles.Member },
            TestCancellation)).Content.ReadFromJsonAsync<CreatedOperator>(TestCancellation);
        Assert.False(string.IsNullOrWhiteSpace(created!.TemporaryPassword));

        using var user = factory.CreateClient();
        var signIn = await user.PostAsJsonAsync("/api/auth/sign-in",
            new { userName, password = created.TemporaryPassword }, TestCancellation);
        Assert.True((await signIn.Content.ReadFromJsonAsync<OperatorSession>(
            TestCancellation))!.MustChangePassword);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await user.GetAsync("/api/helpdesk/conversations", TestCancellation)).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostAsJsonAsync("/api/auth/password",
            new { currentPassword = created.TemporaryPassword, newPassword = "short" },
            TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.PostAsJsonAsync("/api/auth/password",
            new { currentPassword = created.TemporaryPassword, newPassword = "a-long-new-password" },
            TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await user.GetAsync("/api/helpdesk/conversations", TestCancellation)).StatusCode);

        // Disabling the account ends its existing session immediately.
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync(
            $"/api/operators/{created.User.UserId}", new { disabled = true },
            TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await user.GetAsync("/api/helpdesk/conversations", TestCancellation)).StatusCode);
    }

    [Fact]
    public async Task AdminsCannotRemoveTheirOwnAdminAccess()
    {
        using var admin = await factory.CreateOperatorClientAsync(cancellationToken: TestCancellation);
        var me = await admin.GetFromJsonAsync<OperatorSession>("/api/auth/session",
            TestCancellation);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PatchAsJsonAsync(
            $"/api/operators/{me!.UserId}", new { role = OperatorRoles.Member },
            TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PatchAsJsonAsync(
            $"/api/operators/{me.UserId}", new { disabled = true },
            TestCancellation)).StatusCode);
    }

    [Fact]
    public async Task FirstStartWritesAOneTimeAdminPasswordNextToTheDatabase()
    {
        _ = factory.Services; // starts the host, which bootstraps the first admin
        var path = Path.Combine(factory.FileRepositoryRoot, OperatorUserStore.BootstrapPasswordFileName);
        var text = await File.ReadAllTextAsync(path, TestCancellation);
        var password = Regex.Match(text, @"Password:\s+(\S+)").Groups[1].Value;

        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/sign-in",
            new { userName = OperatorUserStore.BootstrapUserName, password }, TestCancellation);
        var session = await response.Content.ReadFromJsonAsync<OperatorSession>(TestCancellation);
        Assert.Equal(OperatorRoles.Admin, session!.Role);
        Assert.True(session.MustChangePassword);
    }

    [Fact]
    public async Task OnlyAdminsOrLocalScriptsIssueEnrollmentCodes()
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsync("/api/agent-enrollments", null, TestCancellation)).StatusCode);

        using var browser = new HttpRequestMessage(HttpMethod.Post, "/api/agent-enrollments");
        browser.Headers.Add("Origin", "http://127.0.0.1:3000");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(browser, TestCancellation)).StatusCode);

        using var member = await factory.CreateOperatorClientAsync(OperatorRoles.Member,
            TestCancellation);
        using var memberRequest = new HttpRequestMessage(HttpMethod.Post, "/api/agent-enrollments");
        memberRequest.Headers.Add("Origin", "http://127.0.0.1:3000");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await member.SendAsync(memberRequest, TestCancellation)).StatusCode);
    }

    [Fact]
    public void PasswordHashesVerifyOnlyTheOriginalPassword()
    {
        var hash = OperatorPasswordHasher.Hash("correct horse battery");
        Assert.True(OperatorPasswordHasher.Verify("correct horse battery", hash));
        Assert.False(OperatorPasswordHasher.Verify("correct horse battery!", hash));
        Assert.False(OperatorPasswordHasher.Verify("anything", "not-a-hash"));
        Assert.NotEqual(hash, OperatorPasswordHasher.Hash("correct horse battery"));
    }

    private sealed record CreatedOperator(OperatorUser User, string TemporaryPassword);
}
