using EndpointAgents.Shared.Updates;

namespace ControlPlane.Api.Updates;

/// <summary>The answer to one endpoint's update check: an offer, or why there is none.</summary>
public sealed record AgentUpdateDecision(AgentUpdateOffer? Offer, string Reason);

/// <summary>
/// Decides whether one PC should update now. Pure, so every rule is covered by tests:
/// paused, no target, pilot first, only newer signed versions, never a version that already
/// failed on that PC, Windows build, the update window, and never during a conversation.
/// </summary>
public static class AgentUpdatePlanner
{
    public static AgentUpdateDecision Decide(string agentId, AgentUpdateCheck check,
        AgentUpdateSettings settings, IReadOnlyList<AgentUpdatePackage> packages,
        bool inConversation, DateTimeOffset nowLocal)
    {
        if (settings.Paused) return None("Updates are paused.");
        var isPilot = settings.PilotAgentIds.Contains(agentId, StringComparer.OrdinalIgnoreCase);
        var target = isPilot && !string.IsNullOrWhiteSpace(settings.PilotVersion)
            ? settings.PilotVersion : settings.TargetVersion;
        if (string.IsNullOrWhiteSpace(target)) return None("No target version is set.");
        if (!AgentUpdatePolicy.IsNewer(target, check.CurrentVersion))
            return None($"Already on {check.CurrentVersion} (target {target}).");
        if (check.FailedVersions?.Contains(target, StringComparer.OrdinalIgnoreCase) == true)
            return None($"{target} failed on this PC before and was rolled back.");
        var package = packages.FirstOrDefault(item => item.Version == target);
        if (package is null) return None($"Version {target} is not in the file repository.");
        if (!package.IsReady || package.Manifest is null)
            return None($"Version {target} is not ready: {package.Problem ?? package.Status}.");
        if (check.WindowsBuild < package.Manifest.MinWindowsBuild)
            return None($"This PC's Windows build {check.WindowsBuild} is older than {package.Manifest.MinWindowsBuild}.");
        if (!string.Equals(check.Architecture, package.Manifest.Architecture, StringComparison.OrdinalIgnoreCase))
            return None($"This PC is {check.Architecture}; the update is {package.Manifest.Architecture}.");
        if (!InWindow(settings, nowLocal))
            return None($"Outside the update window ({settings.WindowStartHour}:00 to {settings.WindowEndHour}:00).");
        if (check.Busy) return None("The PC is running a repair.");
        if (inConversation) return None("The user is in a conversation.");
        return new AgentUpdateDecision(
            new AgentUpdateOffer(package.Manifest, AgentUpdateRoutes.Download(package.Version)),
            $"Offer {package.Version}{(isPilot ? " (pilot)" : "")}.");
    }

    /// <summary>True when no window is set, or the hour is inside it (a window may cross midnight).</summary>
    public static bool InWindow(AgentUpdateSettings settings, DateTimeOffset nowLocal)
    {
        if (settings.WindowStartHour is not { } start || settings.WindowEndHour is not { } end
            || start == end)
            return true;
        var hour = nowLocal.Hour;
        return start < end ? hour >= start && hour < end : hour >= start || hour < end;
    }

    private static AgentUpdateDecision None(string reason) => new(null, reason);
}
