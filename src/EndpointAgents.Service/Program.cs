using EndpointAgents.Core.Commands;
using EndpointAgents.Platform.Windows.EndpointInfo;
using EndpointAgents.Service;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]

var builder = Host.CreateApplicationBuilder(args);

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

builder.Services.AddSingleton<CommandDispatcher>();
builder.Services.AddSingleton<AgentIdentityProvider>();

builder.Services.AddSingleton<IEndpointCommandHandler, EndpointInfoCommandHandler>();

builder.Services.AddHostedService<AgentWorker>();

var host = builder.Build();

host.Run();
