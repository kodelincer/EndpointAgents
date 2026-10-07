namespace EndpointAgents.Platform.Windows.SystemInfo;

/// <summary>
/// One read-only part of the <c>system.get_info</c> report. To teach the endpoint a new
/// fact, add a section class, register it in the Service, and add its name to
/// <see cref="EndpointAgents.Shared.Support.SystemInfoContracts"/>. Sections must never
/// change the machine, prompt the user, or return credentials.
/// </summary>
public interface ISystemInfoSection
{
    /// <summary>Section name from <see cref="EndpointAgents.Shared.Support.SystemInfoContracts"/>.</summary>
    string Name { get; }

    Task<object?> CollectAsync(SystemInfoWarnings warnings, CancellationToken cancellationToken);
}
