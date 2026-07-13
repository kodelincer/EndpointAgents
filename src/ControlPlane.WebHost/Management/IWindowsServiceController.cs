namespace ControlPlane.WebHost.Management;

/// <summary>
/// Abstracts Windows SCM access so lifecycle orchestration can be tested without
/// installing or mutating a real service.
/// </summary>
public interface IWindowsServiceController
{
    Task<ManagedServiceState> GetStatusAsync(CancellationToken cancellationToken);
    Task StartAsync(TimeSpan timeout, CancellationToken cancellationToken);
    Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken);
    Task RestartAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
