using EndpointAgents.Platform.Windows.Actions;
using EndpointAgents.Shared.LocalIpc;

namespace EndpointAgents.Tray;

/// <summary>
/// Runs one catalogued user-session task the service sent. Only the methods listed in
/// <see cref="TrayIpcProtocol"/> are recognised; anything else is refused without side effects.
/// </summary>
public sealed class TrayTaskDispatcher
{
    private readonly IbsngDialerLauncher _launcher;
    private readonly OfficeRemediationLauncher _officeRemediation;
    private readonly WindowsRemediationConsent _windowsRemediation;
    private readonly WindowsHelloLauncher _windowsHello;
    private readonly UserActionHost _userActions = new();

    public TrayTaskDispatcher(IUserConsentPrompt? consent = null)
    {
        consent ??= new MessageBoxConsentPrompt();
        _launcher = new IbsngDialerLauncher(consent: consent);
        _officeRemediation = new OfficeRemediationLauncher(consent);
        _windowsRemediation = new WindowsRemediationConsent(consent);
        _windowsHello = new WindowsHelloLauncher(consent);
    }

    public async Task<TrayIpcResponse> DispatchAsync(TrayIpcRequest request,
        CancellationToken cancellationToken)
    {
        TrayEventLogger.Information(
            $"درخواست محلی '{request.Method}' با شناسه {request.Id} دریافت شد.");
        TrayIpcResponse response;
        try
        {
            response = request.Method switch
            {
                TrayIpcProtocol.LaunchIbsngMethod
                    or TrayIpcProtocol.ConfirmIbsngInstallMethod
                    or TrayIpcProtocol.ReconcileIbsngMethod
                    or TrayIpcProtocol.InspectIbsngMethod
                    or TrayIpcProtocol.ReadIbsngUiMethod =>
                    await _launcher.LaunchAsync(request, cancellationToken),
                TrayIpcProtocol.OfficeRemediationLaunchMethod =>
                    await _officeRemediation.LaunchAsync(request, cancellationToken),
                TrayIpcProtocol.OfficeBugfixConsentMethod =>
                    await _officeRemediation.ConfirmBugfixAsync(request, cancellationToken),
                TrayIpcProtocol.WindowsBugfixConsentMethod =>
                    await _windowsRemediation.ConfirmAsync(request, cancellationToken),
                TrayIpcProtocol.WindowsHelloLaunchMethod =>
                    await _windowsHello.LaunchAsync(request, cancellationToken),
                TrayIpcProtocol.EndpointUserActionMethod => new TrayIpcResponse(request.Id, true,
                    "Completed", ActionResult: await _userActions.RunAsync(request.Action,
                        cancellationToken)),
                _ => new TrayIpcResponse(request.Id, false, "UnknownMethod")
            };
        }
        catch (OperationCanceledException)
        {
            response = new TrayIpcResponse(request.Id, false, "Cancelled");
        }
        catch (Exception ex)
        {
            TrayEventLogger.Error("اجرای درخواست محلی برنامه ناحیه اعلان ناموفق بود: " +
                $"{ex.GetType().Name}: {ex.Message}");
            response = new TrayIpcResponse(request.Id, false, "TaskFailed",
                Message: ex.GetType().Name);
        }

        var message = $"درخواست محلی '{request.Method}' با شناسه {request.Id} " +
            $"با وضعیت {response.State} پایان یافت.";
        if (response.Success) TrayEventLogger.Information(message);
        else TrayEventLogger.Warning(message);
        return response with { Id = request.Id };
    }
}
