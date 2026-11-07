using ControlPlane.Api.Agents;
using ControlPlane.Api.Hubs;
using ControlPlane.Api.Security;
using EndpointAgents.Shared.Updates;
using Microsoft.AspNetCore.SignalR;

namespace ControlPlane.Api.Updates;

public sealed record AgentUpdateSettingsRequest(
    string? TargetVersion,
    string? PilotVersion,
    IReadOnlyList<string>? PilotAgentIds,
    bool Paused,
    int? WindowStartHour,
    int? WindowEndHour);

/// <summary>
/// The Agent updates page (admins) and the download route endpoints use. Endpoints never
/// choose a version themselves: they ask in the hub, and the control plane offers one.
/// </summary>
public static class AgentUpdateEndpoints
{
    public static void MapAgentUpdateEndpoints(this WebApplication app)
    {
        // Device-token download of one signed zip (the endpoint checks hash and signature again).
        app.MapGet(AgentUpdateRoutes.Root + "/{version}/package", async (string version,
            HttpContext context, AgentUpdateRepository repository, CancellationToken cancellationToken) =>
        {
            var package = await repository.GetAsync(version, cancellationToken);
            if (package is not { IsReady: true, ZipPath: not null })
                return Results.NotFound(new { message = "That agent version is not available." });
            context.Response.Headers["X-Artifact-Sha256"] = package.Manifest!.Sha256;
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(package.ZipPath, "application/zip", package.Manifest.FileName,
                enableRangeProcessing: true);
        }).RequireAuthorization();

        app.MapGet("/api/agent-updates", async (AgentUpdateRepository repository,
            AgentUpdateStore store, AgentRegistry registry, CancellationToken cancellationToken) =>
        {
            var packages = await repository.GetAllAsync(cancellationToken);
            var online = registry.GetOnlineAgents().ToDictionary(agent => agent.AgentId, StringComparer.OrdinalIgnoreCase);
            var devices = (await store.GetDevicesAsync(cancellationToken)).Select(device => new
            {
                device.AgentId,
                MachineName = device.MachineName.Length > 0 ? device.MachineName
                    : online.TryGetValue(device.AgentId, out var agent) ? agent.MachineName : "",
                CurrentVersion = online.TryGetValue(device.AgentId, out var live) ? live.Version : device.CurrentVersion,
                Online = online.ContainsKey(device.AgentId),
                device.LastCheckUtc, device.LastStatus, device.LastFromVersion, device.LastToVersion,
                device.LastMessage, device.LastReportUtc
            });
            return Results.Ok(new
            {
                settings = await store.GetSettingsAsync(cancellationToken),
                packages = packages.Select(item => new
                {
                    item.Version, item.Status, item.Problem, item.AddedAtUtc,
                    releaseNotes = item.Manifest?.ReleaseNotes,
                    minWindowsBuild = item.Manifest?.MinWindowsBuild,
                    sizeBytes = item.Manifest?.SizeBytes,
                    signedBy = item.Manifest?.Signature?.KeyId
                }),
                devices
            });
        }).RequireAuthorization(OperatorPolicies.Admin);

        app.MapPut("/api/agent-updates/settings", async (AgentUpdateSettingsRequest request,
            HttpContext context, AgentUpdateStore store, AgentUpdateRepository repository,
            IHubContext<AgentHub> hub, CancellationToken cancellationToken) =>
        {
            var error = await ValidateAsync(request, repository, cancellationToken);
            if (error is not null) return Results.BadRequest(new { message = error });
            var settings = new AgentUpdateSettings(Empty(request.TargetVersion), Empty(request.PilotVersion),
                (request.PilotAgentIds ?? []).Select(id => id.Trim()).Where(id => id.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                request.Paused, request.WindowStartHour, request.WindowEndHour,
                Operator(context), DateTimeOffset.UtcNow);
            await store.SaveSettingsAsync(settings, cancellationToken);
            // Ask every online PC to check now instead of at its next hourly check.
            if (!settings.Paused)
                await hub.Clients.All.SendAsync(AgentUpdateHubMethods.Available, cancellationToken);
            return Results.Ok(settings);
        }).RequireAuthorization(OperatorPolicies.Admin);
    }

    internal static async Task<string?> ValidateAsync(AgentUpdateSettingsRequest request,
        AgentUpdateRepository repository, CancellationToken cancellationToken)
    {
        foreach (var version in new[] { request.TargetVersion, request.PilotVersion })
        {
            if (string.IsNullOrWhiteSpace(version)) continue;
            var package = await repository.GetAsync(version.Trim(), cancellationToken);
            if (package is null) return $"Version {version} is not in the file repository.";
            if (!package.IsReady) return $"Version {version} is not ready: {package.Problem ?? package.Status}.";
        }
        if (!string.IsNullOrWhiteSpace(request.PilotVersion) && (request.PilotAgentIds?.Count ?? 0) == 0)
            return "Choose at least one pilot PC for the pilot version.";
        if ((request.PilotAgentIds?.Count ?? 0) > 500) return "At most 500 pilot PCs.";
        if (request.PilotAgentIds?.Any(id => id.Length > 100) == true) return "A PC id is too long.";
        if (request.WindowStartHour is < 0 or > 23 || request.WindowEndHour is < 0 or > 23)
            return "Window hours must be between 0 and 23.";
        if (request.WindowStartHour.HasValue != request.WindowEndHour.HasValue)
            return "Set both window hours, or neither.";
        return null;
    }

    private static string Operator(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true && context.User.Identity.Name is { Length: > 0 } name
            ? name : "operator";

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
