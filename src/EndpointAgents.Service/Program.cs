using EndpointAgents.Core.Commands;
using EndpointAgents.Platform.Windows.EndpointInfo;
using EndpointAgents.Service;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]

// Use the generic host so the process can run interactively during development
// and as a Windows Service in production.
var builder = Host.CreateApplicationBuilder(args);

// Registers Windows Service lifetime integration; host.Run() starts the service
// after all hosted services have been built.
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "EndpointAgents Service";
});

builder.Services.AddOptions<ControlPlaneOptions>()
    .Bind(builder.Configuration.GetSection("ControlPlane"))
    .Validate(options =>
        Uri.TryCreate(options.HubUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps),
        "ControlPlane:HubUrl must be an absolute HTTP or HTTPS URL.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Version),
        "ControlPlane:Version is required.")
    .Validate(options => options.CommandTimeoutSeconds is >= 1 and <= 3600,
        "ControlPlane:CommandTimeoutSeconds must be between 1 and 3600.")
    .Validate(options => options.MaxConcurrentCommands is >= 1 and <= 32,
        "ControlPlane:MaxConcurrentCommands must be between 1 and 32.")
    .ValidateOnStart();

// These shared services are consumed by AgentWorker and the command handlers.
builder.Services.AddSingleton<CommandDispatcher>();
builder.Services.AddSingleton<AgentIdentityProvider>();

// Only the endpoint diagnostics handler is enabled by this service currently.
builder.Services.AddSingleton<IEndpointCommandHandler, EndpointInfoCommandHandler>();

// AgentWorker owns the SignalR connection and receives remote commands.
builder.Services.AddHostedService<AgentWorker>();

var host = builder.Build();

host.Run();
