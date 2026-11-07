namespace EndpointAgents.Updater;

/// <summary>What the updater does to Windows, behind an interface so the switch logic is tested.</summary>
internal interface IMachine
{
    bool StopService(string name, TimeSpan timeout);
    bool StartService(string name);
    string? GetServiceImagePath(string name);
    void SetServiceImagePath(string name, string imagePath);
    string? GetTrayRunCommand();
    void SetTrayRunCommand(string? command);
    /// <summary>Closes every Tray started from <paramref name="installRoot"/>; returns the sessions they ran in.</summary>
    IReadOnlyCollection<int> StopTrays(string installRoot);
    void StartTray(string trayExe, IReadOnlyCollection<int> sessions);
    void DeleteDirectory(string path);
    IReadOnlyList<string> ListDirectories(string path);
    void Sleep(TimeSpan delay);
    DateTimeOffset UtcNow { get; }
}
