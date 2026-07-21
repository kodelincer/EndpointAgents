using ControlPlane.Api.Agents;
using ControlPlane.Api.Commands;
using ControlPlane.Api.Dashboard;
using ControlPlane.Api.Files;
using ControlPlane.Api.Hubs;
using ControlPlane.Api.System;
using ControlPlane.Api.Support;
using EndpointAgents.Shared.Rpc;
using EndpointAgents.Shared.Office;
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
builder.Services.AddSingleton<SupportRequestStore>();
builder.Services.AddSingleton<SupportRequestPublisher>();
builder.Services.AddOptions<FileRepositoryOptions>()
    .Bind(builder.Configuration.GetSection("FileRepository"))
    .Validate(options => !string.IsNullOrWhiteSpace(options.RootPath),
        "FileRepository:RootPath is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.IbsngDialerFileName)
        && Path.GetFileName(options.IbsngDialerFileName) == options.IbsngDialerFileName
        && options.IbsngDialerFileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
        "FileRepository:IbsngDialerFileName must be an executable file name.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.OfficeDirectoryName)
        && Path.GetFileName(options.OfficeDirectoryName) == options.OfficeDirectoryName,
        "FileRepository:OfficeDirectoryName must be one directory name.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.OfficeManifestFileName)
        && Path.GetFileName(options.OfficeManifestFileName) == options.OfficeManifestFileName
        && options.OfficeManifestFileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase),
        "FileRepository:OfficeManifestFileName must be a JSON file name.")
    .Validate(options => options.MaxFileSizeBytes is >= 1 and <= 1024L * 1024 * 1024,
        "FileRepository:MaxFileSizeBytes must be between 1 byte and 1 GiB.")
    .ValidateOnStart();
builder.Services.AddSingleton<FileArtifactRepository>();
builder.Services.AddSingleton<ControlPlaneRuntimeState>();
builder.Services.AddHealthChecks()
    .AddCheck<MaintenanceReadinessHealthCheck>("maintenance-readiness", tags: ["ready"]);

var app = builder.Build();

// Endpoint agents and health monitors can reach the explicitly public routes
// below. The unauthenticated operator console and command surface remain local
// to this machine until authentication and authorization are introduced.
app.Use(async (context, next) =>
{
    if (!LoopbackRequestGuard.IsLoopback(context.Connection.RemoteIpAddress)
        && !OperatorAccessPolicy.AllowsRemoteRequest(context.Request.Path))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new
        {
            message = "The control-plane console and operator APIs are available only on the server."
        });
        return;
    }

    await next();
});

// ControlPlane.Web is always a separate Next.js process on port 3000, including
// production, so its configured origin needs CORS access in every environment.
app.UseCors("Dashboard");

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

