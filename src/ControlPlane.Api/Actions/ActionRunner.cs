using ControlPlane.Api.Agents;
using ControlPlane.Api.Tools;
using EndpointAgents.Shared.Actions;
using EndpointAgents.Shared.Rpc;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ControlPlane.Api.Actions;

/// <summary>Who asked for an action and for which PC and user.</summary>
public sealed record ActionRunContext(string AgentId, string? UserSid, string? ConversationId,
    string Source, string? RequestedBy = null);

public sealed record ActionRunOutcome(
    bool Accepted,
    string? Error,
    string? RunId,
    string ActionId,
    ActionStatus? Status,
    string? Summary,
    JsonNode? Data,
    JsonNode? Verification,
    bool UndoAvailable)
{
    public static ActionRunOutcome Rejected(string actionId, string error) =>
        new(false, error, null, actionId, null, null, null, null, false);
}

/// <summary>
/// The one place general actions are checked and sent: catalogue membership, the kill switch,
/// the hourly limit, the user's go-ahead for disruptive actions, argument and target
/// validation, the before/after verification, the run log and undo data. The agent's tools
/// and the dashboard both go through here.
/// </summary>
public sealed class ActionRunner(ActionStore store, AgentRegistry registry,
    ILogger<ActionRunner> logger, ControlPlane.Api.Inventory.InventoryStore inventory, TimeProvider clock)
{
    public static readonly TimeSpan LimitWindow = TimeSpan.FromHours(1);

    public async Task<ActionRunOutcome> RunAsync(ActionRunContext context, string actionId,
        IReadOnlyDictionary<string, string?>? arguments, bool userConfirmed,
        CancellationToken cancellationToken, string? reason = null)
    {
        var definition = EndpointActionCatalog.Find(actionId);
        if (definition is null)
            return ActionRunOutcome.Rejected(actionId, "Unknown action. Use list_endpoint_actions.");
        if (definition.Tier == ActionTier.OperatorOnly && context.Source != ActionRunSources.Operator
            && !(context.Source == ActionRunSources.HealthRule && actionId == "notice.show"))
            return ActionRunOutcome.Rejected(actionId, "This action is for the helpdesk only.");

        var settings = await store.GetActionSettingsAsync(cancellationToken);
        var setting = settings.GetValueOrDefault(actionId);
        if (setting is { Enabled: false })
            return ActionRunOutcome.Rejected(actionId, "This action is switched off by the helpdesk.");
        if (definition.Tier == ActionTier.Disruptive && !userConfirmed)
            return ActionRunOutcome.Rejected(actionId,
                "This action interrupts the user. Ask the user (now or later) and call again with userConfirmed=true only after they agree.");

        if (!EndpointActionValidator.TryValidate(definition, arguments, out var normalized,
                out var argumentError))
            return ActionRunOutcome.Rejected(actionId, argumentError);

        var targets = await store.GetTargetsAsync(cancellationToken);
        var resolved = new Dictionary<string, ResolvedTarget>(StringComparer.Ordinal);
        foreach (var parameter in definition.Parameters.Where(item => item.Kind == ActionParameterKind.Target))
        {
            if (!normalized.TryGetValue(parameter.Name, out var targetId)) continue;
            var target = targets.FirstOrDefault(item => item.Id == targetId);
            if (target is null || parameter.TargetKinds?.Contains(target.Kind) == false)
                return ActionRunOutcome.Rejected(actionId,
                    $"'{targetId}' is not a matching environment-map target. Use list_environment_targets.");
            resolved[parameter.Name] = target.ToResolved();
        }
        if (actionId == "net.test_port" && resolved.TryGetValue("target", out var portTarget)
            && !PortAllowed(portTarget, int.Parse(normalized["port"])))
            return ActionRunOutcome.Rejected(actionId, "That port is not listed for this target.");

        var limit = setting?.MaxPerHour ?? definition.EffectiveMaxPerHour;
        if (await store.CountRecentRunsAsync(context.AgentId, actionId, LimitWindow,
                cancellationToken) >= limit)
            return ActionRunOutcome.Rejected(actionId,
                $"This action already ran {limit} times on this PC in the last hour. Try another step or escalate.");

        if (NeedsApproval(definition, setting) && context.Source == ActionRunSources.Agent)
        {
            var approval = await store.CreateApprovalAsync(ApprovalKinds.Action, actionId,
                definition.Name + Describe(normalized, resolved), context.AgentId,
                context.UserSid, context.ConversationId, JsonSerializer.Serialize(normalized),
                reason ?? string.Empty, cancellationToken);
            await TraceAsync(context, actionId, ApprovalStatus.Pending,
                "Waiting for a helpdesk member's approval.", cancellationToken);
            return new ActionRunOutcome(true, null, null, actionId, ActionStatus.AwaitingApproval,
                AwaitingApprovalSummary, new JsonObject { ["approvalId"] = approval.ApprovalId },
                null, false);
        }

        var environment = await store.GetSettingsAsync(cancellationToken);
        var masker = new TargetMasker(targets, environment);

        // Keep, don't schedule: a fresh stored answer is given to the agent instead of asking the PC again.
        var inventoryKey = ControlPlane.Api.Inventory.InventoryKeys.Action(actionId);
        var keepable = definition.Tier == ActionTier.Look && normalized.Count == 0
            && ControlPlane.Api.Inventory.InventoryKeys.IsKept(inventoryKey);
        if (keepable && context.Source == ActionRunSources.Agent
            && await inventory.GetFreshAsync(context.AgentId, inventoryKey, cancellationToken) is { } stored)
        {
            var age = clock.GetUtcNow() - stored.ReadAtUtc;
            return new ActionRunOutcome(true, null, null, actionId, ActionStatus.Ok,
                $"Stored answer from {(age.TotalHours < 1 ? $"{Math.Max(1, (int)age.TotalMinutes)} minutes" : $"{(int)age.TotalHours} hours")} ago; the PC was not asked again.",
                masker.Mask(stored.Value), null, false);
        }
        var runId = Guid.NewGuid().ToString("N");
        await store.StartRunAsync(runId, actionId, context.AgentId, context.ConversationId,
            context.Source, JsonSerializer.Serialize(normalized), cancellationToken);

        JsonObject? verification = null;
        JsonNode? before = null;
        if (definition.Verify is { } verify && definition.Tier != ActionTier.Look)
            before = await RunVerificationAsync(context, verify, normalized, environment,
                targets, cancellationToken);

        var request = new EndpointActionRequest(actionId, normalized, resolved,
            (definition.UsesSettings ?? [])
                .Where(environment.ContainsKey)
                .ToDictionary(key => key, key => environment[key], StringComparer.Ordinal),
            context.UserSid);
        var (result, transportError) = await DispatchAsync(context.AgentId, request,
            cancellationToken);
        if (result is null)
        {
            await store.CompleteRunAsync(runId, "Failed", transportError ?? "No answer.", null,
                null, null, cancellationToken);
            await TraceAsync(context, actionId, "Failed", transportError, cancellationToken);
            return new ActionRunOutcome(true, transportError, runId, actionId,
                ActionStatus.Failed, transportError, null, null, false);
        }

        if (definition.Verify is { } check && definition.Tier != ActionTier.Look
            && result.Status == ActionStatus.Done)
        {
            var after = await RunVerificationAsync(context, check, normalized, environment,
                targets, cancellationToken);
            verification = new JsonObject
            {
                ["check"] = check.ActionId,
                ["before"] = before,
                ["after"] = after
            };
        }

        var data = Annotate(actionId, ToNode(result.Data), targets);
        if (keepable && result.Status == ActionStatus.Ok)
            await inventory.SaveAsync(context.AgentId, MachineName(context.AgentId), inventoryKey,
                data?.DeepClone(), cancellationToken);
        var undoJson = result.Undo is { Count: > 0 } && definition.UndoActionId is not null
            ? JsonSerializer.Serialize(result.Undo) : null;
        await store.CompleteRunAsync(runId, result.Status.ToString(), result.Summary,
            data?.ToJsonString(), verification?.ToJsonString(), undoJson, cancellationToken);
        await TraceAsync(context, actionId, result.Status.ToString(), result.Summary,
            cancellationToken);
        logger.LogInformation("Endpoint action {ActionId} on {AgentId}: {Status}", actionId,
            context.AgentId, result.Status);

        return new ActionRunOutcome(true, null, runId, actionId, result.Status,
            masker.Mask(result.Summary), masker.Mask(data), masker.Mask(verification),
            undoJson is not null);
    }

    public const string AwaitingApprovalSummary =
        "Sent to the helpdesk for approval. It runs as soon as a helpdesk member approves it, and the user sees the result in this chat.";

    /// <summary>ItApproval actions, and any fix an administrator switched to "needs approval".</summary>
    private string? MachineName(string agentId) =>
        registry.GetOnlineAgents().FirstOrDefault(agent => agent.AgentId == agentId)?.MachineName;

    public static bool NeedsApproval(EndpointActionDefinition definition, ActionSetting? setting) =>
        definition.Tier == ActionTier.ItApproval
        || setting is { RequiresApproval: true } && definition.Tier is ActionTier.SafeFix or ActionTier.Disruptive;

    private static string Describe(IReadOnlyDictionary<string, string> arguments,
        IReadOnlyDictionary<string, ResolvedTarget> targets) =>
        targets.Count > 0 ? ": " + string.Join(", ", targets.Values.Select(target => target.Label))
        : arguments.Count > 0 ? ": " + string.Join(", ", arguments.Values) : string.Empty;

    /// <summary>Helpdesk undo of a fix that saved its previous values.</summary>
    public async Task<ActionRunOutcome> UndoAsync(string runId, string? requestedBy,
        CancellationToken cancellationToken)
    {
        var run = await store.GetRunAsync(runId, cancellationToken);
        var definition = run is null ? null : EndpointActionCatalog.Find(run.ActionId);
        if (run?.UndoJson is null || definition?.UndoActionId is null || run.UndoneAtUtc is not null)
            return ActionRunOutcome.Rejected(run?.ActionId ?? "", "This run cannot be undone.");
        var arguments = JsonSerializer.Deserialize<Dictionary<string, string?>>(run.UndoJson)!
            .Where(item => !string.IsNullOrEmpty(item.Value))
            .ToDictionary(item => item.Key, item => item.Value);
        var outcome = await RunAsync(new ActionRunContext(run.AgentId, null, run.ConversationId,
            ActionRunSources.Operator, requestedBy), definition.UndoActionId, arguments, true,
            cancellationToken);
        if (outcome.Status is ActionStatus.Done or ActionStatus.NotNeeded)
            await store.MarkUndoneAsync(runId, cancellationToken);
        return outcome;
    }

    /// <summary>Sanitized JSON for the agent: masked names, no IP addresses or identifiers.</summary>
    public static string ToToolResult(ActionRunOutcome outcome) =>
        ToolResultSanitizer.Sanitize(outcome.Accepted
            ? new
            {
                ok = outcome.Status is not ActionStatus.Failed,
                actionId = outcome.ActionId,
                status = outcome.Status?.ToString(),
                summary = outcome.Summary,
                data = outcome.Data,
                verification = outcome.Verification
            }
            : new { ok = false, actionId = outcome.ActionId, error = outcome.Error });

    private async Task<JsonNode?> RunVerificationAsync(ActionRunContext context,
        ActionVerification verify, IReadOnlyDictionary<string, string> arguments,
        IReadOnlyDictionary<string, string> environment, IReadOnlyList<EnvironmentTarget> targets,
        CancellationToken cancellationToken)
    {
        var definition = EndpointActionCatalog.Find(verify.ActionId);
        if (definition is null) return null;
        var verifyArguments = new Dictionary<string, string>(StringComparer.Ordinal);
        if (verify.PassSameArguments)
            foreach (var parameter in definition.Parameters)
                if (arguments.TryGetValue(parameter.Name, out var value))
                    verifyArguments[parameter.Name] = value;
        foreach (var (key, value) in verify.FixedArguments ?? new Dictionary<string, string>())
            verifyArguments[key] = value;
        var request = new EndpointActionRequest(definition.Id, verifyArguments, null,
            (definition.UsesSettings ?? []).Where(environment.ContainsKey)
                .ToDictionary(key => key, key => environment[key], StringComparer.Ordinal),
            context.UserSid);
        var (result, error) = await DispatchAsync(context.AgentId, request, cancellationToken);
        return result is null ? JsonValue.Create(error)
            : new JsonObject
            {
                ["summary"] = result.Summary,
                ["data"] = Annotate(definition.Id, ToNode(result.Data), targets)
            };
    }

    private async Task<(EndpointActionResult? Result, string? Error)> DispatchAsync(
        string agentId, EndpointActionRequest request, CancellationToken cancellationToken)
    {
        var rpc = RpcRequest.Create(Guid.NewGuid().ToString("N"), EndpointActionProtocol.Method,
            JsonSerializer.SerializeToElement(request, EndpointActionProtocol.Json));
        var response = await registry.SendCommandAsync(agentId, rpc, cancellationToken);
        if (response.Error is not null)
            return (null, response.Error.Code == 404 && response.Error.Message.StartsWith(
                    "Unknown endpoint command", StringComparison.Ordinal)
                ? "This PC runs an older endpoint version without general actions."
                : response.Error.Message);
        try
        {
            var element = response.Result is JsonElement json ? json
                : JsonSerializer.SerializeToElement(response.Result, EndpointActionProtocol.Json);
            return (element.Deserialize<EndpointActionResult>(EndpointActionProtocol.Json), null);
        }
        catch (JsonException)
        {
            return (null, "The endpoint sent an unreadable result.");
        }
    }

    private Task TraceAsync(ActionRunContext context, string actionId, string outcome,
        string? summary, CancellationToken cancellationToken) =>
        context.ConversationId is null ? Task.CompletedTask
            : store.AppendConversationTraceAsync(context.ConversationId, outcome,
                new { actionId, summary, source = context.Source }, cancellationToken);

    internal static bool PortAllowed(ResolvedTarget target, int port) =>
        (target.Ports ?? []).Contains(port)
        || target.Kind == EnvironmentTargetKinds.WebApp
            && Uri.TryCreate(target.Address, UriKind.Absolute, out var uri) && uri.Port == port;

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        _ => JsonSerializer.SerializeToNode(value, EndpointActionProtocol.Json)
    };

    /// <summary>Adds environment-map matches the PC cannot know about.</summary>
    internal static JsonNode? Annotate(string actionId, JsonNode? data,
        IReadOnlyList<EnvironmentTarget> targets)
    {
        if (data is not JsonObject root) return data;
        switch (actionId)
        {
            case "printer.list" when root["printers"] is JsonArray printers:
                foreach (var printer in printers.OfType<JsonObject>())
                    printer["matchesTarget"] = TargetMasker.Match(targets,
                        printer["name"]?.GetValue<string>())?.Id;
                break;
            case "drives.list" when root["drives"] is JsonArray drives:
                var mapped = new List<(string Letter, string? Target, bool Connected)>();
                foreach (var drive in drives.OfType<JsonObject>())
                {
                    var match = TargetMasker.Match(targets, drive["share"]?.GetValue<string>());
                    drive["matchesTarget"] = match?.Id;
                    mapped.Add((drive["letter"]?.GetValue<string>() ?? "", match?.Id,
                        drive["connected"]?.GetValue<bool>() == true));
                }
                root["missingStandardDrives"] = new JsonArray(targets
                    .Where(target => target.Kind == EnvironmentTargetKinds.Share
                        && target.DriveLetter is not null
                        && !mapped.Any(item => item.Target == target.Id && item.Connected))
                    .Select(target => (JsonNode)JsonValue.Create(target.Id)!).ToArray());
                break;
            case "printer.status":
                root["matchesTarget"] = TargetMasker.Match(targets,
                    root["name"]?.GetValue<string>())?.Id;
                break;
            case "software.inventory" when root["apps"] is JsonArray apps:
                var removable = targets.Where(target => target.Kind == EnvironmentTargetKinds.App).ToArray();
                foreach (var app in apps.OfType<JsonObject>())
                {
                    var name = app["name"]?.GetValue<string>();
                    var publisher = app["publisher"]?.GetValue<string>();
                    var match = removable.FirstOrDefault(target =>
                        EndpointActionValidator.MatchesAppPattern(target.Address, name)
                        && (target.Detail(EnvironmentTargetDetails.Publisher) is not { } wanted
                            || EndpointActionValidator.MatchesAppPattern(wanted, publisher)));
                    app["removable"] = match?.Id;
                    app["uninstallPackageId"] = match?.Detail(EnvironmentTargetDetails.PackageId);
                }
                break;
            case "drivers.inventory" when root["devices"] is JsonArray devices:
                var catalog = targets.Where(target => target.Kind == EnvironmentTargetKinds.Device)
                    .Select(target => (Target: target,
                        Ids: EndpointActionValidator.SplitHardwareIds(target.Address)))
                    .ToArray();
                foreach (var device in devices.OfType<JsonObject>())
                {
                    var ids = (device["hardwareIds"] as JsonArray ?? [])
                        .Select(item => item?.GetValue<string>()).OfType<string>().ToArray();
                    var match = catalog.FirstOrDefault(entry => entry.Ids.Any(id =>
                        ids.Any(reported => reported.Equals(id, StringComparison.OrdinalIgnoreCase)
                            || reported.StartsWith(id + "&", StringComparison.OrdinalIgnoreCase))));
                    device["catalogMatch"] = match.Target is null ? null : new JsonObject
                    {
                        ["target"] = match.Target.Id,
                        ["model"] = match.Target.Label,
                        ["packageId"] = match.Target.Detail(EnvironmentTargetDetails.PackageId)
                    };
                }
                break;
            case "wifi.status" when root["profiles"] is JsonArray profiles:
                var company = targets.Where(target => target.Kind == EnvironmentTargetKinds.Wifi)
                    .Select(target => (Target: target,
                        Profile: target.Detail(EnvironmentTargetDetails.Profile) ?? target.Address))
                    .ToArray();
                var saved = profiles.Select(item => item?.GetValue<string>()).OfType<string>().ToArray();
                root["companyProfiles"] = new JsonArray(company
                    .Select(entry => (JsonNode)new JsonObject
                    {
                        ["target"] = entry.Target.Id,
                        ["label"] = entry.Target.Label,
                        ["saved"] = saved.Contains(entry.Profile, StringComparer.OrdinalIgnoreCase),
                        ["connected"] = string.Equals(root["ssid"]?.GetValue<string>(),
                            entry.Target.Address, StringComparison.Ordinal)
                    }).ToArray());
                break;
            case "zones.status" when root["entries"] is JsonArray entries:
                // Only environment-map ids leave the control plane; the PC's own host list stays here.
                var zoneHosts = entries.OfType<JsonObject>()
                    .Select(entry => (Host: ZoneHost(entry["host"]?.GetValue<string>()),
                        Zone: entry["zone"]?.GetValue<string>() ?? "Unknown",
                        Policy: entry["setByPolicy"]?.GetValue<bool>() == true))
                    .Where(entry => entry.Host.Length > 0)
                    .ToArray();
                root["sites"] = new JsonArray(targets
                    .Where(target => target.Kind == EnvironmentTargetKinds.WebApp)
                    .Select(target =>
                    {
                        var host = EndpointActionValidator.HostOf(target.ToResolved()) ?? "";
                        var match = zoneHosts.FirstOrDefault(entry =>
                            host.Equals(entry.Host, StringComparison.OrdinalIgnoreCase)
                            || host.EndsWith("." + entry.Host, StringComparison.OrdinalIgnoreCase));
                        return (JsonNode)new JsonObject
                        {
                            ["target"] = target.Id,
                            ["zone"] = match.Host is null ? "Internet" : match.Zone,
                            ["setByPolicy"] = match.Policy
                        };
                    }).ToArray());
                root["entryCount"] = entries.Count;
                root.Remove("entries");
                break;
            case "credman.list" when root["credentials"] is JsonArray credentials:
                foreach (var credential in credentials.OfType<JsonObject>())
                {
                    var server = credential["server"]?.GetValue<string>();
                    credential["matchesTarget"] = targets.FirstOrDefault(target =>
                        string.Equals(EndpointActionValidator.HostOf(target.ToResolved()), server,
                            StringComparison.OrdinalIgnoreCase))?.Id;
                }
                break;
        }
        return root;
    }

    /// <summary>A zone-map entry as a bare host: no scheme, no leading "*.".</summary>
    private static string ZoneHost(string? value)
    {
        var host = (value ?? "").Trim();
        var scheme = host.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) host = host[(scheme + 3)..];
        host = host.Split('/', ':')[0];
        return host.StartsWith("*.", StringComparison.Ordinal) ? host[2..] : host;
    }
}
