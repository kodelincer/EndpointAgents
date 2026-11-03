using ControlPlane.Api.Actions;
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
using EndpointAgents.Shared.Packages;
using EndpointAgents.Shared.Windows;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
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
        AgentAuthentication.Scheme, _ => { })
    .AddOperatorCookie();
builder.Services.AddAuthorization(OperatorPolicies.Add);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<OperatorSignInThrottle>();
builder.Services.AddSingleton<OperatorUserStore>();
builder.Services.Configure<OperatorDirectoryOptions>(builder.Configuration.GetSection("OperatorDirectory"));
builder.Services.AddSingleton<IOperatorDirectory, OperatorDirectory>();
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
    .Validate(options => options.MaxVisibleItems is >= 1 and <= 200,
        "Chat:MaxVisibleItems must be between 1 and 200.")
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
    .Validate(options => options.MaxHistoryMessages is >= 1 and <= 100,
        "Hermes:MaxHistoryMessages must be between 1 and 100.")
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
    .WithTools<EndpointSupportTools>()
    .WithTools<EndpointActionTools>();
// General endpoint actions: catalogue runner, environment map, incidents, helpdesk skills.
builder.Services.AddSingleton<ActionStore>();
builder.Services.AddSingleton<ControlPlane.Api.Updates.AgentUpdateStore>();
builder.Services.Configure<ControlPlane.Api.Software.SoftwareInstallOptions>(
    builder.Configuration.GetSection("SoftwareInstall"));
builder.Services.AddSingleton<ControlPlane.Api.Software.SoftwareApprovalStore>();
builder.Services.AddSingleton<ControlPlane.Api.Updates.AgentUpdateRepository>();
builder.Services.AddSingleton<ActionRunner>();
builder.Services.AddSingleton<ApprovalService>();
builder.Services.AddSingleton<ServerProbe>();
builder.Services.AddHttpClient(ServerProbe.HttpClientName)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false
    });
builder.Services.Configure<ActiveDirectoryOptions>(builder.Configuration.GetSection("ActiveDirectory"));
builder.Services.AddSingleton<ActiveDirectoryAccountChecker>();
builder.Services.Configure<HelpdeskSkillOptions>(builder.Configuration.GetSection("HelpdeskSkills"));
builder.Services.AddSingleton<SkillService>();
builder.Services.Configure<OrganizationKnowledgeOptions>(builder.Configuration.GetSection("Organization"));
builder.Services.AddSingleton<OrganizationKnowledgeService>();
builder.Services.Configure<PrivacyOptions>(builder.Configuration.GetSection("Privacy"));
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
    .Validate(options => !string.IsNullOrWhiteSpace(options.OfficeBugfixManifestFileName)
        && Path.GetFileName(options.OfficeBugfixManifestFileName)
            == options.OfficeBugfixManifestFileName
        && options.OfficeBugfixManifestFileName.EndsWith(".json",
            StringComparison.OrdinalIgnoreCase),
        "FileRepository:OfficeBugfixManifestFileName must be a JSON file name.")
    .Validate(options => options.WindowsDirectoryName.Equals("Windows",
            StringComparison.Ordinal),
        "FileRepository:WindowsDirectoryName must be Windows.")
    .Validate(options => options.WindowsBugfixFileName.Equals(
            WindowsRemediationPaths.FileName, StringComparison.Ordinal),
        "FileRepository:WindowsBugfixFileName must be windowsbugfixer.bat.")
    .Validate(options => options.WindowsBugfixManifestFileName.Equals(
            "windows-bugfix-managers.json", StringComparison.Ordinal),
        "FileRepository:WindowsBugfixManifestFileName must be windows-bugfix-managers.json.")
    .Validate(options => options.WindowsHelloSampleFileName.Equals(
            WindowsHelloSamplePaths.FileName, StringComparison.Ordinal),
        "FileRepository:WindowsHelloSampleFileName must be windows-hello-sample.bat.")
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
await app.Services.GetRequiredService<OperatorUserStore>()
    .InitializeAsync(app.Lifetime.ApplicationStopping);
