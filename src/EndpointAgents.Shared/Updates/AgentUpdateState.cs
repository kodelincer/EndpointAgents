using System.Text.Json;

namespace EndpointAgents.Shared.Updates;

/// <summary>Where an agent update is, written by the service and the updater in one protected file.</summary>
public static class AgentUpdatePhases
{
    /// <summary>The service staged the new version and started the updater.</summary>
    public const string Installing = "Installing";
    /// <summary>The updater switched to the new version and waits for it to connect.</summary>
    public const string AwaitingHealth = "AwaitingHealth";
    /// <summary>The new service connected to the control plane.</summary>
    public const string Healthy = "Healthy";
    public const string Succeeded = "Succeeded";
    public const string RolledBack = "RolledBack";
    public const string Failed = "Failed";
    /// <summary>The new version crashed again and again after the update; the updater goes back.</summary>
    public const string RollingBack = "RollingBack";

    public static bool IsInProgress(string? phase) => phase is Installing or AwaitingHealth or Healthy or RollingBack;

    public static bool IsFinished(string? phase) => phase is Succeeded or RolledBack or Failed;
}

/// <summary>
/// The hand-over between the running service, the updater and the new service. It lives in
/// <c>C:\ProgramData\EndpointAgents\Updates\state.json</c>, which only SYSTEM and
/// Administrators can write.
/// </summary>
public sealed record AgentUpdateState
{
    public string Version { get; init; } = "";
    public string FromVersion { get; init; } = "";
    public string Phase { get; init; } = "";
    public string InstallRoot { get; init; } = "";
    public string PreviousServiceExe { get; init; } = "";
    public string? PreviousTrayCommand { get; init; }
    public string NewServiceExe { get; init; } = "";
    public string NewTrayExe { get; init; } = "";
    public string ServiceName { get; init; } = "EndpointAgents.Service";
    public int HealthTimeoutSeconds { get; init; } = 180;
    public string Message { get; init; } = "";
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    /// <summary>Versions that failed here and were rolled back; the PC never tries them again.</summary>
    public List<string> FailedVersions { get; init; } = [];
    /// <summary>
    /// After a successful update the new version is watched until this time: if it stops
    /// unexpectedly <see cref="MaxCrashesDuringProbation"/> times, it rolls back by itself.
    /// </summary>
    public DateTimeOffset? ProbationUntilUtc { get; init; }
    public int CrashesDuringProbation { get; init; }
    public const int MaxCrashesDuringProbation = 3;
    /// <summary>False from service start until a normal stop, so a crash can be told from a reboot.</summary>
    public bool CleanStop { get; init; } = true;
    /// <summary>True once the final result reached the control plane.</summary>
    public bool Reported { get; init; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static AgentUpdateState? Read(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<AgentUpdateState>(File.ReadAllText(path), Json)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes through a temporary file so a crash never leaves half a state file.</summary>
    public void Write(string path)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this with { UpdatedAtUtc = DateTimeOffset.UtcNow }, Json));
        File.Move(temp, path, overwrite: true);
    }
}
