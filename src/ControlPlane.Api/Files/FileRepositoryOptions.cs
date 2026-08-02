namespace ControlPlane.Api.Files;

/// <summary>Configuration for operator-managed files that endpoints may download.</summary>
public sealed class FileRepositoryOptions
{
    public string RootPath { get; set; } =
        @"C:\ProgramData\EndpointAgents\ControlPlane\FileRepository";

    public string IbsngDialerFileName { get; set; } = "IBSngDialer-pap(1).exe";

    public string OfficeDirectoryName { get; set; } = "Office";

    public string OfficeManifestFileName { get; set; } = "office-artifacts.json";

    public string OfficeBugfixFileName { get; set; } = "office-activation-repair.bat";

    public long MaxFileSizeBytes { get; set; } = 100 * 1024 * 1024;
}