await app.Services.GetRequiredService<ActionStore>()
    .InitializeAsync(app.Lifetime.ApplicationStopping);
await app.Services.GetRequiredService<ControlPlane.Api.Updates.AgentUpdateStore>()
    .InitializeAsync(app.Lifetime.ApplicationStopping);
await app.Services.GetRequiredService<ControlPlane.Api.Software.SoftwareApprovalStore>().InitializeAsync();

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
app.UseAuthorization();

// Every other route needs a signed-in operator (the fallback policy). Endpoint agents
// authenticate inside the hub with their device token, and Hermes MCP is guarded by
// its own key above, so neither uses the operator sign-in.
app.MapHub<AgentHub>("/agent-hub").RequireAuthorization(OperatorPolicies.AgentConnection);
app.MapHub<DashboardHub>("/dashboard-hub");
app.MapMcp("/internal/mcp").AllowAnonymous();
app.MapOperatorAuth();

// Liveness proves that the process can answer. Readiness additionally indicates
// whether operators currently allow new command work.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResultStatusCodes =
    {
        [HealthStatus.Healthy] = StatusCodes.Status200OK,
        [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,
        [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
    }
}).AllowAnonymous();

app.MapGet("/", () => Results.Ok(new
{
    service = "EndpointAgents.ControlPlane.Api",
    status = "running"
})).AllowAnonymous();
app.MapPost("/api/agent-enrollments", async (HttpContext context, ChatStore store,
    IAuthorizationService authorization,
    Microsoft.Extensions.Options.IOptions<AgentSecurityOptions> security,
    CancellationToken cancellationToken) =>
{
    // Admins issue codes from the dashboard. Scripts on the server itself (for example
    // start-dev-stack.ps1) may too: browsers always send Origin on a POST, so a
    // loopback call without one is a local program, not a web page.
    var localScript = LoopbackRequestGuard.IsLoopback(context.Connection.RemoteIpAddress)
        && !context.Request.Headers.ContainsKey("Origin");
    if (!localScript)
    {
        var operatorResult = await context.AuthenticateAsync(OperatorAuthentication.Scheme);
        if (!operatorResult.Succeeded) return Results.Unauthorized();
        if (!(await authorization.AuthorizeAsync(operatorResult.Principal,
                OperatorPolicies.Admin)).Succeeded)
            return Results.Forbid(authenticationSchemes: [OperatorAuthentication.Scheme]);
    }

    var issue = await store.CreateEnrollmentAsync(
        TimeSpan.FromMinutes(security.Value.EnrollmentLifetimeMinutes), cancellationToken);
    return Results.Ok(issue);
}).AllowAnonymous(); // checked above: admin, or a local script
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
}).AllowAnonymous();
app.MapPost("/api/devices/{agentId}/revoke", async (string agentId, ChatStore store,
    AgentRegistry registry, CancellationToken cancellationToken) =>
{
    await store.RevokeDeviceAsync(agentId, cancellationToken);
    await registry.RevokeAsync(agentId, cancellationToken);
    return Results.NoContent();
}).RequireAuthorization(OperatorPolicies.Admin);
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
}).AllowAnonymous();
app.MapGet("/api/files", async (FileArtifactRepository repository,
    CancellationToken cancellationToken) =>
{
    var ibsng = await repository.GetIbsngDialerAsync(cancellationToken);
    var office = await repository.GetOfficeCatalogAsync(cancellationToken);
    var bugfixes = await repository.GetOfficeBugfixCatalogAsync(cancellationToken);
    var windowsBugfixes = await repository.GetWindowsBugfixCatalogAsync(cancellationToken);
    var files = new List<FileRepositoryItem>();
    if (ibsng is not null)
        files.Add(new FileRepositoryItem("ibsng-dialer", ibsng.FileName, ibsng.SizeBytes,
            ibsng.LastModifiedUtc, ibsng.Sha256, "/api/files/ibsng-dialer",
            "IBSng", "IBSng dialer", "Approved endpoint dialer", "Executable"));
    files.AddRange(office.Artifacts.Select(ToRepositoryItem));
    files.AddRange(bugfixes.Artifacts.Select(ToBugfixRepositoryItem));
    files.AddRange(windowsBugfixes.Artifacts.Select(ToWindowsBugfixRepositoryItem));

    return Results.Ok(new
    {
        files,
        issues = office.Issues.Concat(bugfixes.Issues).Concat(windowsBugfixes.Issues),
        repository = new
        {
            expectedPattern = repository.IbsngDialerFileName,
            officeManifestPath = repository.OfficeManifestPath,
            officeBugfixManifestPath = repository.OfficeBugfixManifestPath,
            windowsBugfixFilePath = repository.WindowsBugfixFilePath,
            windowsBugfixManifestPath = repository.WindowsBugfixManifestPath,
            windowsHelloSampleFilePath = repository.WindowsHelloSampleFilePath,
            protectedAclRequired = repository.ProtectedAclRequired,
            downloadMethod = "GET"
        }
    });
});
// Operator view of BAT package folders; loopback-only like the rest of /api/files.
app.MapGet("/api/files/packages", async (FileArtifactRepository repository,
    CancellationToken cancellationToken) => Results.Ok(new
{
    packagesPath = repository.PackagesRootPath,
    protectedAclRequired = repository.ProtectedAclRequired,
    limits = new
    {
        maxSteps = BatchPlaybookPolicy.MaximumSteps,
        maxResponses = BatchPlaybookPolicy.MaximumTotalResponses,
        maxResponseCharacters = BatchPlaybookPolicy.MaximumResponseCharacters,
        minStepTimeoutSeconds = BatchPlaybookPolicy.MinimumStepTimeoutSeconds,
        maxStepTimeoutSeconds = BatchPlaybookPolicy.MaximumStepTimeoutSeconds,
        defaultGuiDialogGraceSeconds = BatchPlaybookPolicy.DefaultGuiDialogGraceSeconds,
        // Versions 1 and 2 use the endpoint runner's fixed limit (OfficeBugfixRunner);
        // version 3 playbooks set their own timeoutSeconds within these bounds.
        maxRunMinutes = 8,
        defaultRunTimeoutSeconds = BatchPlaybookPolicy.DefaultRunTimeoutSeconds,
        minRunTimeoutSeconds = BatchPlaybookPolicy.MinimumRunTimeoutSeconds,
        maxRunTimeoutSeconds = BatchPlaybookPolicy.MaximumRunTimeoutSeconds,
        maxArguments = BatchPlaybookPolicy.MaximumArguments,
        maxOutputBytes = 1024 * 1024
    },
    packages = await repository.GetPackageStatusesAsync(cancellationToken)
}));
// Endpoint-facing general repair catalog (schema 2 packages).
app.MapGet(PackageRoutes.Catalog, async (FileArtifactRepository repository,
    CancellationToken cancellationToken) => Results.Ok(new PackageCatalog(
        (await repository.GetRepairPackagesAsync(cancellationToken))
            .Select(item => item.Descriptor).ToArray())))
    .RequireAuthorization();
