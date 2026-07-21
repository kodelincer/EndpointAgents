namespace EndpointAgents.Platform.Windows.Office;

public sealed class OfficeRemediationOptions
{
    public string StagingRoot { get; set; } =
        EndpointAgents.Shared.Office.OfficeRemediationPaths.StagingRoot;

    public long MaxDownloadBytes { get; set; } = 250 * 1024 * 1024;
}
