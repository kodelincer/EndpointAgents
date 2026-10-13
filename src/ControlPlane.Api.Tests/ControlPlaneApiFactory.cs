using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using ControlPlane.Api.Security;
using System.Net;
using System.Net.Http.Json;

namespace ControlPlane.Api.Tests;

/// <summary>Hosts the API with controllable peer addresses.</summary>
public sealed class ControlPlaneApiFactory : WebApplicationFactory<Program>
{
    public string FileRepositoryRoot { get; } = Path.Combine(Path.GetTempPath(),
        $"EndpointAgents-api-tests-{Guid.NewGuid():N}");
    public string DatabasePath => Path.Combine(FileRepositoryRoot, "control-plane.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileRepository:RootPath"] = FileRepositoryRoot,
                ["FileRepository:RequireProtectedAcl"] = "false",
                ["Chat:DatabasePath"] = DatabasePath,
                ["AgentSecurity:RequireHttps"] = "false",
                ["Hermes:Enabled"] = "false",
                ["Hermes:McpApiKey"] =
                    "test-controlplane-mcp-0123456789abcdef"
            }));
        builder.ConfigureServices(services =>
            services.AddSingleton<IStartupFilter, TestConnectionStartupFilter>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(FileRepositoryRoot))
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(FileRepositoryRoot, recursive: true);
        }
    }

    /// <summary>A loopback client signed in as a fresh operator with the given role.</summary>
    public async Task<HttpClient> CreateOperatorClientAsync(string role = OperatorRoles.Admin,
        CancellationToken cancellationToken = default)
    {
        const string password = "test-operator-password";
        var userName = $"{role}-{Guid.NewGuid():N}"[..32];
        await Services.GetRequiredService<OperatorUserStore>().CreateAsync(userName,
            $"Test {role}", role, password, mustChangePassword: false, cancellationToken);
        var client = CreateClient();
        (await client.PostAsJsonAsync("/api/auth/sign-in",
            new { userName, password }, cancellationToken)).EnsureSuccessStatusCode();
        return client;
    }

    public HttpClient CreateRemoteClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestConnectionStartupFilter.RemoteIpHeader, "192.0.2.10");
        return client;
    }

    private sealed class TestConnectionStartupFilter : IStartupFilter
    {
        public const string RemoteIpHeader = "X-Test-Remote-IP";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                var requestedAddress = context.Request.Headers[RemoteIpHeader].FirstOrDefault();
                context.Connection.RemoteIpAddress = string.IsNullOrWhiteSpace(requestedAddress)
                    ? IPAddress.Loopback
                    : IPAddress.Parse(requestedAddress);
                await nextMiddleware();
            });
            next(app);
        };
    }
}
