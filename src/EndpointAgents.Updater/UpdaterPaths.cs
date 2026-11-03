using EndpointAgents.Shared.Updates;

namespace EndpointAgents.Updater;

internal static class UpdaterPaths
{
    public const string DefaultStatePath = @"C:\ProgramData\EndpointAgents\Updates\state.json";

    /// <summary>
    /// Every path the updater writes into the service configuration must be our own executable
    /// inside the install folder below Program Files. Null when the state is safe to act on.
    /// </summary>
    public static string? Validate(AgentUpdateState state, string? programFiles = null)
    {
        programFiles = Path.GetFullPath(programFiles
            ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)).TrimEnd('\\', '/');
        if (!AgentUpdatePolicy.IsValidVersion(state.Version)) return "The version is invalid.";
        if (string.IsNullOrWhiteSpace(state.InstallRoot)) return "The install folder is missing.";
        var root = Path.GetFullPath(state.InstallRoot).TrimEnd('\\', '/');
        if (!root.StartsWith(programFiles + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return "The install folder is not below Program Files.";
        var inside = root + Path.DirectorySeparatorChar;
        bool Ours(string? path, string fileName) => !string.IsNullOrWhiteSpace(path)
            && Path.GetFullPath(path).StartsWith(inside, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase);
        if (!Ours(state.NewServiceExe, "EndpointAgents.Service.exe")) return "The new service path is unsafe.";
        if (!Ours(state.PreviousServiceExe, "EndpointAgents.Service.exe")) return "The previous service path is unsafe.";
        if (!Ours(state.NewTrayExe, "EndpointAgents.TrayWeb.exe")) return "The new Tray path is unsafe.";
        var expectedNew = Path.Combine(root, "versions", state.Version, "Service", "EndpointAgents.Service.exe");
        if (!Path.GetFullPath(state.NewServiceExe).Equals(expectedNew, StringComparison.OrdinalIgnoreCase))
            return "The new service is not in versions\\<version>.";
        if (state.ServiceName != "EndpointAgents.Service") return "Unexpected service name.";
        return null;
    }

    /// <summary>The executable path inside a Run-key command such as "\"C:\...\Tray.exe\"".</summary>
    public static string? ExeFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var text = command.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }
        var space = text.IndexOf(' ');
        return space > 0 ? text[..space] : text;
    }

    public static string Quote(string path) => $"\"{path}\"";
}
