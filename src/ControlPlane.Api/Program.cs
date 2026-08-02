using ControlPlane.Api.Agents;
using ControlPlane.Api.Commands;
using ControlPlane.Api.Chat;
using ControlPlane.Api.Dashboard;
using ControlPlane.Api.Files;
using ControlPlane.Api.Hermes;
using ControlPlane.Api.Hubs;
using ControlPlane.Api.Security;
using ControlPlane.Api.System;
using ControlPlane.Api.Support;
using ControlPlane.Api.Tools;
using EndpointAgents.Shared.Rpc;
using EndpointAgents.Shared.Office;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ModelContextProtocol.Server;
using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

// The same executable runs under Visual Studio during development and under the
// Windows Service Control Manager after deployment.
builder.Services.AddWindowsService(options =>
    options.ServiceName = "EndpointAgents.ControlPlane.Api");
builder.Services.AddSignalR();
builder.Services.AddAuthentication(AgentAuthentication.Scheme)
    .AddScheme<AuthenticationSchemeOptions, AgentTokenAuthenticationHandler>(
        AgentAuthentication.Scheme, _ => { });
builder.Services.AddCors(options => options.AddPolicy("Dashboard", policy =>
    policy.WithOrigins(builder.Configuration.GetSection("Dashboard:AllowedOrigins").Get<string[]>() ?? [])
        .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddSingleton<ActivityStore>();
builder.Services.AddSingleton<DashboardPublisher>();
builder.Services.AddSingleton<CommandCatalog>();
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<SupportRequestStore>();
builder.Services.AddSingleton<SupportRequestPublisher>();
builder.Services.AddOptions<ChatOptions>()
    .Bind(builder.Configuration.GetSection("Chat"))
    .PostConfigure(options =>
    {
        if (!Path.IsPathFullyQualified(options.DatabasePath))
            options.DatabasePath = Path.GetFullPath(options.DatabasePath,
                builder.Environment.ContentRootPath);
    })
    .Validate(options => Path.IsPathFullyQualified(options.DatabasePath),
        "Chat:DatabasePath must be an absolute path.")
    .Validate(options => options.RetentionDays is >= 1 and <= 3650,
        "Chat:RetentionDays must be between 1 and 3650.")
    .ValidateOnStart();
builder.Services.AddOptions<AgentSecurityOptions>()
    .Bind(builder.Configuration.GetSection("AgentSecurity"))
    .Validate(options => options.EnrollmentLifetimeMinutes is >= 5 and <= 1440,
        "AgentSecurity:EnrollmentLifetimeMinutes must be between 5 and 1440.")
    .ValidateOnStart();
builder.Services.AddOptions<HermesOptions>()
    .Bind(builder.Configuration.GetSection("Hermes"))
    .Validate(options => Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri)
        && uri.IsLoopback, "Hermes:BaseUrl must be an absolute loopback URL.")
    .Validate(options => options.MaxConcurrentRuns is >= 1 and <= 32,
        "Hermes:MaxConcurrentRuns must be between 1 and 32.")
    .Validate(options => options.RunTimeoutSeconds is >= 30 and <= 3600,
        "Hermes:RunTimeoutSeconds must be between 30 and 3600.")
    .Validate(options => !options.Enabled
        || (options.ApiKey.Length >= 32 && options.McpApiKey.Length >= 32),
        "Hermes API and MCP keys must each contain at least 32 characters when Hermes is enabled.")
    .ValidateOnStart();
builder.Services.AddSingleton<ChatStore>();
builder.Services.AddHostedService<ChatRetentionWorker>();
builder.Services.AddHermesHttpClient(builder.Configuration);
builder.Services.AddHostedService<HermesRunWorker>();
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<EndpointSupportTools>();
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
    .Validate(options => options.OfficeBugfixFileName.Equals(
            "office-activation-repair.bat", StringComparison.OrdinalIgnoreCase),
        "FileRepository:OfficeBugfixFileName must be office-activation-repair.bat.")
    .Validate(options => options.MaxFileSizeBytes is >= 1 and <= 1024L * 1024 * 1024,
        "FileRepository:MaxFileSizeBytes must be between 1 byte and 1 GiB.")
    .ValidateOnStart();
builder.Services.AddSingleton<FileArtifactRepository>();
builder.Services.AddSingleton<ControlPlaneRuntimeState>();
builder.Services.AddHealthChecks()
    .AddCheck<MaintenanceReadinessHealthCheck>("maintenance-readiness", tags: ["ready"]);

var app = builder.Build();
await app.Services.GetRequiredService<ChatStore>()
    .InitializeAsync(app.Lifetime.ApplicationStopping);

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

app.Use(async (context, next) =>
{
    var security = context.RequestServices.GetRequiredService<
        Microsoft.Extensions.Options.IOptions<AgentSecurityOptions>>().Value;
    var isRemote = !LoopbackRequestGuard.IsLoopback(context.Connection.RemoteIpAddress);
    if (security.RequireHttps && isRemote && !context.Request.IsHttps
        && OperatorAccessPolicy.AllowsRemoteRequest(context.Request.Path))
    {
        context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
        await context.Response.WriteAsJsonAsync(new
        {
            message = "Endpoint connections and artifact downloads require HTTPS."
        });
        return;
    }

    await next();
});

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/internal/mcp")
        && !InternalToolAccessPolicy.IsAllowed(context,
            context.RequestServices.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<HermesOptions>>()))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    await next();
});

