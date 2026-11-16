using ControlPlane.Api.Security;
using Microsoft.AspNetCore.Authorization;

namespace ControlPlane.Api.Files.Builder;

/// <summary>
/// The dashboard "Package builder" page: read a BAT's menus, load a package into the
/// form, and save the form as a package folder. Loopback-only like the rest of /api/files.
/// Saving never signs: a saved package stays unpublished until it is signed.
/// </summary>
public static class PackageBuilderEndpoints
{
    public static void MapPackageBuilderEndpoints(this WebApplication app)
    {
        app.MapPost("/api/files/packages/builder/scan", (PackageFileUpload upload) =>
        {
            var (result, error) = PackageBuilder.Scan(upload);
            return result is null ? Results.BadRequest(new { message = error }) : Results.Ok(result);
        }).RequireAuthorization(OperatorPolicies.Operator);

        app.MapGet("/api/files/packages/builder/{packageId}", async (string packageId,
            PackageBuilder builder, CancellationToken cancellationToken) =>
            await builder.LoadAsync(packageId, cancellationToken) is { } draft
                ? Results.Ok(draft)
                : Results.NotFound(new { message = "No readable package with that id." }))
            .RequireAuthorization(OperatorPolicies.Operator);

        app.MapPost("/api/files/packages/builder", async (PackageBuildRequest request,
            HttpContext context, PackageBuilder builder, IAuthorizationService authorization,
            CancellationToken cancellationToken) =>
        {
            var author = context.User.Identity?.Name is { Length: > 0 } name ? name : "operator";
            var isAdmin = (await authorization.AuthorizeAsync(context.User,
                OperatorPolicies.Admin)).Succeeded;
            var result = await builder.SaveAsync(request, author, isAdmin, cancellationToken);
            return result.Saved ? Results.Ok(result) : Results.BadRequest(new { message = result.Error });
        }).RequireAuthorization(OperatorPolicies.Operator);
    }
}
