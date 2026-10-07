using EndpointAgents.Core.Commands;
using EndpointAgents.Platform.Windows.EndpointInfo;
using EndpointAgents.Platform.Windows;
using EndpointAgents.Platform.Windows.Ibsng;
using EndpointAgents.Platform.Windows.Office;
using EndpointAgents.Platform.Windows.SystemInfo;
using EndpointAgents.Platform.Windows.UserSessions;
using EndpointAgents.Platform.Windows.Windows;
using EndpointAgents.Shared.Office;
using EndpointAgents.Service;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Configuration;
using Microsoft.Extensions.Logging.EventLog;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]

// Use the generic host so the process can run interactively during development
// and as a Windows Service in production.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddProvider(new EndpointActivityLoggerProvider());

// Registers Windows Service lifetime integration; host.Run() starts the service
// after all hosted services have been built.
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "EndpointAgents Service";
});
LoggerProviderOptions.RegisterProviderOptions<EventLogSettings, EventLogLoggerProvider>(
    builder.Services);

builder.Services.AddOptions<ControlPlaneOptions>()
    .Bind(builder.Configuration.GetSection("ControlPlane"))
    .Validate(options =>
        Uri.TryCreate(options.HubUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps),
        "ControlPlane:HubUrl must be an absolute HTTP or HTTPS URL.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Version),
        "ControlPlane:Version is required.")
    .Validate(options => Path.IsPathFullyQualified(options.DeviceCredentialPath),
        "ControlPlane:DeviceCredentialPath must be an absolute path.")
    .Validate(options => options.CommandTimeoutSeconds is >= 1 and <= 3600,
        "ControlPlane:CommandTimeoutSeconds must be between 1 and 3600.")
    .Validate(options => options.MaxConcurrentCommands is >= 1 and <= 32,
        "ControlPlane:MaxConcurrentCommands must be between 1 and 32.")
    .ValidateOnStart();

builder.Services.AddOptions<EndpointInfoOptions>()
    .Bind(builder.Configuration.GetSection("Diagnostics:EndpointInfo"))
    .Validate(options => Uri.TryCreate(options.InternetCheckUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps),
        "Diagnostics:EndpointInfo:InternetCheckUrl must be an absolute HTTP or HTTPS URL.")
    .Validate(options => options.InternetCheckTimeoutSeconds is >= 1 and <= 60,
        "Diagnostics:EndpointInfo:InternetCheckTimeoutSeconds must be between 1 and 60.")
    .ValidateOnStart();

builder.Services.AddOptions<IbsngOptions>()
    .Bind(builder.Configuration.GetSection("Diagnostics:Ibsng"))
    .Validate(options => !string.IsNullOrWhiteSpace(options.DialerFileName)
        && Path.GetFileName(options.DialerFileName) == options.DialerFileName
        && options.DialerFileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
        "Diagnostics:Ibsng:DialerFileName must be an executable file name.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.DialerSearchPattern)
        && Path.GetFileName(options.DialerSearchPattern) == options.DialerSearchPattern
        && options.DialerSearchPattern.EndsWith(".exe", StringComparison.OrdinalIgnoreCase),
        "Diagnostics:Ibsng:DialerSearchPattern must be an executable search pattern without a path.")
    .Validate(options => Uri.TryCreate(options.ArtifactApiPath, UriKind.RelativeOrAbsolute, out _),
        "Diagnostics:Ibsng:ArtifactApiPath must be a valid URI path.")
    .Validate(options => options.MaxDownloadBytes is >= 1 and <= 1024L * 1024 * 1024,
        "Diagnostics:Ibsng:MaxDownloadBytes must be between 1 byte and 1 GiB.")
    .Validate(options => string.IsNullOrWhiteSpace(options.TrustedSha256)
        || NormalizeSha256(options.TrustedSha256) is not null,
        "Diagnostics:Ibsng:TrustedSha256 must be a 64-character SHA-256 value when configured.")
    .Validate(options => options.ProbeTimeoutSeconds is >= 1 and <= 30,
        "Diagnostics:Ibsng:ProbeTimeoutSeconds must be between 1 and 30.")
    .Validate(options => options.MaxProbeTargets is >= 1 and <= 32,
        "Diagnostics:Ibsng:MaxProbeTargets must be between 1 and 32.")
    .Validate(options => Path.IsPathFullyQualified(options.QuarantineRoot),
        "Diagnostics:Ibsng:QuarantineRoot must be an absolute path.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.TrayPipePrefix)
        && options.TrayPipePrefix.IndexOfAny(Path.GetInvalidFileNameChars()) < 0,
        "Diagnostics:Ibsng:TrayPipePrefix must be a valid pipe-name prefix.")
    .Validate(options => options.TrayConnectTimeoutSeconds is >= 1 and <= 30,
        "Diagnostics:Ibsng:TrayConnectTimeoutSeconds must be between 1 and 30.")
    .Validate(options => options.TrayResponseTimeoutSeconds is >= 1 and <= 120,
        "Diagnostics:Ibsng:TrayResponseTimeoutSeconds must be between 1 and 120.")
    .ValidateOnStart();

