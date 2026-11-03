using EndpointAgents.Shared.Updates;

namespace EndpointAgents.Updater;

/// <summary>
/// The switch from one agent version to another, and the way back. Both versions stay on disk
/// side by side (<c>versions\&lt;version&gt;</c>), so switching only changes which executable the
/// Windows service and the Tray's Run entry point at. Nothing is overwritten in place.
/// </summary>
internal sealed class AgentSwitcher(AgentUpdateState state, string statePath, UpdaterLog log, IMachine machine)
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Probation = TimeSpan.FromHours(24);

    private AgentUpdateState _state = state;

    public int Run()
    {
        log.Write($"Updating {_state.FromVersion} -> {_state.Version}.");
        var previousImage = machine.GetServiceImagePath(_state.ServiceName)
            ?? UpdaterPaths.Quote(_state.PreviousServiceExe);
        var previousTray = machine.GetTrayRunCommand() ?? _state.PreviousTrayCommand;

        if (!machine.StopService(_state.ServiceName, StopTimeout))
        {
            log.Write("The service did not stop; the update is cancelled and nothing changed.");
            machine.StartService(_state.ServiceName);
            return Finish(AgentUpdatePhases.Failed, "The old version did not stop in time; nothing was changed.", failed: false);
        }
        var sessions = machine.StopTrays(_state.InstallRoot);

        machine.SetServiceImagePath(_state.ServiceName, UpdaterPaths.Quote(_state.NewServiceExe));
        machine.SetTrayRunCommand(UpdaterPaths.Quote(_state.NewTrayExe));
        Save(_state with { Phase = AgentUpdatePhases.AwaitingHealth, Message = "Waiting for the new version to connect." });

        if (machine.StartService(_state.ServiceName) && WaitForHealth())
        {
            machine.StartTray(_state.NewTrayExe, sessions);
            CleanUpOldVersions();
            log.Write($"Version {_state.Version} is running.");
            Save(_state with
            {
                Phase = AgentUpdatePhases.Succeeded,
                Message = $"Updated from {_state.FromVersion} to {_state.Version}.",
                ProbationUntilUtc = machine.UtcNow + Probation,
                CrashesDuringProbation = 0,
                CleanStop = true,
                Reported = false
            });
            return 0;
        }

        log.Write("The new version did not connect in time; going back to the previous version.");
        machine.SetServiceImagePath(_state.ServiceName, previousImage);
        machine.SetTrayRunCommand(previousTray);
        machine.StopService(_state.ServiceName, StopTimeout);
        machine.StopTrays(_state.InstallRoot);
        machine.StartService(_state.ServiceName);
        var previousTrayExe = UpdaterPaths.ExeFromCommand(previousTray);
        if (previousTrayExe is not null) machine.StartTray(previousTrayExe, sessions);
        TryDelete(VersionDirectory(_state.Version));
        return Finish(AgentUpdatePhases.RolledBack,
            $"Version {_state.Version} did not connect within {_state.HealthTimeoutSeconds} seconds, so {_state.FromVersion} was put back.",
            failed: true);
    }

    /// <summary>The new version kept crashing during its first day: put the previous one back.</summary>
    public int RollBackAfterCrashes()
    {
        log.Write($"Version {_state.Version} kept crashing; going back to {_state.FromVersion}.");
        var previousTray = _state.PreviousTrayCommand;
        // Point the service back first, so Windows' own restart-after-crash starts the old version.
        machine.SetServiceImagePath(_state.ServiceName, UpdaterPaths.Quote(_state.PreviousServiceExe));
        machine.SetTrayRunCommand(previousTray);
        machine.StopService(_state.ServiceName, StopTimeout);
        var sessions = machine.StopTrays(_state.InstallRoot);
        machine.StartService(_state.ServiceName);
        var previousTrayExe = UpdaterPaths.ExeFromCommand(previousTray);
        if (previousTrayExe is not null) machine.StartTray(previousTrayExe, sessions);
        return Finish(AgentUpdatePhases.RolledBack,
            string.IsNullOrWhiteSpace(_state.Message) ? $"Version {_state.Version} kept crashing, so {_state.FromVersion} was put back."
                : _state.Message + $" {_state.FromVersion} was put back.",
            failed: true);
    }

    private bool WaitForHealth()
    {
        var deadline = machine.UtcNow + TimeSpan.FromSeconds(_state.HealthTimeoutSeconds);
        while (machine.UtcNow < deadline)
        {
            var current = AgentUpdateState.Read(statePath);
            if (current?.Phase == AgentUpdatePhases.Healthy
                && string.Equals(current.Version, _state.Version, StringComparison.OrdinalIgnoreCase))
            {
                _state = current;
                return true;
            }
            machine.Sleep(Poll);
        }
        return false;
    }

    /// <summary>Keeps the new version and the one before it (for the 24-hour rollback); removes older ones.</summary>
    private void CleanUpOldVersions()
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(VersionDirectory(_state.Version)),
            Path.GetFullPath(Path.GetDirectoryName(Path.GetDirectoryName(_state.PreviousServiceExe)!)!)
        };
        foreach (var directory in machine.ListDirectories(Path.Combine(_state.InstallRoot, "versions")))
            if (!keep.Contains(Path.GetFullPath(directory))) TryDelete(directory);
        // The first, script-installed copy (Service and Tray directly in the install folder) goes
        // once it is no longer the previous version.
        if (!keep.Contains(Path.GetFullPath(_state.InstallRoot)))
        {
            TryDelete(Path.Combine(_state.InstallRoot, "Service"));
            TryDelete(Path.Combine(_state.InstallRoot, "Tray"));
        }
    }

    private string VersionDirectory(string version) => Path.Combine(_state.InstallRoot, "versions", version);

    private void TryDelete(string path)
    {
        try { machine.DeleteDirectory(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write($"Could not remove {path}: {ex.Message}");
        }
    }

    private int Finish(string phase, string message, bool failed)
    {
        log.Write(message);
        var failedVersions = new List<string>(_state.FailedVersions);
        if (failed && !failedVersions.Contains(_state.Version, StringComparer.OrdinalIgnoreCase))
            failedVersions.Add(_state.Version);
        Save(_state with
        {
            Phase = phase, Message = message, FailedVersions = failedVersions,
            ProbationUntilUtc = null, Reported = false, CleanStop = true
        });
        return phase == AgentUpdatePhases.Succeeded ? 0 : 1;
    }

    private void Save(AgentUpdateState next)
    {
        _state = next;
        next.Write(statePath);
    }
}
