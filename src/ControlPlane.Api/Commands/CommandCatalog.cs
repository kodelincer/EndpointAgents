namespace ControlPlane.Api.Commands;

public sealed record CommandDefinition(string Method, string Name, string Description, object? DefaultParams);

public sealed class CommandCatalog
{
    private static readonly CommandDefinition[] Commands =
    [
        new("endpoint.get_info", "Get endpoint information",
            "Collect user, machine, network adapter, proxy, and time diagnostics.", null)
    ];

    public IReadOnlyList<CommandDefinition> GetAll() => Commands;
    public bool Contains(string method) => Commands.Any(command => command.Method == method);
}