builder.Services.AddOptions<OfficeRemediationOptions>()
    .Bind(builder.Configuration.GetSection("Diagnostics:OfficeRemediation"))
    .Validate(options => Path.GetFullPath(options.StagingRoot).Equals(
            Path.GetFullPath(OfficeRemediationPaths.StagingRoot),
            StringComparison.OrdinalIgnoreCase),
        $"Diagnostics:OfficeRemediation:StagingRoot must be {OfficeRemediationPaths.StagingRoot}.")
    .Validate(options => options.MaxDownloadBytes is >= 1 and <= 1024L * 1024 * 1024,
        "Diagnostics:OfficeRemediation:MaxDownloadBytes must be between 1 byte and 1 GiB.")
    .ValidateOnStart();

builder.Services.AddOptions<WindowsRemediationOptions>()
    .Bind(builder.Configuration.GetSection("Diagnostics:WindowsRemediation"))
    .Validate(options => !string.IsNullOrWhiteSpace(options.StagingRoot),
        "Diagnostics:WindowsRemediation:StagingRoot is required.")
    .Validate(options => builder.Environment.IsDevelopment()
        || Path.GetFullPath(options.StagingRoot).Equals(
            Path.GetFullPath(EndpointAgents.Shared.Windows.WindowsRemediationPaths.StagingRoot),
            StringComparison.OrdinalIgnoreCase)
        && options.RequireProtectedAcl
        && options.RequireAdministrator,
        "Unprotected, non-administrator Windows remediation is allowed only in Development.")
    .ValidateOnStart();

// These shared services are consumed by AgentWorker and the command handlers.
builder.Services.AddSingleton<CommandDispatcher>();
builder.Services.AddSingleton<AgentIdentityProvider>();
builder.Services.AddSingleton<AgentCredentialProvider>();
builder.Services.AddSingleton<AgentRuntimeState>();
builder.Services.AddSingleton<InteractiveUserSessionLocator>();
builder.Services.AddSingleton<IbsngArtifactVerifier>();
builder.Services.AddSingleton<IbsngArtifactQuarantine>();
builder.Services.AddSingleton<IbsngDesktopReconciler>();
builder.Services.AddSingleton<IbsngCommandCoordinator>();
builder.Services.AddSingleton<IbsngAtomicInstaller>();
builder.Services.AddSingleton<RasConnectionInspector>();
builder.Services.AddSingleton<IbsngConnectionInspector>();
builder.Services.AddSingleton<IbsngDiagnosticService>();
builder.Services.AddSingleton<IOfficeInstallationSource, WindowsOfficeInstallationSource>();
builder.Services.AddSingleton<OfficeInventoryCollector>();
builder.Services.AddSingleton<IOfficeUserSessionSource, OfficeUserSessionSource>();
builder.Services.AddSingleton<OfficeRuntimeInspector>();
builder.Services.AddSingleton<IOfficeVolumeActivationProbe, OfficeVolumeActivationProbe>();
builder.Services.AddSingleton<IOfficeActivationCatalogClient, OfficeActivationCatalogClient>();
builder.Services.AddSingleton<IOfficeBugfixCatalogClient, OfficeBugfixCatalogClient>();
builder.Services.AddSingleton<OfficeActivationService>();
builder.Services.AddSingleton<OfficeArtifactDownloader>();
builder.Services.AddSingleton<OfficeBugfixRunner>();
builder.Services.AddSingleton<IWindowsActivationProbe, WindowsActivationProbe>();
builder.Services.AddSingleton<IEndpointArtifactAuthorizationProvider,
    WindowsArtifactAuthorizationProvider>();