app.MapGet(PackageRoutes.Root + "/{packageId}/file/{fileName}",
    async (string packageId, string fileName, HttpContext context,
        FileArtifactRepository repository, CancellationToken cancellationToken) =>
    {
        var file = await repository.GetRepairPackageSupportFileAsync(packageId,
            fileName, cancellationToken);
        if (file is null) return Results.NotFound();
        context.Response.Headers["X-Artifact-Sha256"] = file.Sha256;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.File(file.FullPath, "application/octet-stream", file.FileName);
    }).RequireAuthorization();
app.MapGet(PackageRoutes.Root + "/{packageId}", async (string packageId,
    HttpContext context, FileArtifactRepository repository,
    CancellationToken cancellationToken) =>
{
    var package = await repository.GetRepairPackageAsync(packageId, cancellationToken);
    if (package is null)
        return Results.NotFound(new { message = "The approved package is not available." });
    context.Response.Headers["X-Artifact-Sha256"] = package.File.Sha256;
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    return Results.File(package.File.FullPath, "application/octet-stream",
        package.File.FileName, package.File.LastModifiedUtc,
        enableRangeProcessing: true);
}).RequireAuthorization();
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
}).AllowAnonymous();
app.MapGet("/api/files/office/catalog", async (FileArtifactRepository repository,
    CancellationToken cancellationToken) =>
{
    var catalog = await repository.GetOfficeCatalogAsync(cancellationToken);
    return Results.Ok(new OfficeRemediationCatalog(
        catalog.Artifacts.Select(item => item.Descriptor).ToArray()));
}).RequireAuthorization();
app.MapGet("/api/files/office/bugfix/catalog", async (
    FileArtifactRepository repository, CancellationToken cancellationToken) =>
{
    var catalog = await repository.GetOfficeBugfixCatalogAsync(cancellationToken);
    return Results.Ok(new OfficeBugfixCatalog(
        catalog.Artifacts.Select(item => item.Descriptor).ToArray()));
}).RequireAuthorization();
app.MapGet("/api/files/office/bugfix/{artifactId}/file/{fileName}",
    async (string artifactId, string fileName, HttpContext context,
        FileArtifactRepository repository, CancellationToken cancellationToken) =>
    {
        var file = await repository.GetOfficeBugfixSupportFileAsync(artifactId,
            fileName, cancellationToken);
        if (file is null) return Results.NotFound();
        context.Response.Headers["X-Artifact-Sha256"] = file.Sha256;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.File(file.FullPath, "application/octet-stream",
            file.FileName);
    }).RequireAuthorization();
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
}).RequireAuthorization();
app.MapGet("/api/files/windows/bugfix/catalog", async (
    FileArtifactRepository repository, CancellationToken cancellationToken) =>
{
    var catalog = await repository.GetWindowsBugfixCatalogAsync(cancellationToken);
    return Results.Ok(new WindowsBugfixCatalog(
        catalog.Artifacts.Select(item => item.Descriptor).ToArray()));
}).RequireAuthorization();
app.MapGet("/api/files/windows/bugfix/{artifactId}/file/{fileName}",
    async (string artifactId, string fileName, HttpContext context,
        FileArtifactRepository repository, CancellationToken cancellationToken) =>
    {
        var file = await repository.GetWindowsBugfixSupportFileAsync(artifactId,
            fileName, cancellationToken);
        if (file is null) return Results.NotFound();
        context.Response.Headers["X-Artifact-Sha256"] = file.Sha256;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.File(file.FullPath, "application/octet-stream",
            file.FileName);
    }).RequireAuthorization();
