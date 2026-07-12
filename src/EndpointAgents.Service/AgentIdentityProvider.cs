namespace EndpointAgents.Service;

public sealed class AgentIdentityProvider
{
    private const string IdentityFileName = "agent-id.txt";
    private readonly ILogger<AgentIdentityProvider> _logger;
    private readonly object _sync = new();
    private string? _agentId;

    public AgentIdentityProvider(ILogger<AgentIdentityProvider> logger)
    {
        _logger = logger;
    }

    public string GetOrCreate(string? configuredAgentId)
    {
        if (!string.IsNullOrWhiteSpace(configuredAgentId))
        {
            return configuredAgentId.Trim();
        }

        lock (_sync)
        {
            return _agentId ??= LoadOrCreate();
        }
    }

    private string LoadOrCreate()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "EndpointAgents");
        var path = Path.Combine(directory, IdentityFileName);

        Directory.CreateDirectory(directory);

        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (Guid.TryParse(existing, out _))
            {
                return existing;
            }

            throw new InvalidDataException($"The persisted agent identity at '{path}' is invalid.");
        }

        var created = Guid.NewGuid().ToString("D");
        File.WriteAllText(path, created);
        _logger.LogInformation("Created a durable endpoint agent identity at {IdentityPath}.", path);
        return created;
    }
}