builder.Services.AddSingleton<EndpointArtifactTransport>();
builder.Services.AddSingleton<WindowsArtifactClient>();
builder.Services.AddSingleton<WindowsBugfixRunner>();
builder.Services.AddSingleton<ITrayCommandClient, TrayCommandClient>();
builder.Services.AddSingleton(provider => new Uri(
    provider.GetRequiredService<IOptions<ControlPlaneOptions>>().Value.HubUrl));

// Only explicitly registered handlers are remotely callable.
builder.Services.AddSingleton<IEndpointCommandHandler, EndpointInfoCommandHandler>();
builder.Services.AddSingleton<IEndpointCommandHandler, IbsngDiagnoseCommandHandler>();
builder.Services.AddSingleton<IEndpointCommandHandler, IbsngEnsureDialerCommandHandler>();
builder.Services.AddSingleton<IEndpointCommandHandler, IbsngLaunchCommandHandler>();
builder.Services.AddSingleton<IEndpointCommandHandler, OfficeInventoryCommandHandler>();
builder.Services.AddSingleton<IEndpointCommandHandler, OfficeActivationStatusCommandHandler>();
builder.Services.AddSingleton<IEndpointCommandHandler, OfficeRuntimeStatusCommandHandler>();
builder.Services.AddSingleton<IEndpointCommandHandler, OfficeRemediationCommandHandler>();
builder.Services.AddSingleton<IEndpointCommandHandler, OfficeBugfixCommandHandler>();
builder.Services.AddSingleton<IEndpointCommandHandler, WindowsActivationStatusCommandHandler>();
builder.Services.AddSingleton<IEndpointCommandHandler, WindowsActivationBugfixCommandHandler>();
builder.Services.AddSingleton<IEndpointCommandHandler, WindowsHelloSampleCommandHandler>();
builder.Services.AddSingleton<IEndpointCommandHandler, SystemInfoCommandHandler>();

// Read-only system.get_info sections; add a new ISystemInfoSection here to extend it.
builder.Services.AddSingleton<ISystemInfoSection, NetworkInfoSection>();
builder.Services.AddSingleton<ISystemInfoSection, WindowsInfoSection>();
builder.Services.AddSingleton<ISystemInfoSection, DomainInfoSection>();
builder.Services.AddSingleton<ISystemInfoSection, HardwareInfoSection>();
builder.Services.AddSingleton<ISystemInfoSection, StorageInfoSection>();

// AgentWorker owns the SignalR connection. Register the concrete singleton so
// the local IPC bridge can submit helpdesk requests through the same connection.
builder.Services.AddSingleton<AgentWorker>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<AgentWorker>());
builder.Services.AddHostedService<ServiceIpcServer>();

var host = builder.Build();

host.Run();

static string? NormalizeSha256(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
        return null;

    var normalized = value.Trim().Replace("-", string.Empty, StringComparison.Ordinal);
    return normalized.Length == 64 && normalized.All(Uri.IsHexDigit) ? normalized : null;
}
