using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.Net;

namespace ControlPlane.Api.Tests;

/// <summary>Hosts the API with controllable peer addresses.</summary>
public sealed class ControlPlaneApiFactory : WebApplicationFactory<Program>
{
    public string FileRepositoryRoot { get; } = Path.Combine(Path.GetTempPath(),
        $"EndpointAgents-api-tests-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileRepository:RootPath"] = FileRepositoryRoot
            }));
        builder.ConfigureServices(services =>
            services.AddSingleton<IStartupFilter, TestConnectionStartupFilter>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(FileRepositoryRoot))
            Directory.Delete(FileRepositoryRoot, recursive: true);
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
