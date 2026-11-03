using ControlPlane.Api.Files;
using ControlPlane.Api.Security;

namespace ControlPlane.Api.Software;

public sealed record NoApprovalRequest(bool NoApproval);

/// <summary>The "No approval needed" list on the Packages page.</summary>
public static class SoftwareEndpoints
{
    public static void MapSoftwareEndpoints(this WebApplication app)
    {
        app.MapGet("/api/software/no-approval", async (SoftwareApprovalStore store,
            CancellationToken cancellationToken) => Results.Ok(await store.GetAsync(cancellationToken)))
            .RequireAuthorization(OperatorPolicies.Operator);

        app.MapPut("/api/software/no-approval/{packageId}", async (string packageId,
            NoApprovalRequest request, HttpContext context, SoftwareApprovalStore store,
            FileArtifactRepository files, CancellationToken cancellationToken) =>
        {
            if (request.NoApproval)
            {
                var problem = SoftwareApprovalStore.CannotSkipApproval(
                    await files.GetRepairPackageAsync(packageId, cancellationToken));
                if (problem is not null) return Results.BadRequest(new { message = problem });
            }
            var by = context.User.Identity?.Name is { Length: > 0 } name ? name : "operator";
            await store.SetAsync(packageId, request.NoApproval, by, cancellationToken);
            return Results.Ok(await store.GetAsync(cancellationToken));
        }).RequireAuthorization(OperatorPolicies.Admin);
    }
}
