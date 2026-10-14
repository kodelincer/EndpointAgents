using System.Runtime.Versioning;
using System.ServiceProcess;

namespace EndpointAgents.Platform.Windows.Actions.Infrastructure;

/// <summary>Start/stop helpers for the catalogued services.</summary>
[SupportedOSPlatform("windows")]
public static class ServiceTools
{
    public static async Task EnsureRunningAsync(string name, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var service = new ServiceController(name);
        if (service.Status == ServiceControllerStatus.Running) return;
        if (service.Status is ServiceControllerStatus.Stopped)
            service.Start();
        await WaitAsync(service, ServiceControllerStatus.Running, timeout, cancellationToken);
    }

    public static async Task StopAsync(ServiceController service, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (service.Status == ServiceControllerStatus.Stopped) return;
        if (service.CanStop) service.Stop();
        await WaitAsync(service, ServiceControllerStatus.Stopped, timeout, cancellationToken);
    }

    public static async Task WaitAsync(ServiceController service,
        ServiceControllerStatus wanted, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            service.Refresh();
            if (service.Status == wanted) return;
            await Task.Delay(250, cancellationToken);
        }
        throw new global::System.TimeoutException($"{service.ServiceName} did not reach {wanted}.");
    }
}
