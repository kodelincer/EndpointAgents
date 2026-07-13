namespace ControlPlane.Api.Commands;

/// <summary>Operator-visible metadata for one approved endpoint RPC method.</summary>
public sealed record CommandDefinition(string Method, string Name, string Description, object? DefaultParams);

/// <summary>
/// Allow-list of RPC methods exposed by the dashboard. The API never accepts an
/// arbitrary method supplied only by a browser request.
/// </summary>
public sealed class CommandCatalog
{
    private static readonly CommandDefinition[] Commands =
    [
        new("endpoint.get_info", "Get endpoint information",
            "Collect user, machine, network adapter, proxy, and time diagnostics.", null)
    ];

    /// <summary>Returns all commands that may be selected by an operator.</summary>
    public IReadOnlyList<CommandDefinition> GetAll() => Commands;

    /// <summary>Returns whether a method is explicitly approved for dispatch.</summary>
    public bool Contains(string method) => Commands.Any(command => command.Method == method);
}
