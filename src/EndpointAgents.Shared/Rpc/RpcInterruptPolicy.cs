using EndpointAgents.Shared.Support;

namespace EndpointAgents.Shared.Rpc;

/// <summary>
/// Which endpoint commands the Stop button may interrupt while they run. Read-only checks can be
/// abandoned at any point; anything that repairs or installs is left to finish, because killing a
/// repair halfway can leave the PC worse than either end state.
/// </summary>
public static class RpcInterruptPolicy
{
    private static readonly HashSet<string> ReadOnlyMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "endpoint.get_info",
        SystemInfoContracts.Method,
        "system.windows_activation_status",
        "software.office_inventory",
        "software.office_activation_status",
        "software.office_runtime_status",
        "network.ibsng_diagnose"
    };

    /// <summary>The hub client method that asks the endpoint to stop one running command by request id.</summary>
    public const string CancelMethod = "CancelRpc";

    public static bool CanInterrupt(string method) => ReadOnlyMethods.Contains(method);
}
