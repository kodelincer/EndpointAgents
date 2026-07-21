namespace EndpointAgents.Platform.Windows.Ibsng;

public sealed class IbsngAtomicInstaller
{
    public void Install(string verifiedStagingPath, string destinationPath)
    {
        var stagingDirectory = Path.GetFullPath(Path.GetDirectoryName(verifiedStagingPath)!);
        var destinationDirectory = Path.GetFullPath(Path.GetDirectoryName(destinationPath)!);
        if (!stagingDirectory.Equals(destinationDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The verified IBSng staging file must be on the destination volume and directory.");

        File.Move(verifiedStagingPath, destinationPath, overwrite: false);
    }
}
