using EndpointAgents.Shared.Actions;

namespace EndpointAgents.Platform.Windows.Actions;

/// <summary>
/// One catalogued action from <see cref="EndpointActionCatalog"/>. Implementations are fixed
/// code: they never run a shell, never take a command, path or URL from the caller, and only
/// use the validated arguments and control-plane-resolved targets in the invocation.
/// </summary>
public interface IEndpointAction
{
    /// <summary>The catalogue id this class implements.</summary>
    string Id { get; }

    Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken);
}

/// <summary>Validated input for one action run.</summary>
public sealed class ActionInvocation(EndpointActionDefinition definition,
    IReadOnlyDictionary<string, string> arguments,
    IReadOnlyDictionary<string, ResolvedTarget> targets,
    IReadOnlyDictionary<string, string> settings,
    string? userSid)
{
    public EndpointActionDefinition Definition { get; } = definition;
    public IReadOnlyDictionary<string, string> Arguments { get; } = arguments;
    public IReadOnlyDictionary<string, ResolvedTarget> Targets { get; } = targets;
    public IReadOnlyDictionary<string, string> Settings { get; } = settings;
    public string? UserSid { get; } = userSid;

    public string? Argument(string name) =>
        Arguments.TryGetValue(name, out var value) ? value : null;

    public int IntArgument(string name, int fallback) =>
        int.TryParse(Argument(name), out var value) ? value : fallback;

    public ResolvedTarget Target(string name = "target") =>
        Targets.TryGetValue(name, out var target) ? target
            : throw new InvalidOperationException($"Target '{name}' was not resolved.");

    public string? Setting(string key) =>
        Settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim() : null;
}
