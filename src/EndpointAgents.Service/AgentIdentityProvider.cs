namespace EndpointAgents.Service;

/// <summary>Resolves the stable identity used when an endpoint agent registers.</summary>
/// <remarks>
/// An explicitly configured identity takes precedence. Otherwise, a GUID is
/// created once and persisted under common application data so service restarts
/// do not create duplicate logical agents.
/// </remarks>
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

    /// <summary>Returns the configured identity or loads/creates the persisted identity.</summary>
    /// <param name="configuredAgentId">Optional identity supplied by configuration.</param>
    public string GetOrCreate(string? configuredAgentId)
    {
        if (!string.IsNullOrWhiteSpace(configuredAgentId))
        {
            return configuredAgentId.Trim();
        }

        // The provider is a singleton and may be accessed concurrently during
        // startup; serialize the read/create operation.
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

        // CommonApplicationData is shared by service accounts, unlike a user
        // profile, so the identity remains available across service launches.
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
