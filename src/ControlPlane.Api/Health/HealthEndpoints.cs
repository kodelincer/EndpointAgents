using ControlPlane.Api.Security;
using EndpointAgents.Shared.Actions;

namespace ControlPlane.Api.Health;

public sealed record HealthRuleEnabledRequest(bool Enabled);

/// <summary>The Health rules (Compliance) page: fixed checks, rules and their latest results.</summary>
public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/api/health/checks", () => Results.Ok(HealthCheckCatalog.All.Select(check => new
        {
            check.Id, check.Name, check.NameFa, check.Description, check.OptionLabel, check.DefaultOption,
            Options = check.Options,
            Fixes = check.Fixes.Select(id => new { Id = id, Name = EndpointActionCatalog.Find(id)?.Name ?? id }),
            CanTellUser = check.NoticeId is not null,
            Notice = check.NoticeId is null ? null : UserNotices.Choices.First(choice => choice.Value == check.NoticeId).Label
        }))).RequireAuthorization(OperatorPolicies.Operator);

        app.MapGet("/api/health/rules", async (HealthRuleStore store, CancellationToken cancellationToken) =>
            Results.Ok(await store.SummariesAsync(cancellationToken))).RequireAuthorization(OperatorPolicies.Operator);

        app.MapGet("/api/health/rules/{ruleId}/results", async (string ruleId, HealthRuleStore store,
            CancellationToken cancellationToken) => Results.Ok(await store.ResultsAsync(ruleId, cancellationToken)))
            .RequireAuthorization(OperatorPolicies.Operator);

        app.MapPost("/api/health/rules", async (HealthRuleInput input, HttpContext context, HealthRuleStore store,
            CancellationToken cancellationToken) =>
        {
            if (HealthRuleStore.Validate(input) is { } problem) return Results.BadRequest(new { message = problem });
            if (await store.CountAsync(cancellationToken) >= HealthRuleStore.MaxRules)
                return Results.BadRequest(new { message = $"At most {HealthRuleStore.MaxRules} rules." });
            var by = context.User.Identity?.Name is { Length: > 0 } name ? name : "admin";
            return Results.Ok(await store.CreateAsync(input, by, cancellationToken));
        }).RequireAuthorization(OperatorPolicies.Admin);

        app.MapPatch("/api/health/rules/{ruleId}", async (string ruleId, HealthRuleEnabledRequest request,
            HealthRuleStore store, CancellationToken cancellationToken) =>
            await store.SetEnabledAsync(ruleId, request.Enabled, cancellationToken) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(OperatorPolicies.Admin);

        app.MapDelete("/api/health/rules/{ruleId}", async (string ruleId, HealthRuleStore store,
            CancellationToken cancellationToken) =>
            await store.DeleteAsync(ruleId, cancellationToken) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(OperatorPolicies.Admin);
    }
}