app.MapGet("/", () => Results.Ok(new
{
    service = "EndpointAgents.ControlPlane.Api",
    status = "running"
}));
app.MapGet("/api/agents", (AgentRegistry registry) => Results.Ok(registry.GetOnlineAgents()));
app.MapGet("/api/commands", (CommandCatalog catalog) => Results.Ok(catalog.GetAll()));
app.MapGet("/api/files/ibsng-dialer/metadata", async (FileArtifactRepository repository,
    CancellationToken cancellationToken) =>
{
    var artifact = await repository.GetIbsngDialerAsync(cancellationToken);
    return artifact is null
        ? Results.NotFound(new
        {
            message = "No IBSng dialer is available.",
            expectedFileName = repository.IbsngDialerFileName
        })
        : Results.Ok(new
        {
            artifact.FileName,
            artifact.SizeBytes,
            artifact.LastModifiedUtc,
            artifact.Sha256
        });
});
app.MapGet("/api/files", async (FileArtifactRepository repository,
    CancellationToken cancellationToken) =>
{
    var ibsng = await repository.GetIbsngDialerAsync(cancellationToken);
    var office = await repository.GetOfficeCatalogAsync(cancellationToken);
    var files = new List<FileRepositoryItem>();
    if (ibsng is not null)
        files.Add(new FileRepositoryItem("ibsng-dialer", ibsng.FileName, ibsng.SizeBytes,
            ibsng.LastModifiedUtc, ibsng.Sha256, "/api/files/ibsng-dialer",
            "IBSng", "IBSng dialer", "Approved endpoint dialer", "Executable"));
    files.AddRange(office.Artifacts.Select(ToRepositoryItem));

    return Results.Ok(new
    {
        files,
        issues = office.Issues,
        repository = new
        {
            expectedPattern = repository.IbsngDialerFileName,
            officeManifestPath = repository.OfficeManifestPath,
            downloadMethod = "GET"
        }
    });
});
app.MapGet("/api/files/ibsng-dialer", async (HttpContext context,
    FileArtifactRepository repository, CancellationToken cancellationToken) =>
{
    var artifact = await repository.GetIbsngDialerAsync(cancellationToken);
    if (artifact is null)
        return Results.NotFound(new
        {
            message = "The approved IBSng dialer file is not available.",
            expectedFileName = repository.IbsngDialerFileName
        });

    context.Response.Headers["X-Artifact-Sha256"] = artifact.Sha256;
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    return Results.File(artifact.FullPath, "application/octet-stream", artifact.FileName,
        artifact.LastModifiedUtc, enableRangeProcessing: true);
});
app.MapGet("/api/files/office/catalog", async (FileArtifactRepository repository,
    CancellationToken cancellationToken) =>
{
    var catalog = await repository.GetOfficeCatalogAsync(cancellationToken);
    return Results.Ok(new OfficeRemediationCatalog(
        catalog.Artifacts.Select(item => item.Descriptor).ToArray()));
});
app.MapGet("/api/files/office/{artifactId}/metadata", async (string artifactId,
    FileArtifactRepository repository, CancellationToken cancellationToken) =>
{
    var artifact = await repository.GetOfficeArtifactAsync(artifactId, cancellationToken);
    return artifact is null
        ? Results.NotFound(new { message = "The approved Office artifact is not available." })
        : Results.Ok(ToRepositoryItem(artifact));
});
app.MapGet("/api/files/office/{artifactId}", async (string artifactId, HttpContext context,
    FileArtifactRepository repository, CancellationToken cancellationToken) =>
{
    var artifact = await repository.GetOfficeArtifactAsync(artifactId, cancellationToken);
    if (artifact is null)
        return Results.NotFound(new { message = "The approved Office artifact is not available." });

    context.Response.Headers["X-Artifact-Sha256"] = artifact.File.Sha256;
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    return Results.File(artifact.File.FullPath, "application/octet-stream",
        artifact.File.FileName, artifact.File.LastModifiedUtc, enableRangeProcessing: true);
});
app.MapGet("/api/activity", (int? limit, string? agentId, ActivityStore store) =>
    Results.Ok(store.Get(limit ?? 100, agentId)));
app.MapGet("/api/support-requests", (int? limit, string? agentId,
    SupportRequestStore store) => Results.Ok(store.Get(limit ?? 100, agentId)));
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
    // Defense in depth: the global operator boundary already rejects remote
    // callers, and this privileged state change verifies loopback explicitly.
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
    if (SensitiveCommandParameterGuard.ContainsSensitiveData(request.Params))
        return Results.BadRequest(RpcResponse.Failure(request.Id, 400,
            "Passwords, tokens, and other credentials are not accepted by this command channel."));
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

static FileRepositoryItem ToRepositoryItem(OfficeFileArtifact artifact) => new(
    artifact.Descriptor.ArtifactId,
    artifact.File.FileName,
    artifact.File.SizeBytes,
    artifact.File.LastModifiedUtc,
    artifact.File.Sha256,
    artifact.Descriptor.DownloadPath,
    "Office",
    artifact.Descriptor.DisplayName,
    artifact.Descriptor.Description,
    artifact.Descriptor.Kind,
    artifact.Descriptor.SupportedProductFamilies,
    artifact.Descriptor.MinOfficeVersion,
    artifact.Descriptor.MaxOfficeVersion,
    artifact.Descriptor.ActivationStates,
    artifact.Descriptor.ErrorCodes,
    artifact.Descriptor.RequiresInteractiveUser,
    artifact.Descriptor.RequiresOfficeAppsClosed,
    artifact.Descriptor.RequiresRestart);

public partial class Program;
