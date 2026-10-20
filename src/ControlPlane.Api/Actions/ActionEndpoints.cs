using ControlPlane.Api.Security;
using EndpointAgents.Shared.Actions;
using System.Net;
using System.Text.RegularExpressions;

namespace ControlPlane.Api.Actions;

public sealed record SkillReviewRequest(int? Version, string? Note);
public sealed record SkillLintRequest(string Name, string Description, string Body);
public sealed record MissingActionRequest(string Summary, string? Details);

/// <summary>
/// Operator (loopback dashboard) routes for the capabilities page, the environment map,
/// incidents, the action run log and helpdesk-written skills. Every route needs a signed-in
/// operator (the fallback policy); members read and draft, reviewers publish skills and undo
/// fixes, admins change the environment map, standards and action switches.
/// </summary>
public static partial class ActionEndpoints
{
    private static readonly TimeSpan StatsWindow = TimeSpan.FromDays(30);

    public static void MapActionEndpoints(this WebApplication app)
    {
        // ---------------- Capabilities page ----------------
        app.MapGet("/api/capabilities", async (ActionStore store, SkillService skills,
            ActiveDirectoryAccountChecker directory, CancellationToken cancellationToken) =>
        {
            var settings = await store.GetActionSettingsAsync(cancellationToken);
            var stats = await store.GetUsageStatsAsync(StatsWindow, cancellationToken);
            return Results.Ok(new
            {
                generatedAtUtc = DateTimeOffset.UtcNow,
                areas = ActionAreas.All,
                targetKinds = EnvironmentTargetKinds.All,
                settingKeys = ActionSettings.All,
                accountChecksConfigured = directory.Configured,
                skillsPublishToHermes = skills.CanWriteToHermes,
                tools = SkillService.KnownTools.Order(StringComparer.Ordinal),
                actions = EndpointActionCatalog.All.Select(definition =>
                {
                    var setting = settings.GetValueOrDefault(definition.Id);
                    return new
                    {
                        definition.Id,
                        definition.Area,
                        Tier = definition.Tier.ToString(),
                        RunAs = definition.RunAs.ToString(),
                        definition.Name,
                        definition.NameFa,
                        definition.Description,
                        definition.Returns,
                        definition.TimeoutSeconds,
                        DefaultMaxPerHour = definition.EffectiveMaxPerHour,
                        MaxPerHour = setting?.MaxPerHour ?? definition.EffectiveMaxPerHour,
                        Enabled = setting?.Enabled ?? true,
                        RequiresApproval = ActionRunner.NeedsApproval(definition, setting),
                        ApprovalConfigurable = definition.Tier is ActionTier.SafeFix or ActionTier.Disruptive,
                        VerifiedBy = definition.Verify?.ActionId,
                        definition.UndoActionId,
                        definition.ExampleResult,
                        definition.UsesSettings,
                        Parameters = definition.Parameters.Select(parameter => new
                        {
                            parameter.Name,
                            Kind = parameter.Kind.ToString(),
                            parameter.Description,
                            parameter.Required,
                            parameter.Choices,
                            parameter.TargetKinds,
                            parameter.Min,
                            parameter.Max
                        }),
                        Stats = stats.GetValueOrDefault(definition.Id)
                    };
                })
            });
        });
        app.MapPut("/api/actions/{actionId}/settings", async (string actionId,
            ActionSettingInput input, HttpContext context, ActionStore store,
            CancellationToken cancellationToken) =>
        {
            if (EndpointActionCatalog.Find(actionId) is null)
                return Results.NotFound(new { message = "Unknown action." });
            if (input.MaxPerHour is < 0 or > 1000)
                return Results.BadRequest(new { message = "The hourly limit must be 0 to 1000." });
            await store.SaveActionSettingAsync(actionId, input, Operator(context), cancellationToken);
            return Results.NoContent();
        })
            .RequireAuthorization(OperatorPolicies.Admin);
        app.MapPost("/api/actions/requests", async (MissingActionRequest request,
            HttpContext context, ActionStore store, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Summary))
                return Results.BadRequest(new { message = "Describe the action you need." });
            await store.RecordMissingActionRequestAsync(request.Summary, request.Details ?? "",
                Operator(context), cancellationToken);
            return Results.NoContent();
        });

        // ---------------- Approvals (steps the agent sent to the helpdesk) ----------------
        app.MapGet("/api/approvals", async (bool? pendingOnly, int? limit, ActionStore store,
            CancellationToken cancellationToken) =>
            Results.Ok(await store.GetApprovalsAsync(pendingOnly ?? false, limit ?? 100,
                cancellationToken)));
        app.MapPost("/api/approvals/{approvalId}/approve", async (string approvalId,
            ApprovalDecisionInput? input, HttpContext context, ApprovalService approvals,
            CancellationToken cancellationToken) =>
        {
            var outcome = await approvals.ApproveAsync(approvalId, Operator(context), input?.Note,
                cancellationToken);
            return outcome.Accepted ? Results.Ok(outcome.Approval)
                : outcome.Approval is null ? Results.NotFound(new { message = outcome.Error })
                : Results.Conflict(new { message = outcome.Error });
        });
        app.MapPost("/api/approvals/{approvalId}/decline", async (string approvalId,
            ApprovalDecisionInput? input, HttpContext context, ApprovalService approvals,
            CancellationToken cancellationToken) =>
        {
            var outcome = await approvals.DeclineAsync(approvalId, Operator(context), input?.Note,
                cancellationToken);
            return outcome.Accepted ? Results.Ok(outcome.Approval)
                : outcome.Approval is null ? Results.NotFound(new { message = outcome.Error })
                : Results.Conflict(new { message = outcome.Error });
        });

        // ---------------- Run log and undo ----------------
        app.MapGet("/api/actions/runs", async (string? agentId, string? conversationId,
            int? limit, ActionStore store, CancellationToken cancellationToken) =>
            Results.Ok(await store.GetRecentRunsAsync(Empty(agentId), Empty(conversationId),
                limit ?? 100, cancellationToken)));
        app.MapPost("/api/actions/runs/{runId}/undo", async (string runId, HttpContext context,
            ActionRunner runner, CancellationToken cancellationToken) =>
        {
            var outcome = await runner.UndoAsync(runId, Operator(context), cancellationToken);
            return outcome.Accepted ? Results.Ok(outcome) : Results.BadRequest(new { message = outcome.Error });
        })
            .RequireAuthorization(OperatorPolicies.Reviewer);
        // ---------------- Environment map ----------------
        app.MapGet("/api/environment/targets", async (ActionStore store,
            CancellationToken cancellationToken) =>
            Results.Ok(await store.GetTargetsAsync(cancellationToken)));
        app.MapPut("/api/environment/targets/{targetId}", async (string targetId,
            EnvironmentTargetInput input, ActionStore store, CancellationToken cancellationToken) =>
        {
            var error = ValidateTarget(input with { Id = targetId });
            if (error is not null) return Results.BadRequest(new { message = error });
            await store.UpsertTargetAsync(input with { Id = targetId }, cancellationToken);
            return Results.NoContent();
        })
            .RequireAuthorization(OperatorPolicies.Admin);
        app.MapDelete("/api/environment/targets/{targetId}", async (string targetId,
            ActionStore store, CancellationToken cancellationToken) =>
            await store.DeleteTargetAsync(targetId, cancellationToken) > 0
                ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(OperatorPolicies.Admin);
        app.MapGet("/api/environment/settings", async (ActionStore store,
            CancellationToken cancellationToken) =>
            Results.Ok(await store.GetSettingsAsync(cancellationToken)));
        app.MapPut("/api/environment/settings", async (Dictionary<string, string?> values,
            ActionStore store, CancellationToken cancellationToken) =>
        {
            foreach (var (key, value) in values)
                if (ValidateSetting(key, value) is { } error)
                    return Results.BadRequest(new { message = error });
            await store.SaveSettingsAsync(values, cancellationToken);
            return Results.NoContent();
        })
            .RequireAuthorization(OperatorPolicies.Admin);
        // ---------------- Incidents ----------------
        app.MapGet("/api/incidents", async (bool? activeOnly, ActionStore store,
            CancellationToken cancellationToken) =>
            Results.Ok(await store.GetIncidentsAsync(activeOnly ?? false, cancellationToken)));
        app.MapPost("/api/incidents", async (IncidentInput input, HttpContext context,
            ActionStore store, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Length > 200)
                return Results.BadRequest(new { message = "A title of up to 200 characters is required." });
            if (input.AffectedTargets?.Any(id => !EndpointActionValidator.IsValidTargetId(id)) == true)
                return Results.BadRequest(new { message = "Affected targets must be environment-map ids." });
            return Results.Ok(await store.CreateIncidentAsync(input, Operator(context), cancellationToken));
        });
        app.MapMethods("/api/incidents/{incidentId}", ["PATCH"], async (string incidentId,
            IncidentPatch patch, ActionStore store, CancellationToken cancellationToken) =>
        {
            if (patch.Status is not null && patch.Status != IncidentStatus.Active
                && patch.Status != IncidentStatus.Resolved)
                return Results.BadRequest(new { message = "Status must be Active or Resolved." });
            return await store.PatchIncidentAsync(incidentId, patch, cancellationToken)
                ? Results.NoContent() : Results.NotFound();
        });

        // ---------------- Helpdesk skills ----------------
        app.MapGet("/api/skills", async (ActionStore store, CancellationToken cancellationToken) =>
        {
            var stats = await store.GetSkillStatsAsync(StatsWindow, cancellationToken);
            return Results.Ok((await store.GetSkillsAsync(cancellationToken)).Select(skill => new
            {
                skill,
                stats = stats.GetValueOrDefault(skill.Name)
            }));
        });
        app.MapGet("/api/skills/template", () => Results.Ok(new { body = SkillService.Template }));
        app.MapPost("/api/skills/lint", (SkillLintRequest request) =>
            Results.Ok(SkillService.Lint(request.Name ?? "", request.Description ?? "",
                request.Body ?? "")));
        app.MapGet("/api/skills/from-conversation/{conversationId}", async (string conversationId,
            SkillService skills, CancellationToken cancellationToken) =>
            await skills.DraftFromConversationAsync(conversationId, cancellationToken) is { } draft
                ? Results.Ok(draft) : Results.NotFound());
        app.MapGet("/api/skills/{name}", async (string name, ActionStore store,
            CancellationToken cancellationToken) =>
        {
            var skill = await store.GetSkillAsync(name, cancellationToken);
            return skill is null ? Results.NotFound() : Results.Ok(new
            {
                skill,
                versions = await store.GetSkillVersionsAsync(name, cancellationToken),
                rendered = SkillService.Render(skill),
                issues = SkillService.Lint(skill.Name, skill.Description, skill.Body)
            });
        });
        app.MapPost("/api/skills", async (SkillDraftInput input, HttpContext context,
            ActionStore store, CancellationToken cancellationToken) =>
        {
            var issues = SkillService.Lint(input.Name ?? "", input.Description ?? "", input.Body ?? "");
            if (!SkillService.IsValidName(input.Name) || string.IsNullOrWhiteSpace(input.Body))
                return Results.BadRequest(new { message = "Name and steps are required.", issues });
            var saved = await store.SaveSkillDraftAsync(input with
            {
                Body = input.Body.Length > SkillService.MaxBodyCharacters
                    ? input.Body[..SkillService.MaxBodyCharacters] : input.Body,
                Description = (input.Description ?? "").Trim()
            }, Operator(context), cancellationToken);
            return Results.Ok(new { skill = saved, issues });
        });
        app.MapPost("/api/skills/{name}/submit", async (string name, HttpContext context,
            ActionStore store, CancellationToken cancellationToken) =>
        {
            var skill = await store.GetSkillAsync(name, cancellationToken);
            if (skill is null) return Results.NotFound();
            var issues = SkillService.Lint(skill.Name, skill.Description, skill.Body);
            if (issues.Any(issue => issue.Severity == "error"))
                return Results.BadRequest(new { message = "Fix the errors first.", issues });
            await store.SetSkillStatusAsync(name, SkillStatus.InReview, null, null, cancellationToken);
            return Results.NoContent();
        });
        app.MapPost("/api/skills/{name}/publish", async (string name, SkillReviewRequest request,
            HttpContext context, SkillService skills, CancellationToken cancellationToken) =>
        {
            var result = await skills.PublishAsync(name, request.Version, Operator(context),
                request.Note, cancellationToken);
            return result.Published ? Results.Ok(result) : Results.BadRequest(result);
        })
            .RequireAuthorization(OperatorPolicies.Reviewer);
        app.MapPost("/api/skills/{name}/reject", async (string name, SkillReviewRequest request,
            HttpContext context, ActionStore store, CancellationToken cancellationToken) =>
        {
            if (await store.GetSkillAsync(name, cancellationToken) is null) return Results.NotFound();
            await store.SetSkillStatusAsync(name, SkillStatus.Draft, Operator(context),
                request.Note, cancellationToken);
            return Results.NoContent();
        })
            .RequireAuthorization(OperatorPolicies.Reviewer);
        app.MapPost("/api/skills/{name}/retire", async (string name, SkillReviewRequest request,
            HttpContext context, SkillService skills, CancellationToken cancellationToken) =>
            await skills.RetireAsync(name, Operator(context), request.Note, cancellationToken)
                ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(OperatorPolicies.Reviewer);
        app.MapGet("/api/skills/{name}/download", async (string name, ActionStore store,
            CancellationToken cancellationToken) =>
        {
            var skill = await store.GetSkillAsync(name, cancellationToken);
            return skill is null ? Results.NotFound()
                : Results.Text(SkillService.Render(skill), "text/markdown; charset=utf-8");
        });
    }

    internal static string? ValidateTarget(EnvironmentTargetInput input)
    {
        if (!EndpointActionValidator.IsValidTargetId(input.Id))
            return "Id: lowercase English letters, digits and dashes, up to 48 characters.";
        if (!EnvironmentTargetKinds.All.Contains(input.Kind))
            return $"Kind must be one of: {string.Join(", ", EnvironmentTargetKinds.All)}.";
        if (input.Details?.Keys.FirstOrDefault(key => !EnvironmentTargetDetails.For(input.Kind).Contains(key)) is { } extra)
            return $"'{extra}' does not apply to a {input.Kind} entry.";
        if (string.IsNullOrWhiteSpace(input.Label) || input.Label.Length > 80)
            return "A label of up to 80 characters is required.";
        if (input.Ports is { } ports && (ports.Count > 8 || ports.Any(port => port is < 1 or > 65535)))
            return "Up to 8 ports between 1 and 65535.";
        if (input.ExpectedAddresses is { } addresses && (addresses.Count > 8
                || addresses.Any(address => !IPAddress.TryParse(address, out _))))
            return "Expected addresses must be up to 8 IP addresses.";
        if (input.DriveLetter is not null && (input.Kind != EnvironmentTargetKinds.Share
                || !EndpointActionValidator.IsValidDriveLetter(input.DriveLetter.ToUpperInvariant())))
            return "A drive letter (D: to Z:) only fits a share.";
        if (input.Location?.Length > 120 || input.Notes?.Length > 1000)
            return "Location or notes are too long.";
        var resolved = new ResolvedTarget(input.Id, input.Kind, input.Label, input.Address?.Trim() ?? "",
            Details: input.Details);
        return EndpointActionValidator.IsValidTarget(resolved) ? null : input.Kind switch
        {
            EnvironmentTargetKinds.WebApp => "Address must be an http(s) URL, e.g. https://hr.corp.local.",
            EnvironmentTargetKinds.Host => "Address must be a host name or IPv4 address.",
            EnvironmentTargetKinds.App => "Name must be the Apps & features name (or a pattern with *, e.g. \"Foxit PDF Reader*\"); publisher the same; package id lowercase.",
            EnvironmentTargetKinds.Device => "Hardware IDs must look like USB\\VID_04F9&PID_0042, separated by semicolons (up to 20); package id lowercase.",
            EnvironmentTargetKinds.Vpn => "Address must be the VPN server host name; type builtin needs the Windows connection name, type client the client's service name.",
            EnvironmentTargetKinds.Wifi => "Address must be the Wi-Fi network name (SSID), up to 32 characters without quotes.",
            EnvironmentTargetKinds.Site => "Address must be a site host name such as bank.example.com, or *.example.com for every name under it.",
            _ => @"Address must be a UNC path, e.g. \\server\share."
        };
    }

    internal static string? ValidateSetting(string key, string? value)
    {
        if (!ActionSettings.All.Contains(key)) return $"Unknown setting '{key}'.";
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.Length > 1000) return $"'{key}' is too long.";
        return key switch
        {
            ActionSettings.ProxyMode when value is not ("none" or "pac" or "server") =>
                "proxy.mode must be none, pac or server.",
            ActionSettings.ProxyPacUrl when !EndpointActionValidator.IsValidWebAddress(value) =>
                "proxy.pacUrl must be an http(s) URL.",
            ActionSettings.ProxyServer when !ProxyServerPattern().IsMatch(value) =>
                "proxy.server must be host:port.",
            ActionSettings.RootCaThumbprints when !value.Split(',').All(item =>
                    ThumbprintPattern().IsMatch(item.Trim())) =>
                "trust.rootThumbprints must be comma-separated 40-character SHA-1 thumbprints.",
            ActionSettings.InternalDnsSuffixes when !value.Split(',').All(item =>
                    EndpointActionValidator.IsValidHost(item.Trim().TrimStart('.'))) =>
                "network.internalSuffixes must be comma-separated DNS suffixes.",
            ActionSettings.StandardTimeZone when value.Length > 100 || value.Any(char.IsControl) =>
                "time.standardZone must be a Windows time zone id.",
            ActionSettings.DnsFromDhcp when value is not ("true" or "false") =>
                "network.dnsFromDhcp must be true or false.",
            ActionSettings.IpFromDhcp when value is not ("true" or "false") =>
                "network.ipFromDhcp must be true or false.",
            ActionSettings.PowerPlan when value is not ("balanced" or "high" or "saver") =>
                "power.standardPlan must be balanced, high or saver.",
            _ => null
        };
    }

    private static string Operator(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true && context.User.Identity.Name is { Length: > 0 } name
            ? name : "operator";

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.\-]{0,252}:\d{1,5}$")]
    private static partial Regex ProxyServerPattern();

    [GeneratedRegex(@"^[0-9A-Fa-f]{40}$")]
    private static partial Regex ThumbprintPattern();
}
