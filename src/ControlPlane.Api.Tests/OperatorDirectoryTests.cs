using ControlPlane.Api.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Net.Http.Json;

namespace ControlPlane.Api.Tests;

public sealed class OperatorDirectoryTests(ControlPlaneApiFactory factory)
    : IClassFixture<ControlPlaneApiFactory>
{
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    private sealed class FakeDirectory(bool configured = true) : IOperatorDirectory
    {
        public Dictionary<string, (string Password, string DisplayName, string? Role)> Users { get; } = new();
        public bool Down { get; set; }
        public int SignIns { get; private set; }
        public bool Configured => configured;
        public string Domain => "CORP";

        public Task<DirectorySignIn> SignInAsync(string userName, string password, CancellationToken cancellationToken)
        {
            SignIns++;
            if (Down) return Task.FromResult(new DirectorySignIn(DirectorySignInStatus.Unavailable));
            var account = OperatorDirectory.AccountName(userName);
            return Task.FromResult(Users.TryGetValue(account, out var user) && user.Password == password
                ? new DirectorySignIn(DirectorySignInStatus.Accepted, new DirectoryPerson(account, user.DisplayName, user.Role))
                : new DirectorySignIn(DirectorySignInStatus.Rejected));
        }

        public Task<DirectoryPerson?> FindAsync(string userName, CancellationToken cancellationToken)
        {
            var account = OperatorDirectory.AccountName(userName);
            return Task.FromResult(Users.TryGetValue(account, out var user)
                ? new DirectoryPerson(account, user.DisplayName, user.Role) : null);
        }
    }

    private WebApplicationFactory<Program> With(FakeDirectory directory) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IOperatorDirectory>();
            services.AddSingleton<IOperatorDirectory>(directory);
        }));

    private static async Task<HttpResponseMessage> SignIn(HttpClient client, string name, string password) =>
        await client.PostAsJsonAsync("/api/auth/sign-in", new { userName = name, password }, TestCancellation);

    private static async Task<HttpClient> AdminAsync(WebApplicationFactory<Program> host)
    {
        var name = $"admin-{Guid.NewGuid():N}"[..20];
        await host.Services.GetRequiredService<OperatorUserStore>().CreateAsync(name, "Admin",
            OperatorRoles.Admin, "local-admin-password", false, TestCancellation);
        var client = host.CreateClient();
        (await SignIn(client, name, "local-admin-password")).EnsureSuccessStatusCode();
        return client;
    }

    [Theory]
    [InlineData("CORP\\Sara", "sara")]
    [InlineData("sara@corp.example", "sara")]
    [InlineData("  Sara ", "sara")]
    public void AccountNameDropsDomainAndSuffix(string typed, string expected) =>
        Assert.Equal(expected, OperatorDirectory.AccountName(typed));

    [Fact]
    public void FilterValuesAreEscaped() =>
        Assert.Equal("a\\2a\\29\\28b\\5c", OperatorDirectory.EscapeFilter("a*)(b\\"));

    [Fact]
    public async Task DirectoryUserInAMappedGroupGetsAnAccountWithTheGroupRole()
    {
        var directory = new FakeDirectory();
        directory.Users["sara"] = ("domain-password-1", "Sara Karimi", OperatorRoles.Reviewer);
        using var host = With(directory);
        using var client = host.CreateClient();

        var response = await SignIn(client, "CORP\\Sara", "domain-password-1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = await response.Content.ReadFromJsonAsync<OperatorSession>(TestCancellation);
        Assert.Equal("sara", session!.UserName);
        Assert.Equal(OperatorRoles.Reviewer, session.Role);
        Assert.False(session.MustChangePassword);

        var stored = await host.Services.GetRequiredService<OperatorUserStore>()
            .FindByUserNameAsync("sara", TestCancellation);
        Assert.Equal(OperatorSources.Directory, stored!.Source);

        using var again = host.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await SignIn(again, "sara", "domain-password-1")).StatusCode);
    }

    [Fact]
    public async Task DirectoryUserOutsideTheMappedGroupsOrWithAWrongPasswordIsRefused()
    {
        var directory = new FakeDirectory();
        directory.Users["omid"] = ("domain-password-2", "Omid", null);
        directory.Users["nima"] = ("domain-password-3", "Nima", OperatorRoles.Member);
        using var host = With(directory);
        using var client = host.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await SignIn(client, "omid", "domain-password-2")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignIn(client, "nima", "wrong-password-xx")).StatusCode);
        var store = host.Services.GetRequiredService<OperatorUserStore>();
        Assert.Null(await store.FindByUserNameAsync("omid", TestCancellation));
        Assert.Null(await store.FindByUserNameAsync("nima", TestCancellation));
    }

    [Fact]
    public async Task ALocalAccountIsNeverCheckedAgainstTheDirectory()
    {
        var directory = new FakeDirectory();
        directory.Users["leila"] = ("domain-password-4", "Leila", OperatorRoles.Admin);
        using var host = With(directory);
        await host.Services.GetRequiredService<OperatorUserStore>().CreateAsync("leila", "Leila local",
            OperatorRoles.Member, "local-password-5", false, TestCancellation);
        using var client = host.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await SignIn(client, "leila", "domain-password-4")).StatusCode);
        var local = await SignIn(client, "leila", "local-password-5");
        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
        Assert.Equal(OperatorRoles.Member, (await local.Content.ReadFromJsonAsync<OperatorSession>(TestCancellation))!.Role);
        Assert.Equal(0, directory.SignIns);
    }

    [Fact]
    public async Task AnUnreachableDirectoryGives503ButLocalAdminsStillSignIn()
    {
        var directory = new FakeDirectory { Down = true };
        using var host = With(directory);
        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await SignIn(client, "sara", "whatever-password")).StatusCode);
        using var admin = await AdminAsync(host);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/auth/session", TestCancellation)).StatusCode);
    }

    [Fact]
    public async Task AdminsAddDirectoryUsersByNameAndTheirPasswordsStayInTheDirectory()
    {
        var directory = new FakeDirectory();
        directory.Users["reza"] = ("domain-password-6", "Reza J", null);
        using var host = With(directory);
        using var admin = await AdminAsync(host);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/operators/",
            new { userName = "ghost", role = "member", source = "directory" }, TestCancellation)).StatusCode);
        var added = await admin.PostAsJsonAsync("/api/operators/",
            new { userName = "CORP\\Reza", role = "reviewer", source = "directory" }, TestCancellation);
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        var user = (await added.Content.ReadFromJsonAsync<global::System.Text.Json.JsonElement>(TestCancellation)).GetProperty("user");
        Assert.Equal("directory", user.GetProperty("source").GetString());
        var userId = user.GetProperty("userId").GetString();

        // Added by name, so the group mapping isn't needed.
        using var reza = host.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await SignIn(reza, "reza", "domain-password-6")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reza.PostAsJsonAsync("/api/auth/password",
            new { currentPassword = "domain-password-6", newPassword = "another-long-password" }, TestCancellation)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PatchAsJsonAsync($"/api/operators/{userId}",
            new { resetPassword = true }, TestCancellation)).StatusCode);
    }

    [Fact]
    public async Task WithoutADirectorySetupDirectoryOptionsAreOffAndUnknownNamesFail()
    {
        using var host = With(new FakeDirectory(configured: false));
        using var client = host.CreateClient();
        var options = await client.GetFromJsonAsync<global::System.Text.Json.JsonElement>("/api/auth/options", TestCancellation);
        Assert.False(options.GetProperty("directory").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignIn(client, "sara", "domain-password-1")).StatusCode);
        using var admin = await AdminAsync(host);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/operators/",
            new { userName = "sara", role = "member", source = "directory" }, TestCancellation)).StatusCode);
    }
}
