using EndpointAgents.Shared.Updates;
using EndpointAgents.Updater;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]

// EndpointAgents.Updater --state <state.json> [--rollback]
//
// Started by the endpoint service from the NEW version's folder, as SYSTEM. It switches the
// service and the Tray to the new version and waits for the new service to connect. If it does
// not, it puts the previous version back. With --rollback (a version that kept crashing during
// its first 24 hours) it only puts the previous version back.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    if (e.ExceptionObject is not Exception exception) return;
    // The service picks this file up the next time it runs.
    new EndpointAgents.Shared.Problems.ProblemOutbox(EndpointAgents.Shared.Problems.ProblemPaths.UpdaterInbox).Add(
        EndpointAgents.Shared.Problems.ProblemText.FromException(EndpointAgents.Shared.Problems.ProblemKinds.Crash,
            "Updater", exception, typeof(AgentSwitcher).Assembly.GetName().Version?.ToString(3), DateTimeOffset.UtcNow),
        saveNow: true);
};
string? statePath = null;
var rollback = false;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--state" && i + 1 < args.Length) statePath = args[++i];
    else if (args[i] == "--rollback") rollback = true;
}

var expected = Path.GetFullPath(UpdaterPaths.DefaultStatePath);
if (statePath is null || !Path.GetFullPath(statePath).Equals(expected, StringComparison.OrdinalIgnoreCase))
    return 2; // only the fixed, protected state file is accepted

using var log = new UpdaterLog(Path.Combine(Path.GetDirectoryName(expected)!, "updater.log"));
var state = AgentUpdateState.Read(expected);
if (state is null)
{
    log.Write("No update state; nothing to do.");
    return 3;
}

var problem = UpdaterPaths.Validate(state);
if (problem is not null)
{
    log.Write($"Refusing the update state: {problem}");
    new AgentUpdateState
    {
        Version = state.Version, FromVersion = state.FromVersion, Phase = AgentUpdatePhases.Failed,
        Message = problem, FailedVersions = state.FailedVersions, InstallRoot = state.InstallRoot
    }.Write(expected);
    return 4;
}

var updater = new AgentSwitcher(state, expected, log, new WindowsMachine(log));
return rollback ? updater.RollBackAfterCrashes() : updater.Run();