// ControlPlane.Web is always a separate Next.js process on port 3000, including
// production, so its configured origin needs CORS access in every environment.
app.UseCors("Dashboard");
app.UseAuthentication();

app.MapHub<AgentHub>("/agent-hub");
app.MapHub<DashboardHub>("/dashboard-hub");
app.MapMcp("/internal/mcp");

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
app.MapPost("/api/agent-enrollments", async (ChatStore store,
    Microsoft.Extensions.Options.IOptions<AgentSecurityOptions> security,
    CancellationToken cancellationToken) =>
{
    var issue = await store.CreateEnrollmentAsync(
        TimeSpan.FromMinutes(security.Value.EnrollmentLifetimeMinutes), cancellationToken);
    return Results.Ok(issue);
});
app.MapPost("/api/agent-enrollments/redeem", async (EnrollmentRequest request,
    HttpContext context, ChatStore store,
    Microsoft.Extensions.Options.IOptions<AgentSecurityOptions> security,
    CancellationToken cancellationToken) =>
{
    if (security.Value.RequireHttps && !context.Request.IsHttps)
        return Results.Json(new { message = "Endpoint enrollment requires HTTPS." },
            statusCode: StatusCodes.Status426UpgradeRequired);
    var redemption = await store.RedeemEnrollmentAsync(request, cancellationToken);
    return redemption is null
        ? Results.BadRequest(new { message = "The enrollment code is invalid, expired, or used." })
        : Results.Ok(redemption);
});
app.MapPost("/api/devices/{agentId}/revoke", async (string agentId, ChatStore store,
    AgentRegistry registry, CancellationToken cancellationToken) =>
{
    await store.RevokeDeviceAsync(agentId, cancellationToken);
    await registry.RevokeAsync(agentId, cancellationToken);
    return Results.NoContent();
});
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
    var bugfixes = await repository.GetOfficeBugfixCatalogAsync(cancellationToken);
    var files = new List<FileRepositoryItem>();
    if (ibsng is not null)
        files.Add(new FileRepositoryItem("ibsng-dialer", ibsng.FileName, ibsng.SizeBytes,
            ibsng.LastModifiedUtc, ibsng.Sha256, "/api/files/ibsng-dialer",
            "IBSng", "IBSng dialer", "Approved endpoint dialer", "Executable"));
    files.AddRange(office.Artifacts.Select(ToRepositoryItem));
    files.AddRange(bugfixes.Artifacts.Select(ToBugfixRepositoryItem));

    return Results.Ok(new
    {
        files,
        issues = office.Issues.Concat(bugfixes.Issues),
        repository = new
        {
            expectedPattern = repository.IbsngDialerFileName,
            officeManifestPath = repository.OfficeManifestPath,
            officeBugfixFilePath = repository.OfficeBugfixFilePath,
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
app.MapGet("/api/files/office/bugfix/catalog", async (
    FileArtifactRepository repository, CancellationToken cancellationToken) =>
{
    var catalog = await repository.GetOfficeBugfixCatalogAsync(cancellationToken);
    return Results.Ok(new OfficeBugfixCatalog(
        catalog.Artifacts.Select(item => item.Descriptor).ToArray()));
});
app.MapGet("/api/files/office/bugfix/{artifactId}", async (string artifactId,
    HttpContext context, FileArtifactRepository repository,
    CancellationToken cancellationToken) =>
{
    var artifact = await repository.GetOfficeBugfixArtifactAsync(
        artifactId, cancellationToken);
    if (artifact is null)
        return Results.NotFound(new
        {
            message = "The approved Office bugfix manager is not available."
        });

    context.Response.Headers["X-Artifact-Sha256"] = artifact.File.Sha256;
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    return Results.File(artifact.File.FullPath, "application/octet-stream",
        artifact.File.FileName, artifact.File.LastModifiedUtc,
        enableRangeProcessing: true);
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
    null,
    artifact.Descriptor.ActivationStates,
    artifact.Descriptor.ErrorCodes,
    artifact.Descriptor.RequiresInteractiveUser,
    artifact.Descriptor.RequiresOfficeAppsClosed,
    artifact.Descriptor.RequiresRestart);

static FileRepositoryItem ToBugfixRepositoryItem(OfficeBugfixFileArtifact artifact) => new(
    artifact.Descriptor.ArtifactId,
    artifact.File.FileName,
    artifact.File.SizeBytes,
    artifact.File.LastModifiedUtc,
    artifact.File.Sha256,
    artifact.Descriptor.DownloadPath,
    "Office bugfix",
    artifact.Descriptor.DisplayName,
    artifact.Descriptor.Description,
    OfficeArtifactTrustPolicies.AdministratorApprovedSha256,
    null,
    artifact.Descriptor.MinOfficeVersion,
    null,
    artifact.Descriptor.MaxOfficeVersionExclusive,
    null,
    null,
    true);

public partial class Program;
