using ControlPlane.WebHost.Management;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using System.Net;
using System.Runtime.Versioning;
using System.Text.Json.Serialization;

[assembly: SupportedOSPlatform("windows")]

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddWindowsService(options =>
    options.ServiceName = "EndpointAgents.ControlPlane.Web");
builder.Services.AddOptions<ManagementOptions>()
    .Bind(builder.Configuration.GetSection("Management"))
    .Validate(options => Uri.TryCreate(options.ApiBaseUrl, UriKind.Absolute, out _),
        "Management:ApiBaseUrl must be an absolute URL.")
    .Validate(options => options.ActionTimeoutSeconds is >= 5 and <= 300,
        "Management:ActionTimeoutSeconds must be between 5 and 300.")
    .ValidateOnStart();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));
builder.Services.AddCors(options => options.AddPolicy("LocalWebDevelopment", policy =>
    policy.WithOrigins("http://localhost:3000", "http://127.0.0.1:3000")
        .AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddSingleton<IWindowsServiceController, WindowsServiceController>();
builder.Services.AddSingleton<ControlPlaneManagementService>();
builder.Services.AddHttpClient("ControlPlaneApi", (services, client) =>
{
    var management = services.GetRequiredService<IOptions<ManagementOptions>>().Value;
    client.BaseAddress = new Uri(management.ApiBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(5);
});

var app = builder.Build();
app.UseCors("LocalWebDevelopment");

// Management remains unauthenticated for this phase, so loopback is a hard
// security boundary rather than a convenience setting.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/management"))
    {
        var isLoopback = context.Connection.RemoteIpAddress is not null &&
            IPAddress.IsLoopback(context.Connection.RemoteIpAddress);
        var origin = context.Request.Headers.Origin.ToString();
        var isExpectedOrigin = string.IsNullOrWhiteSpace(origin) ||
            origin is "http://localhost:3000" or "http://127.0.0.1:3000";
        if (!isLoopback || !isExpectedOrigin)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
    }

    await next();
});

app.MapGet("/api/management/control-plane", (ControlPlaneManagementService service,
    CancellationToken cancellationToken) => service.GetStatusAsync(cancellationToken));

MapAction("start", ServiceAction.Start);
MapAction("stop", ServiceAction.Stop);
MapAction("restart", ServiceAction.Restart);
MapAction("enter-maintenance", ServiceAction.EnterMaintenance);
MapAction("exit-maintenance", ServiceAction.ExitMaintenance);

void MapAction(string route, ServiceAction action) =>
    app.MapPost($"/api/management/control-plane/{route}", async (
        ControlPlaneManagementService service, CancellationToken cancellationToken) =>
    {
        var result = await service.ExecuteAsync(action, cancellationToken);
        return result.Accepted ? Results.Ok(result) : Results.Conflict(result);
    });

app.UseDefaultFiles();
app.UseStaticFiles();

// Next.js static export emits one index.html per route. Resolve direct visits
// without rewriting every route to the overview document.
app.MapFallback(async context =>
{
    var webRoot = app.Environment.WebRootPath;
    if (string.IsNullOrWhiteSpace(webRoot))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    var route = context.Request.Path.Value?.Trim('/') ?? string.Empty;
    var candidate = Path.GetFullPath(Path.Combine(webRoot, route, "index.html"));
    var root = Path.GetFullPath(webRoot) + Path.DirectorySeparatorChar;
    if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
        candidate = Path.Combine(webRoot, "index.html");

    if (!File.Exists(candidate))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsync("ControlPlane.Web has not been built yet.");
        return;
    }

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(candidate);
});

app.Run();

public partial class Program;
