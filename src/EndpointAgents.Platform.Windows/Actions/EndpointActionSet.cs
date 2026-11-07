using EndpointAgents.Platform.Windows.Actions.SystemSide;
using EndpointAgents.Platform.Windows.Actions.UserSide;
using EndpointAgents.Shared.Actions;
using EndpointAgents.Shared.LocalIpc;
using System.Runtime.Versioning;
using System.Text.Json;

namespace EndpointAgents.Platform.Windows.Actions;

/// <summary>Every implemented action, split by where it runs.</summary>
[SupportedOSPlatform("windows")]
public static class EndpointActionSet
{
    /// <summary>Actions the LocalSystem service runs.</summary>
    public static IReadOnlyList<IEndpointAction> CreateSystemActions() =>
    [
        new NetworkStatusAction(), new NetworkTriageAction(), new DnsResolveAction(),
        new TestPortAction(), new PingAction(), new HostsCheckAction(), new DnsFlushAction(),
        new DhcpRenewAction(), new RegisterDnsAction(), new ArpClearAction(),
        new DomainHealthAction(), new TimeStatusAction(), new TimeResyncAction(),
        new ComputerPolicyRefreshAction(),
        new SpoolerRestartAndClearAction(), new CertificateTrustAction(),
        new RecentChangesAction(), new PerformanceSnapshotAction(), new EventsSummaryAction(),
        new RestartReasonsAction(), new AppErrorsAction(),
        new ServicesStatusAction(), new ServiceRestartAction(), new WindowsUpdateStatusAction(),
        new SecurityStatusAction(), new DefenderQuickScanAction(), new DeviceProblemsAction(),
        new DevicesRescanAction(), new AppsFindAction(), new DiskCleanupSafeAction(),
        new TimeZoneSetStandardAction(), new TimeZoneRestoreAction(), new PowerStatusAction(),
        new RebootScheduleAction(), new RebootCancelAction(),
        new NetworkAdaptersAction(), new NetworkAdapterEnableAction(),
        new NetworkAdapterRestartAction(), new DnsResetToDhcpAction(), new DnsRestoreAction(),
        new SystemProxyGetAction(), new SystemProxyResetAction(), new SystemProxyRestoreAction(),
        new VpnStatusAction(), new VpnClientRestartAction(),
        new SoftwareInventoryAction(), new SoftwareUninstallAction(),
        new DriversInventoryAction(), new DeviceEnableAction(), new DeviceRestartAction(),
        new DiskUsageAction(), new RestartPendingAction(),
        new NetworkCategoryAction(), new NetworkSetPrivateAction(), new NetworkSetPublicAction(),
        new IpResetToDhcpAction(), new IpRestoreAction(), new NetworkStackResetAction(),
        new WifiStatusAction(), new WifiConnectCompanyAction(),
        new WindowsUpdateResetAction(), new WindowsUpdateScanNowAction(),
        new DefenderUpdateSignaturesAction(), new DefenderThreatsAction(), new BitLockerStatusAction(),
        new DiskHealthAction(), new SystemFileCheckAction(), new SystemImageRepairAction(),
        new SystemRepairStatusAction(), new BatteryReportAction(), new PowerSetPlanStandardAction(),
        new PowerRestorePlanAction(), new OfficeQuickRepairAction(),
        new KmsCheckAction(), new WindowsKmsActivateAction(), new OfficeKmsActivateAction(),
        new TimeSetNtpAction()
    ];

    /// <summary>Actions the Tray runs with the signed-in user's own token.</summary>
    public static IReadOnlyList<IEndpointAction> CreateUserActions() =>
    [
        new WebProbeAction(), new ProxyGetAction(), new ProxyResetAction(),
        new ProxyRestoreAction(), new UserPolicyRefreshAction(), new KerberosPurgeAction(),
        new CredentialListAction(), new CredentialRemoveAction(),
        new PrinterListAction(), new PrinterSetDefaultAction(), new PrinterRestoreDefaultAction(),
        new PrinterAddAction(), new PrinterRemoveAction(), new PrinterTestPageAction(),
        new DrivesListAction(), new ShareTestAction(), new DriveRemapAction(),
        new ProfileStatusAction(), new BrowserInventoryAction(), new BrowserClearCacheAction(),
        new OutlookStatusAction(), new ExplorerStatusAction(),
        new ExplorerRestartAction(), new InputLanguagesAction(), new InputAddLayoutAction(),
        new RegionStatusAction(), new DisplayStatusAction(), new MediaStatusAction(),
        new AllowCameraMicAction(), new AppCloseAction(),
        new AudioStatusAction(), new AudioUnmuteAction(), new AudioSetDefaultAction(),
        new NoticeShowAction(),
        new AudioRestoreDefaultAction(), new PrinterStatusAction(), new WhoAmIGroupsAction(),
        new SiteCheckAccessAction(), new ZonesStatusAction(), new ZonesAddCompanySiteAction(),
        new ZonesRemoveCompanySiteAction(), new StartupListAction(), new StartupDisableAction(),
        new StartupEnableAction(), new RecycleBinEmptyAction(), new OfficeAddinsListAction(),
        new OfficeAddinDisableAction(), new OfficeAddinEnableAction(), new OutlookRebuildOstAction()
    ];
}

/// <summary>Runs one user-context action inside the Tray process.</summary>
[SupportedOSPlatform("windows")]
public sealed class UserActionHost
{
    private readonly IReadOnlyDictionary<string, IEndpointAction> _actions =
        EndpointActionSet.CreateUserActions().ToDictionary(item => item.Id, StringComparer.Ordinal);

    public async Task<EndpointActionResult> RunAsync(EndpointActionRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return EndpointActionResult.Failed("No action was sent.");
        if (!EndpointActionRequestParser.TryCreateInvocation(request, ActionRunAs.User,
                out var invocation, out var error))
            return EndpointActionResult.Failed(error);
        if (!_actions.TryGetValue(request.ActionId, out var action))
            return EndpointActionResult.Failed("The action is not available in this Tray version.");
        var result = await EndpointActionRequestParser.RunAsync(action, invocation!,
            cancellationToken);
        return FitsMessage(result) ? result
            : result with { Data = null, Summary = result.Summary + " (details too long to send)" };
    }

    private static bool FitsMessage(EndpointActionResult result) =>
        JsonSerializer.Serialize(result).Length <= TrayIpcProtocol.MaxActionResultCharacters;
}
