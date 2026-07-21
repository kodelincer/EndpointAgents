namespace EndpointAgents.Platform.Windows.Ibsng;

public sealed class IbsngArtifactQuarantine
{
    public string Move(string source, string quarantineRoot)
    {
        Directory.CreateDirectory(quarantineRoot);
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
        var destination = Path.Combine(quarantineRoot,
            $"{timestamp}-{Guid.NewGuid():N}-{Path.GetFileName(source)}");
        File.Move(source, destination, overwrite: false);
        return destination;
    }
}
