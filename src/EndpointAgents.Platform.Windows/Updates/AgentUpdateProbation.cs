using EndpointAgents.Shared.Updates;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Updates;

/// <summary>
/// The 24-hour watch after an update. Each service start that follows an unexpected stop
/// (a crash, not a reboot or a normal stop) counts. At the third one the service hands over to
/// the updater, which puts the previous version back, and that version is never tried again here.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AgentUpdateProbation
{
    /// <summary>Called first thing when the service starts. True when it must exit for a rollback.</summary>
    public static bool OnServiceStart(string statePath, string runningVersion, DateTimeOffset nowUtc)
    {
        var state = AgentUpdateState.Read(statePath);
        if (state is null) return false;
        var watching = state.Phase == AgentUpdatePhases.Succeeded
            && string.Equals(state.Version, runningVersion, StringComparison.OrdinalIgnoreCase)
            && state.ProbationUntilUtc is { } until && nowUtc < until;
        var crashes = state.CrashesDuringProbation + (watching && !state.CleanStop ? 1 : 0);
        if (watching && crashes >= AgentUpdateState.MaxCrashesDuringProbation
            && File.Exists(UpdaterBeside(state.NewServiceExe)))
        {
            var rollingBack = state with
            {
                Phase = AgentUpdatePhases.RollingBack,
                CrashesDuringProbation = crashes,
                Message = $"Version {state.Version} stopped unexpectedly {crashes} times after the update.",
                Reported = false
            };
            rollingBack.Write(statePath);
            var start = new ProcessStartInfo(UpdaterBeside(state.NewServiceExe))
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
        (state with { CrashesDuringProbation = crashes, CleanStop = false }).Write(statePath);
        return false;
    }

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
}
