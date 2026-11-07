using ControlPlane.Api.Actions;
using ControlPlane.Api.Agents;
using ControlPlane.Api.Security;
using EndpointAgents.Shared.Rpc;
using EndpointAgents.Shared.Support;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ControlPlane.Api.Inventory;

/// <summary>The Devices page: what is known about each PC, how old it is, and Refresh for one PC.</summary>
public static class InventoryEndpoints
{
    private static readonly string[] KeptSections =
        [SystemInfoContracts.Windows, SystemInfoContracts.Domain, SystemInfoContracts.Hardware, SystemInfoContracts.Storage];

    public static void MapInventoryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/inventory", async (InventoryStore inventory, AgentRegistry registry,
            CancellationToken cancellationToken) =>
        {
            var online = registry.GetOnlineAgents().ToDictionary(agent => agent.AgentId);
            var stored = await inventory.ListDevicesAsync(cancellationToken);
            var devices = stored.Select(device => new
            {
                device.AgentId,
                MachineName = online.TryGetValue(device.AgentId, out var agent) ? agent.MachineName : device.MachineName,
                Online = online.ContainsKey(device.AgentId),
                Version = online.TryGetValue(device.AgentId, out var live) ? live.Version : null,
                device.LastReadUtc, device.Facts
            }).ToList();
            devices.AddRange(online.Values.Where(agent => stored.All(device => device.AgentId != agent.AgentId))
                .Select(agent => new
                {
                    agent.AgentId, agent.MachineName, Online = true, Version = (string?)agent.Version,
                    LastReadUtc = (DateTimeOffset)DateTimeOffset.MinValue, Facts = 0
                }));
            return Results.Ok(devices.OrderBy(device => device.MachineName, StringComparer.OrdinalIgnoreCase));
        }).RequireAuthorization(OperatorPolicies.Operator);

        app.MapGet("/api/inventory/{agentId}", async (string agentId, InventoryStore inventory, TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            var now = clock.GetUtcNow();
            return Results.Ok((await inventory.GetDeviceAsync(agentId, cancellationToken)).Select(fact => new
            {
                fact.Key, fact.ReadAtUtc, Fresh = fact.IsFresh(now),
                KeptForHours = InventoryKeys.MaxAge[fact.Key].TotalHours, fact.Value
            }));
        }).RequireAuthorization(OperatorPolicies.Operator);

        // Read-only: asks the PC again for every kept fact and stores the answers.
        app.MapPost("/api/inventory/{agentId}/refresh", async (string agentId, InventoryStore inventory,
            AgentRegistry registry, ActionRunner actions, HttpContext context, CancellationToken cancellationToken) =>
        {
            var agent = registry.GetOnlineAgents().FirstOrDefault(item => item.AgentId == agentId);
            if (agent is null) return Results.BadRequest(new { message = "The PC is offline; its stored facts stay as they are." });
            var problems = new List<string>();
            var response = await registry.SendCommandAsync(agentId, RpcRequest.Create(Guid.NewGuid().ToString("N"),
                SystemInfoContracts.Method, JsonSerializer.SerializeToElement(new { sections = KeptSections })), cancellationToken);
            if (response.Error is not null) problems.Add(response.Error.Message);
            else if (JsonSerializer.SerializeToNode(response.Result) is JsonObject root
                     && root.FirstOrDefault(pair => string.Equals(pair.Key, "result", StringComparison.OrdinalIgnoreCase)).Value is JsonObject sections)
                foreach (var section in KeptSections)
                    if (sections.TryGetPropertyValue(section, out var value) && value is not null)
                        await inventory.SaveAsync(agentId, agent.MachineName, InventoryKeys.System(section), value.DeepClone(), cancellationToken);
            var by = context.User.Identity?.Name is { Length: > 0 } name ? name : "operator";
            foreach (var actionId in InventoryKeys.MaxAge.Keys.Where(key => key.StartsWith("action:", StringComparison.Ordinal))
                         .Select(key => key["action:".Length..]))
            {
                var outcome = await actions.RunAsync(new ActionRunContext(agentId, null, null, ActionRunSources.Operator, by),
                    actionId, null, false, cancellationToken);
                if (!outcome.Accepted || outcome.Status != EndpointAgents.Shared.Actions.ActionStatus.Ok)
                    problems.Add($"{actionId}: {outcome.Error ?? outcome.Summary}");
            }
            return Results.Ok(new { refreshed = true, problems });
        }).RequireAuthorization(OperatorPolicies.Operator);
    }
}
