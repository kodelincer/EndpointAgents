using ControlPlane.Api.Agents;
using ControlPlane.Api.Commands;
using ControlPlane.Api.Dashboard;
using ControlPlane.Api.Hubs;
using EndpointAgents.Shared.Rpc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
builder.Services.AddCors(options => options.AddPolicy("Dashboard", policy =>
    policy.WithOrigins(builder.Configuration.GetSection("Dashboard:AllowedOrigins").Get<string[]>() ?? [])
        .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddSingleton<ActivityStore>();
builder.Services.AddSingleton<DashboardPublisher>();
builder.Services.AddSingleton<CommandCatalog>();
builder.Services.AddSingleton<AgentRegistry>();

var app = builder.Build();
app.UseCors("Dashboard");

app.MapGet("/", () => "EndpointAgents Control Plane is running.");
app.MapHub<AgentHub>("/agent-hub");
app.MapHub<DashboardHub>("/dashboard-hub");

app.MapGet("/api/agents", (AgentRegistry registry) => Results.Ok(registry.GetOnlineAgents()));
app.MapGet("/api/commands", (CommandCatalog catalog) => Results.Ok(catalog.GetAll()));
app.MapGet("/api/activity", (int? limit, string? agentId, ActivityStore store) =>
    Results.Ok(store.Get(limit ?? 100, agentId)));

app.MapPost("/api/agents/{agentId}/commands", async (string agentId, RpcRequest request,
    AgentRegistry registry, CommandCatalog catalog, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Method) || request.JsonRpc != "2.0")
        return Results.BadRequest(RpcResponse.Failure(request.Id ?? string.Empty, -32600, "Invalid JSON-RPC request."));
    if (!catalog.Contains(request.Method))
        return Results.BadRequest(RpcResponse.Failure(request.Id, -32601,
            $"Method '{request.Method}' is not in the command catalog."));

    var response = await registry.SendCommandAsync(agentId, request, cancellationToken);
    return response.Error?.Code == 404 ? Results.NotFound(response) : Results.Ok(response);
});

app.Run();

public partial class Program;
