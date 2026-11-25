using ControlPlane.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;

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

        // An installer for an install package. The body is the file itself (not JSON or a
        // form), streamed to disk, so large installers work. Nothing is run.
        app.MapPost("/api/files/packages/builder/uploads", async (string? fileName,
            HttpContext context, InstallerUploads uploads, CancellationToken cancellationToken) =>
        {
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = uploads.MaximumBytes + 1;
            if (context.Request.ContentLength > uploads.MaximumBytes)
                return Results.BadRequest(new { message = $"The installer is larger than {uploads.MaximumBytes / 1024 / 1024} MB." });
            var (info, error) = await uploads.SaveAsync(context.Request.Body, fileName, cancellationToken);
            return info is null ? Results.BadRequest(new { message = error }) : Results.Ok(info);
        }).RequireAuthorization(OperatorPolicies.Operator);

        // A supporting file for a BAT package's files\ folder, sent the same way.
        app.MapPost("/api/files/packages/builder/support-uploads", async (string? fileName,
            HttpContext context, InstallerUploads uploads, CancellationToken cancellationToken) =>
        {
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = uploads.MaximumBytes + 1;
            if (context.Request.ContentLength > uploads.MaximumBytes)
                return Results.BadRequest(new { message = $"The file is larger than {uploads.MaximumBytes / 1024 / 1024} MB." });
            var (info, error) = await uploads.SaveSupportAsync(context.Request.Body, fileName, cancellationToken);
            return info is null ? Results.BadRequest(new { message = error }) : Results.Ok(info);
        }).RequireAuthorization(OperatorPolicies.Operator);

        app.MapGet("/api/files/packages/builder/{packageId}", async (string packageId,
            PackageBuilder builder, CancellationToken cancellationToken) =>
            await builder.LoadAsync(packageId, cancellationToken) is { } draft
                ? Results.Ok(draft)
                : Results.NotFound(new { message = "No readable package with that id." }))
            .RequireAuthorization(OperatorPolicies.Operator);

        app.MapGet("/api/files/packages/approval", (PackageBuilder builder) =>
            Results.Ok(builder.GetApprovalInfo()))
            .RequireAuthorization(OperatorPolicies.Operator);

        // A second person, an admin, approves the revision they reviewed; the control
        // plane then signs it with its own key.
        app.MapPost("/api/files/packages/builder/{packageId}/approve", async (string packageId,
            PackageApproveRequest request, HttpContext context, PackageBuilder builder,
            CancellationToken cancellationToken) =>
        {
            var approver = context.User.Identity?.Name is { Length: > 0 } name ? name : "admin";
            var result = await builder.ApproveAsync(packageId, request, approver, cancellationToken);
            return result.Saved ? Results.Ok(result) : Results.BadRequest(new { message = result.Error });
        }).RequireAuthorization(OperatorPolicies.Admin);

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