app.MapGet("/api/files/windows/bugfix/{artifactId}", async (string artifactId,
    HttpContext context, FileArtifactRepository repository,
    CancellationToken cancellationToken) =>
{
    var artifact = await repository.GetWindowsBugfixArtifactAsync(
        artifactId, cancellationToken);
    if (artifact is null)
        return Results.NotFound(new
        {
            message = "The approved Windows artifact is not available."
        });
    context.Response.Headers["X-Artifact-Sha256"] = artifact.File.Sha256;
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    return Results.File(artifact.File.FullPath, "application/octet-stream",
        artifact.File.FileName, artifact.File.LastModifiedUtc,
        enableRangeProcessing: true);
}).RequireAuthorization();
app.MapGet("/api/files/office/{artifactId}/metadata", async (string artifactId,
    FileArtifactRepository repository, CancellationToken cancellationToken) =>
{
    var artifact = await repository.GetOfficeArtifactAsync(artifactId, cancellationToken);
    return artifact is null
        ? Results.NotFound(new { message = "The approved Office artifact is not available." })
        : Results.Ok(ToRepositoryItem(artifact));
}).RequireAuthorization();
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
}).RequireAuthorization();
app.MapActionEndpoints();
ControlPlane.Api.Updates.AgentUpdateEndpoints.MapAgentUpdateEndpoints(app);
ControlPlane.Api.Software.SoftwareEndpoints.MapSoftwareEndpoints(app);
app.MapGet("/api/activity", (int? limit, string? agentId, ActivityStore store) =>
    Results.Ok(store.Get(limit ?? 100, agentId)));
