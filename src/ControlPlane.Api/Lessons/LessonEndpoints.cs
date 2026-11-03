using ControlPlane.Api.Security;

namespace ControlPlane.Api.Lessons;

public static class LessonEndpoints
{
    public static void MapLessonEndpoints(this WebApplication app)
    {
        app.MapGet("/api/lessons", async (string? status, LessonStore lessons, CancellationToken cancellationToken) =>
            status is not null && !LessonStatuses.All.Contains(status)
                ? Results.BadRequest(new { message = "Unknown status." })
                : Results.Ok(new
                {
                    lessons = await lessons.ListAsync(status, 500, cancellationToken),
                    fixes = await lessons.FixStatsAsync(cancellationToken)
                })).RequireAuthorization(OperatorPolicies.Operator);

        app.MapPatch("/api/lessons/{lessonId}", async (string lessonId, LessonPatch patch, HttpContext context,
            LessonStore lessons, CancellationToken cancellationToken) =>
        {
            if (patch.Status is not null && !LessonStatuses.All.Contains(patch.Status))
                return Results.BadRequest(new { message = "Unknown status." });
            var by = context.User.Identity?.Name is { Length: > 0 } name ? name : "reviewer";
            return await lessons.PatchAsync(lessonId, patch, by, cancellationToken) ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization(OperatorPolicies.Reviewer);
    }
}
