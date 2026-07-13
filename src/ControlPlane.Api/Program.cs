using ControlPlane.Api.Agents;
using ControlPlane.Api.Commands;
using ControlPlane.Api.Dashboard;
using ControlPlane.Api.Hubs;
using ControlPlane.Api.System;
using EndpointAgents.Shared.Rpc;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

// The same executable runs under Visual Studio during development and under the
// Windows Service Control Manager after deployment.
builder.Services.AddWindowsService(options =>
    options.ServiceName = "EndpointAgents.ControlPlane.Api");
builder.Services.AddSignalR();
builder.Services.AddCors(options => options.AddPolicy("Dashboard", policy =>
    policy.WithOrigins(builder.Configuration.GetSection("Dashboard:AllowedOrigins").Get<string[]>() ?? [])
        .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddSingleton<ActivityStore>();
builder.Services.AddSingleton<DashboardPublisher>();
builder.Services.AddSingleton<CommandCatalog>();
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<ControlPlaneRuntimeState>();
builder.Services.AddHealthChecks()
    .AddCheck<MaintenanceReadinessHealthCheck>("maintenance-readiness", tags: ["ready"]);

var app = builder.Build();
app.UseCors("Dashboard");

app.MapGet("/", () => "EndpointAgents Control Plane is running.");
app.MapHub<AgentHub>("/agent-hub");
app.MapHub<DashboardHub>("/dashboard-hub");

// Liveness proves that the process can answer. Readiness additionally indicates
// whether operators currently allow new command work.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
    }
});

app.MapGet("/api/agents", (AgentRegistry registry) => Results.Ok(registry.GetOnlineAgents()));
app.MapGet("/api/commands", (CommandCatalog catalog) => Results.Ok(catalog.GetAll()));
app.MapGet("/api/activity", (int? limit, string? agentId, ActivityStore store) =>
    Results.Ok(store.Get(limit ?? 100, agentId)));
app.MapGet("/api/system/status", (ControlPlaneRuntimeState runtime, AgentRegistry registry,
    ActivityStore activity) =>
{
    var now = DateTimeOffset.UtcNow;
    var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
    return Results.Ok(new ControlPlaneSystemStatus(version, Environment.MachineName,
        runtime.StartedAtUtc, Math.Max(0, (long)(now - runtime.StartedAtUtc).TotalSeconds),
        runtime.IsMaintenanceMode, registry.GetOnlineAgents().Count, activity.Count));
});

app.MapPost("/api/system/maintenance", (MaintenanceModeRequest request,
    ControlPlaneRuntimeState runtime, HttpContext context) =>
{
    // This endpoint is deliberately unavailable to remote agents and browsers.
    // The independently hosted local Web supervisor is its only caller.
    if (!LoopbackRequestGuard.IsLoopback(context.Connection.RemoteIpAddress))
        return Results.StatusCode(StatusCodes.Status403Forbidden);

    runtime.SetMaintenanceMode(request.Enabled);
    return Results.Ok(new { maintenanceMode = runtime.IsMaintenanceMode });
});

app.MapPost("/api/agents/{agentId}/commands", async (string agentId, RpcRequest request,
    AgentRegistry registry, CommandCatalog catalog, ControlPlaneRuntimeState runtime,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Method) || request.JsonRpc != "2.0")
        return Results.BadRequest(RpcResponse.Failure(request.Id ?? string.Empty, -32600, "Invalid JSON-RPC request."));
    if (!catalog.Contains(request.Method))
        return Results.BadRequest(RpcResponse.Failure(request.Id, -32601,
            $"Method '{request.Method}' is not in the command catalog."));
    if (runtime.IsMaintenanceMode)
        return Results.Json(RpcResponse.Failure(request.Id, 503,
            "The control plane is in maintenance mode and is not accepting commands."),
            statusCode: StatusCodes.Status503ServiceUnavailable);

    var response = await registry.SendCommandAsync(agentId, request, cancellationToken);
    return response.Error?.Code == 404 ? Results.NotFound(response) : Results.Ok(response);
});

app.Run();

public partial class Program;
