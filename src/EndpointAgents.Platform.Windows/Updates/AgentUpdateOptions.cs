namespace EndpointAgents.Platform.Windows.Updates;

/// <summary>Self-update settings (configuration section <c>AgentUpdate</c>).</summary>
public sealed class AgentUpdateOptions
{
    public const string SectionName = "AgentUpdate";

    /// <summary>Ask the control plane for a newer version. Off only for development runs.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often to ask (the control plane can also say "check now").</summary>
    public int CheckIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// The installed product folder (below Program Files) that holds <c>Service</c>, <c>Tray</c>
    /// and <c>versions\&lt;version&gt;</c>. Empty: found from the running service's path.
    /// </summary>
    public string InstallRoot { get; set; } = "";

    /// <summary>Where downloads and the state file live; only SYSTEM and Administrators can write it.</summary>
    public string StagingRoot { get; set; } = @"C:\ProgramData\EndpointAgents\Updates";

    /// <summary>How long the new version has to connect before the updater rolls back.</summary>
    public int HealthTimeoutSeconds { get; set; } = 180;

    /// <summary>Windows service name the updater stops, switches and starts.</summary>
    public string ServiceName { get; set; } = "EndpointAgents.Service";

    public string StatePath => Path.Combine(Path.GetFullPath(StagingRoot), "state.json");
}

/// <summary>Fixed locations shared by the service, the installer script and the updater.</summary>
public static class AgentUpdatePaths
{
    public const string VersionsDirectoryName = "versions";
    public const string TrayRunValueName = "EndpointAgents.Tray";
    public const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// The PC's own settings (control-plane address, signing keys, agent id). They live outside
    /// the program folder so a new version's appsettings.json never replaces them.
    /// </summary>
    public const string SiteSettingsPath = @"C:\ProgramData\EndpointAgents\Config\appsettings.site.json";
}
