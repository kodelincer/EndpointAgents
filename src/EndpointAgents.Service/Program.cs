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

builder.Services.Configure<ControlPlaneOptions>(
    builder.Configuration.GetSection("ControlPlane"));

builder.Services.AddSingleton<CommandDispatcher>();

builder.Services.AddSingleton<IEndpointCommandHandler, EndpointInfoCommandHandler>();

builder.Services.AddHostedService<AgentWorker>();

var host = builder.Build();

host.Run();