// Requests for abilities the support agent does not have yet (loopback operator view).
app.MapGet("/api/feature-requests", async (int? limit, ChatStore store,
    CancellationToken cancellationToken) =>
    Results.Ok(await store.GetFeatureRequestsAsync(limit ?? 200, cancellationToken)));
app.MapMethods("/api/feature-requests/{requestId}", ["PATCH"], async (string requestId,
    FeatureRequestPatch patch, ChatStore store, CancellationToken cancellationToken) =>
    await store.SetFeatureRequestStatusAsync(requestId, patch.Status, cancellationToken)
        ? Results.NoContent()
        : Results.BadRequest(new { message = "Unknown request or status." }))
    .RequireAuthorization(OperatorPolicies.Reviewer);
app.MapGet("/api/support-requests", (int? limit, string? agentId,
    SupportRequestStore store) => Results.Ok(store.Get(limit ?? 100, agentId)));
app.MapGet("/api/helpdesk/conversations", async (int? limit, string? status, string? query,
    ChatStore store, AgentRegistry registry, CancellationToken cancellationToken) =>
{
    var conversations = await store.GetHelpdeskConversationsAsync(limit ?? 200,
        status, query, cancellationToken);
    var online = registry.GetOnlineAgents().ToDictionary(item => item.AgentId,
        StringComparer.OrdinalIgnoreCase);
    return Results.Ok(conversations.Select(item => EnrichHelpdesk(item, online)).ToArray());
});
app.MapGet("/api/helpdesk/conversations/{conversationId}", async (string conversationId,
    long? afterSequence, ChatStore store, AgentRegistry registry,
    CancellationToken cancellationToken) =>
{
    var detail = await store.GetHelpdeskConversationAsync(conversationId,
        afterSequence ?? 0, cancellationToken);
    if (detail is null) return Results.NotFound();
    var online = registry.GetOnlineAgents().ToDictionary(item => item.AgentId,
        StringComparer.OrdinalIgnoreCase);
    return Results.Ok(detail with { Conversation = EnrichHelpdesk(detail.Conversation, online) });
});
app.MapPost("/api/helpdesk/conversations/{conversationId}/messages", async (
    string conversationId, HelpdeskOperatorMessageRequest request, ChatStore store,
    DashboardPublisher dashboard, CancellationToken cancellationToken) =>
{
    var receipt = await store.SubmitOperatorMessageAsync(conversationId, request,
        cancellationToken);
    if (!receipt.Accepted) return Results.BadRequest(receipt);
    await dashboard.PublishHelpdeskAsync(new HelpdeskChange(conversationId,
        "operator-message", receipt.Sequence), cancellationToken);
    return Results.Ok(receipt);
});
app.MapMethods("/api/helpdesk/conversations/{conversationId}", ["PATCH"], async (
    string conversationId, HelpdeskConversationPatch patch, ChatStore store,
    DashboardPublisher dashboard, CancellationToken cancellationToken) =>
{
    if (!await store.PatchHelpdeskConversationAsync(conversationId, patch, cancellationToken))
        return Results.BadRequest(new { message = "Conversation or requested state is invalid." });
    await dashboard.PublishHelpdeskAsync(new HelpdeskChange(conversationId, "state"),
        cancellationToken);
    return Results.NoContent();
});
app.MapPost("/api/helpdesk/conversations/{conversationId}/diagnostics", async (
    string conversationId, HelpdeskDiagnosticRequest diagnostic, ChatStore store,
    AgentRegistry registry, CommandCatalog catalog, ControlPlaneRuntimeState runtime,
    DashboardPublisher dashboard, CancellationToken cancellationToken) =>
{
    var readOnlyDiagnostics = new HashSet<string>(StringComparer.Ordinal)
    {
        "endpoint.get_info", EndpointAgents.Shared.Support.SystemInfoContracts.Method,
        "system.windows_activation_status", "software.office_inventory", "software.office_activation_status",
        "software.office_runtime_status",
        "network.ibsng_diagnose"
    };
    if (!readOnlyDiagnostics.Contains(diagnostic.Method) || !catalog.Contains(diagnostic.Method))
        return Results.BadRequest(new { message = "Only approved read-only diagnostics are available here." });
    if (SensitiveCommandParameterGuard.ContainsSensitiveData(diagnostic.Parameters))
        return Results.BadRequest(new { message = "Diagnostic parameters cannot contain credentials." });
    if (runtime.IsMaintenanceMode)
        return Results.Json(new { message = "Diagnostics are paused during maintenance." },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    var conversation = await store.GetHelpdeskConversationAsync(conversationId,
        long.MaxValue, cancellationToken);
    if (conversation is null) return Results.NotFound();
    var request = RpcRequest.Create($"helpdesk-{Guid.NewGuid():N}", diagnostic.Method,
        diagnostic.Parameters);
    var response = await registry.SendCommandAsync(conversation.Conversation.AgentId,
        request, cancellationToken);
    await store.AppendDiagnosticTraceAsync(conversationId,
        conversation.Conversation.AgentId, request, response, cancellationToken);
    await dashboard.PublishHelpdeskAsync(new HelpdeskChange(conversationId, "diagnostic"),
        cancellationToken);
    return response.Error?.Code == 404 ? Results.NotFound(response) : Results.Ok(response);
});
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
}).RequireAuthorization(OperatorPolicies.Admin);

