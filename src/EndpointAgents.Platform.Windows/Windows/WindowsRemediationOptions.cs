using EndpointAgents.Shared.Windows;

namespace EndpointAgents.Platform.Windows.Windows;

public sealed class WindowsRemediationOptions
{
    public string StagingRoot { get; set; } = WindowsRemediationPaths.StagingRoot;

    public bool RequireProtectedAcl { get; set; } = true;

    public bool RequireAdministrator { get; set; } = true;
}
