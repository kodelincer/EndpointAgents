using EndpointAgents.Shared.Support;
using System.ServiceProcess;

namespace EndpointAgents.Tray;

public enum EndpointServiceState
{
    Unknown,
    NotInstalled,
    Stopped,
    StartPending,
    StopPending,
    Running,
    Interactive
}

public static class EndpointServiceStateResolver
{
    public static EndpointServiceState ResolveObservedState(
        EndpointServiceState serviceControlManagerState, bool ipcAvailable) =>
        ipcAvailable && serviceControlManagerState is EndpointServiceState.Unknown
            or EndpointServiceState.NotInstalled or EndpointServiceState.Stopped
            ? EndpointServiceState.Interactive
            : serviceControlManagerState;

    public static bool IsOperational(EndpointServiceState state) =>
        state is EndpointServiceState.Running or EndpointServiceState.Interactive;
}

public sealed class WindowsServiceManager
{
    public EndpointServiceState GetStatus()
    {
        try
        {
            using var service = new ServiceController(EndpointAgentProduct.ServiceName, ".");
            service.Refresh();
            return service.Status switch
            {
                ServiceControllerStatus.Stopped => EndpointServiceState.Stopped,
                ServiceControllerStatus.StartPending => EndpointServiceState.StartPending,
                ServiceControllerStatus.StopPending => EndpointServiceState.StopPending,
                ServiceControllerStatus.Running => EndpointServiceState.Running,
                _ => EndpointServiceState.Unknown
            };
        }
        catch (InvalidOperationException)
        {
            return EndpointServiceState.NotInstalled;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var service = new ServiceController(EndpointAgentProduct.ServiceName, ".");
        service.Refresh();
        if (service.Status == ServiceControllerStatus.Running) return;
        service.Start();
        await WaitForAsync(service, ServiceControllerStatus.Running, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        using var service = new ServiceController(EndpointAgentProduct.ServiceName, ".");
        service.Refresh();
        if (service.Status == ServiceControllerStatus.Stopped) return;
        if (!service.CanStop) throw new InvalidOperationException("امکان توقف سرویس دستیار پشتیبانی وجود ندارد.");
        service.Stop();
        await WaitForAsync(service, ServiceControllerStatus.Stopped, cancellationToken);
    }

    private static async Task WaitForAsync(ServiceController service,
        ServiceControllerStatus expected, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            service.Refresh();
            if (service.Status == expected) return;
            await Task.Delay(250, cancellationToken);
        }
        throw new System.TimeoutException($"سرویس در مدت ۳۰ ثانیه به وضعیت {expected} نرسید.");
    }
}
