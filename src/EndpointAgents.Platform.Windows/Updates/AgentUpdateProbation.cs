using EndpointAgents.Shared.Updates;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Updates;

/// <summary>
/// The 24-hour watch after an update. Each service start that follows an unexpected stop
/// (a crash, not a reboot, a power cut or a normal stop) counts. At the third one the service
/// hands over to the updater, which puts the previous version back, and that version is never
/// tried again here. It also finishes an update whose updater died half-way (the PC lost power
/// during the switch), so no PC stays on a new version that nobody is watching.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AgentUpdateProbation
{
    public static readonly TimeSpan Probation = TimeSpan.FromHours(24);

    /// <summary>How long after its own deadline the updater is taken for dead.</summary>
    public static readonly TimeSpan OrphanGrace = TimeSpan.FromMinutes(10);

    public enum OrphanOutcome { None, Adopted, RollingBack }

    /// <summary>Called first thing when the service starts. True when it must exit for a rollback.</summary>
    /// <param name="bootedAtUtc">When Windows started. A Windows start after the last service
    /// start means the whole PC went down (power cut, hard reset), which is not this version's fault.</param>
    public static bool OnServiceStart(string statePath, string runningVersion, DateTimeOffset nowUtc,
        DateTimeOffset? bootedAtUtc = null)
    {
        var state = AgentUpdateState.Read(statePath);
        if (state is null) return false;
        var watching = (state.Phase == AgentUpdatePhases.Succeeded
                && string.Equals(state.Version, runningVersion, StringComparison.OrdinalIgnoreCase)
                && state.ProbationUntilUtc is { } until && nowUtc < until)
            || IsOrphaned(state, runningVersion, nowUtc);
        var machineWentDown = bootedAtUtc is { } boot && state.LastStartUtc is { } lastStart && boot > lastStart;
        var crashes = state.CrashesDuringProbation + (watching && !state.CleanStop && !machineWentDown ? 1 : 0);
        if (watching && crashes >= AgentUpdateState.MaxCrashesDuringProbation
            && StartRollback(state with { CrashesDuringProbation = crashes }, statePath,
                $"Version {state.Version} stopped unexpectedly {crashes} times after the update."))
            return true;
        (state with { CrashesDuringProbation = crashes, CleanStop = false, LastStartUtc = nowUtc }).Write(statePath);
        return false;
    }

    /// <summary>
    /// Called every minute by the running service. When the updater never finished its part
    /// (it should have confirmed or rolled back long ago), the service finishes it: a version
    /// that reached the control plane is kept and watched for 24 hours like any update; one
    /// that never connected is rolled back.
    /// </summary>
    public static OrphanOutcome RecoverOrphaned(string statePath, string runningVersion, DateTimeOffset nowUtc,
        bool connected)
    {
        var state = AgentUpdateState.Read(statePath);
        if (state is null || !IsOrphaned(state, runningVersion, nowUtc)) return OrphanOutcome.None;
        if (connected || state.Phase == AgentUpdatePhases.Healthy)
        {
            (state with
            {
                Phase = AgentUpdatePhases.Succeeded,
                Message = $"Updated from {state.FromVersion} to {state.Version} (the updater was interrupted; the service finished the update).",
                ProbationUntilUtc = nowUtc + Probation,
                Reported = false
            }).Write(statePath);
            return OrphanOutcome.Adopted;
        }
        return StartRollback(state, statePath,
            $"Version {state.Version} never connected after an interrupted update.")
            ? OrphanOutcome.RollingBack : OrphanOutcome.None;
    }

    /// <summary>The switch was made, this version runs, and the updater is long past its deadline.</summary>
    public static bool IsOrphaned(AgentUpdateState state, string runningVersion, DateTimeOffset nowUtc) =>
        state.Phase is AgentUpdatePhases.AwaitingHealth or AgentUpdatePhases.Healthy
        && string.Equals(state.Version, runningVersion, StringComparison.OrdinalIgnoreCase)
        && nowUtc - state.StartedAtUtc > TimeSpan.FromSeconds(Math.Clamp(state.HealthTimeoutSeconds, 30, 900)) + OrphanGrace;

    /// <summary>Called when the service stops normally.</summary>
    public static void OnServiceStop(string statePath)
    {
        var state = AgentUpdateState.Read(statePath);
        if (state is null || state.CleanStop) return;
        try { (state with { CleanStop = true }).Write(statePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The updater that came with a version: ...\versions\x\Service\..exe -> ...\versions\x\Updater\..exe.</summary>
    public static string UpdaterBeside(string serviceExe) =>
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(serviceExe)!)!, "Updater", "EndpointAgents.Updater.exe");

    /// <summary>Hands over to the updater's rollback. False when this version has no updater to run.</summary>
    private static bool StartRollback(AgentUpdateState state, string statePath, string message)
    {
        if (string.IsNullOrWhiteSpace(state.NewServiceExe)) return false;
        var updater = UpdaterBeside(state.NewServiceExe);
        if (!File.Exists(updater)) return false;
        (state with { Phase = AgentUpdatePhases.RollingBack, Message = message, Reported = false }).Write(statePath);
        var start = new ProcessStartInfo(updater)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--state");
        start.ArgumentList.Add(statePath);
        start.ArgumentList.Add("--rollback");
        Process.Start(start)?.Dispose();
        return true;
    }
}