app.MapPost("/api/agents/{agentId}/commands", async (string agentId, RpcRequest request,
    AgentRegistry registry, CommandCatalog catalog, ControlPlaneRuntimeState runtime,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Method) || request.JsonRpc != "2.0")
        return Results.BadRequest(RpcResponse.Failure(request.Id ?? string.Empty, -32600, "Invalid JSON-RPC request."));
    if (SensitiveCommandParameterGuard.ContainsReservedActionAuthorization(request.Params))
        return Results.BadRequest(RpcResponse.Failure(request.Id, 400,
            "Reserved action-authorization parameters are not accepted by this command endpoint."));
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
}).RequireAuthorization(OperatorPolicies.Admin);

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

static HelpdeskConversationSummary EnrichHelpdesk(HelpdeskConversationSummary item,
    IReadOnlyDictionary<string, OnlineAgentDto> online) =>
    online.TryGetValue(item.AgentId, out var agent)
        ? item with
        {
            MachineName = agent.MachineName,
            Version = agent.Version,
            IsOnline = true
        }
        : item;

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
    true,
    OfficeProductReleaseIds: artifact.Descriptor.OfficeProductReleaseIds,
    MinWindowsVersion: artifact.Descriptor.MinWindowsVersion,
    MaxWindowsVersionExclusive: artifact.Descriptor.MaxWindowsVersionExclusive,
    AdditionalOfficeCompatibility: artifact.Descriptor.AdditionalCompatibility);

static FileRepositoryItem ToWindowsBugfixRepositoryItem(
    WindowsBugfixFileArtifact artifact) => new(
    artifact.Descriptor.ArtifactId,
    artifact.File.FileName,
    artifact.File.SizeBytes,
    artifact.File.LastModifiedUtc,
    artifact.File.Sha256,
    artifact.Descriptor.DownloadPath,
    "Windows",
    artifact.Descriptor.DisplayName,
    artifact.Descriptor.Description,
    "AdministratorApprovedSha256",
    RequiresInteractiveUser: true,
    MinWindowsVersion: artifact.Descriptor.MinWindowsVersion,
    MaxWindowsVersionExclusive: artifact.Descriptor.MaxWindowsVersionExclusive,
    EditionIds: artifact.Descriptor.EditionIds,
    AdditionalWindowsCompatibility: artifact.Descriptor.AdditionalCompatibility);

public partial class Program;
