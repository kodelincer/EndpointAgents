using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Collections.Concurrent;
using System.Security.Claims;

namespace ControlPlane.Api.Security;

public sealed record OperatorSignInRequest(string? UserName, string? Password);

public sealed record OperatorPasswordChangeRequest(string? CurrentPassword, string? NewPassword);

public sealed record OperatorUserCreateRequest(string? UserName, string? DisplayName, string? Role,
    string? Source = null);

public sealed record OperatorUserPatch(string? DisplayName, string? Role, bool? Disabled,
    bool? ResetPassword);

public sealed record OperatorSession(string UserId, string UserName, string DisplayName,
    string Role, bool MustChangePassword, string[] Permissions, string Source = OperatorSources.Local)
{
    public static OperatorSession From(OperatorUser user) => new(user.UserId, user.UserName,
        user.DisplayName, user.Role, user.MustChangePassword,
        OperatorRoles.PermissionsFor(user.Role), user.Source);
}

/// <summary>Slows password guessing: five failures for one name from one address lock
/// that pair for fifteen minutes.</summary>
public sealed class OperatorSignInThrottle(TimeProvider clock)
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset First)> _failures = new();

    public bool IsLocked(string key) =>
        _failures.TryGetValue(key, out var entry)
        && entry.Count >= MaxFailures && clock.GetUtcNow() - entry.First < Window;

    public void RecordFailure(string key) => _failures.AddOrUpdate(key,
        _ => (1, clock.GetUtcNow()),
        (_, entry) => clock.GetUtcNow() - entry.First >= Window
            ? (1, clock.GetUtcNow())
            : (entry.Count + 1, entry.First));

    public void Reset(string key) => _failures.TryRemove(key, out _);
}

public static class OperatorAuthEndpoints
{
    public static AuthenticationBuilder AddOperatorCookie(this AuthenticationBuilder builder) =>
        builder.AddCookie(OperatorAuthentication.Scheme, options =>
        {
            options.Cookie.Name = OperatorAuthentication.CookieName;
            options.Cookie.HttpOnly = true;
            // The dashboard (port 3000) and API (port 5052) share a host name, which
            // makes them the same site, so Strict still sends the cookie to the API
            // while other sites can never ride on it.
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromHours(10);
            options.SlidingExpiration = true;
            options.Events = new CookieAuthenticationEvents
            {
                OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                },
                OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                },
                OnValidatePrincipal = ValidatePrincipalAsync
            };
        });

    /// <summary>Re-reads the account on every request so a disabled user, a role change
    /// or a password reset takes effect at once instead of when the cookie expires.</summary>
    private static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var userId = context.Principal is null ? null : OperatorAuthentication.UserId(context.Principal);
        var stamp = context.Principal?.FindFirstValue(OperatorAuthentication.StampClaim);
        var store = context.HttpContext.RequestServices.GetRequiredService<OperatorUserStore>();
        var user = userId is null ? null
            : await store.FindByIdAsync(userId, context.HttpContext.RequestAborted);
        if (user is null || user.Disabled || user.SecurityStamp != stamp)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(OperatorAuthentication.Scheme);
            return;
        }

        var current = CreatePrincipal(user);
        if (current.FindFirstValue(ClaimTypes.Role) != context.Principal!.FindFirstValue(ClaimTypes.Role)
            || current.HasClaim(OperatorAuthentication.MustChangePasswordClaim, "true")
                != context.Principal.HasClaim(OperatorAuthentication.MustChangePasswordClaim, "true"))
        {
            context.ReplacePrincipal(current);
            context.ShouldRenew = true;
        }
    }

    private static ClaimsPrincipal CreatePrincipal(OperatorUser user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.UserId),
            new(ClaimTypes.Name, user.UserName),
            new(ClaimTypes.Role, user.Role),
            new(OperatorAuthentication.StampClaim, user.SecurityStamp)
        };
        if (user.MustChangePassword)
            claims.Add(new Claim(OperatorAuthentication.MustChangePasswordClaim, "true"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, OperatorAuthentication.Scheme));
    }

    private static Task SignInAsync(HttpContext context, OperatorUser user) =>
        context.SignInAsync(OperatorAuthentication.Scheme, CreatePrincipal(user));

    public static void MapOperatorAuth(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth");

        auth.MapGet("/options", (IOperatorDirectory directory) =>
            Results.Ok(new { directory = directory.Configured, domain = directory.Domain }))
            .AllowAnonymous();

        auth.MapPost("/sign-in", async (OperatorSignInRequest request, HttpContext context,
            OperatorUserStore store, OperatorSignInThrottle throttle, IOperatorDirectory directory,
            ILogger<OperatorUserStore> logger, CancellationToken cancellationToken) =>
        {
            var typed = request.UserName ?? string.Empty;
            // "DOMAIN\\sara" and "sara@corp.example" are the same person as "sara".
            var userName = OperatorUserStore.NormalizeUserName(
                directory.Configured ? OperatorDirectory.AccountName(typed) : typed);
            var key = $"{userName}|{context.Connection.RemoteIpAddress}";
            if (throttle.IsLocked(key))
                return Results.Json(new { message = "Too many failed attempts. Try again later." },
                    statusCode: StatusCodes.Status429TooManyRequests);

            var user = userName.Length == 0 ? null
                : await store.FindByUserNameAsync(userName, cancellationToken);
            var password = request.Password ?? string.Empty;

            // A local account always signs in with its local password; a name that has no
            // local account, or belongs to a directory account, is checked by the directory.
            if (user is null || user.Source == OperatorSources.Directory)
            {
                if (directory.Configured && userName.Length > 0)
                {
                    var outcome = await DirectorySignInAsync(userName, password, user, store,
                        directory, logger, cancellationToken);
                    if (outcome.Unavailable)
                        return Results.Json(new { message = "The directory server could not be reached." },
                            statusCode: StatusCodes.Status503ServiceUnavailable);
                    user = outcome.User;
                }
                else
                {
                    // Hash even for unknown names so timing doesn't reveal which names exist.
                    OperatorPasswordHasher.Verify(password, DummyHash.Value);
                    user = null;
                }
            }
            else if (!OperatorPasswordHasher.Verify(password, user.PasswordHash))
            {
                user = null;
            }

            if (user is null || user.Disabled)
            {
                throttle.RecordFailure(key);
                logger.LogWarning("Dashboard sign-in failed for '{UserName}'.", userName);
                return Results.Json(new { message = "User name or password is incorrect." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            throttle.Reset(key);
            await store.RecordSignInAsync(user.UserId, cancellationToken);
            await SignInAsync(context, user);
            logger.LogInformation("Dashboard sign-in for '{UserName}' ({Role}, {Source}).",
                user.UserName, user.Role, user.Source);
            return Results.Ok(OperatorSession.From(user));
        }).AllowAnonymous();

        auth.MapPost("/sign-out", async (HttpContext context) =>
        {
            await context.SignOutAsync(OperatorAuthentication.Scheme);
            return Results.NoContent();
        }).AllowAnonymous();

        auth.MapGet("/session", async (ClaimsPrincipal principal, OperatorUserStore store,
            CancellationToken cancellationToken) =>
        {
            var user = await store.FindByIdAsync(OperatorAuthentication.UserId(principal)!,
                cancellationToken);
            return user is null ? Results.Unauthorized() : Results.Ok(OperatorSession.From(user));
        }).RequireAuthorization(OperatorPolicies.SignedIn);

        auth.MapPost("/password", async (OperatorPasswordChangeRequest request,
            ClaimsPrincipal principal, HttpContext context, OperatorUserStore store,
            CancellationToken cancellationToken) =>
        {
            var user = await store.FindByIdAsync(OperatorAuthentication.UserId(principal)!,
                cancellationToken);
            if (user is null) return Results.Unauthorized();
            if (user.Source == OperatorSources.Directory)
                return Results.BadRequest(new { message = "Your password is managed by Active Directory." });
            if (!OperatorPasswordHasher.Verify(request.CurrentPassword ?? string.Empty,
                    user.PasswordHash))
                return Results.BadRequest(new { message = "Current password is incorrect." });
            if (OperatorPasswordHasher.Validate(request.NewPassword) is { } problem)
                return Results.BadRequest(new { message = problem });
            if (request.NewPassword == request.CurrentPassword)
                return Results.BadRequest(new { message = "Choose a password you have not used here." });

            var updated = await store.SetPasswordAsync(user.UserId, request.NewPassword!,
                mustChangePassword: false, cancellationToken);
            await SignInAsync(context, updated!);
            return Results.Ok(OperatorSession.From(updated!));
        }).RequireAuthorization(OperatorPolicies.SignedIn);

        var users = app.MapGroup("/api/operators").RequireAuthorization(OperatorPolicies.Admin);

        users.MapGet("/", async (OperatorUserStore store, CancellationToken cancellationToken) =>
            Results.Ok(await store.ListAsync(cancellationToken)));

        users.MapPost("/", async (OperatorUserCreateRequest request, OperatorUserStore store,
            IOperatorDirectory directory, CancellationToken cancellationToken) =>
        {
            if (request.Source == OperatorSources.Directory)
            {
                if (!directory.Configured)
                    return Results.BadRequest(new { message = "Active Directory sign-in is not set up on this server." });
                if (!OperatorRoles.IsValid(request.Role))
                    return Results.BadRequest(new { message = "Unknown role." });
                var account = OperatorDirectory.AccountName(request.UserName);
                if (OperatorUserStore.ValidateUserName(account) is { } accountProblem)
                    return Results.BadRequest(new { message = accountProblem });
                if (await store.FindByUserNameAsync(account, cancellationToken) is not null)
                    return Results.Conflict(new { message = "That user name is already taken." });
                var person = await directory.FindAsync(account, cancellationToken);
                if (person is null)
                    return Results.BadRequest(new { message = "That user was not found in Active Directory." });
                var added = await store.CreateAsync(person.UserName, person.DisplayName, request.Role!,
                    string.Empty, mustChangePassword: false, cancellationToken, OperatorSources.Directory);
                return Results.Ok(new { user = added, temporaryPassword = (string?)null });
            }

            var userName = OperatorUserStore.NormalizeUserName(request.UserName);
            if (OperatorUserStore.ValidateUserName(userName) is { } problem)
                return Results.BadRequest(new { message = problem });
            var displayName = (request.DisplayName ?? string.Empty).Trim();
            if (displayName.Length is < 1 or > 60)
                return Results.BadRequest(new { message = "Display name must be 1-60 characters." });
            if (!OperatorRoles.IsValid(request.Role))
                return Results.BadRequest(new { message = "Unknown role." });
            if (await store.FindByUserNameAsync(userName, cancellationToken) is not null)
                return Results.Conflict(new { message = "That user name is already taken." });

            var password = OperatorPasswordHasher.GenerateTemporary();
            var user = await store.CreateAsync(userName, displayName, request.Role!, password,
                mustChangePassword: true, cancellationToken);
            return Results.Ok(new { user, temporaryPassword = password });
        });

        users.MapMethods("/{userId}", ["PATCH"], async (string userId, OperatorUserPatch patch,
            ClaimsPrincipal principal, OperatorUserStore store,
            CancellationToken cancellationToken) =>
        {
            var user = await store.FindByIdAsync(userId, cancellationToken);
            if (user is null) return Results.NotFound();
            var role = patch.Role ?? user.Role;
            var disabled = patch.Disabled ?? user.Disabled;
            var displayName = (patch.DisplayName ?? user.DisplayName).Trim();
            if (!OperatorRoles.IsValid(role))
                return Results.BadRequest(new { message = "Unknown role." });
            if (displayName.Length is < 1 or > 60)
                return Results.BadRequest(new { message = "Display name must be 1-60 characters." });
            if (patch.ResetPassword == true && user.Source == OperatorSources.Directory)
                return Results.BadRequest(new { message = "Passwords of Active Directory users are managed in Active Directory." });
            var isSelf = userId == OperatorAuthentication.UserId(principal);
            var losesAdmin = user.Role == OperatorRoles.Admin && !user.Disabled
                && (role != OperatorRoles.Admin || disabled);
            if (isSelf && losesAdmin)
                return Results.BadRequest(new { message = "You can't remove your own admin access." });
            if (losesAdmin && await store.CountEnabledAdminsAsync(cancellationToken) <= 1)
                return Results.BadRequest(new { message = "Keep at least one enabled admin." });

            await store.UpdateAsync(userId, displayName, role, disabled,
                rotateStamp: role != user.Role || disabled != user.Disabled, cancellationToken);
            string? temporaryPassword = null;
            if (patch.ResetPassword == true)
            {
                temporaryPassword = OperatorPasswordHasher.GenerateTemporary();
                await store.SetPasswordAsync(userId, temporaryPassword, mustChangePassword: true,
                    cancellationToken);
            }
            return Results.Ok(new
            {
                user = await store.FindByIdAsync(userId, cancellationToken),
                temporaryPassword
            });
        });
    }

    private sealed record DirectoryOutcome(OperatorUser? User, bool Unavailable = false);

    /// <summary>Checks the password with the directory. An existing directory account keeps the
    /// role an admin gave it; a new person is created only when a mapped group lists them.</summary>
    private static async Task<DirectoryOutcome> DirectorySignInAsync(string userName, string password,
        OperatorUser? existing, OperatorUserStore store, IOperatorDirectory directory,
        ILogger logger, CancellationToken cancellationToken)
    {
        var result = await directory.SignInAsync(userName, password, cancellationToken);
        if (result.Status == DirectorySignInStatus.Unavailable) return new DirectoryOutcome(null, true);
        if (result.Status != DirectorySignInStatus.Accepted || result.Person is null)
            return new DirectoryOutcome(null);
        if (existing is not null) return new DirectoryOutcome(existing);

        var person = result.Person;
        if (person.Role is null || OperatorUserStore.ValidateUserName(person.UserName) is not null)
        {
            logger.LogWarning("Directory user '{UserName}' signed in correctly but is not in a "
                + "group mapped to an operator role, so no dashboard account was created.", person.UserName);
            return new DirectoryOutcome(null);
        }

        var created = await store.CreateAsync(person.UserName, person.DisplayName, person.Role,
            string.Empty, mustChangePassword: false, cancellationToken, OperatorSources.Directory);
        logger.LogInformation("Created dashboard account '{UserName}' ({Role}) from the directory.",
            created.UserName, created.Role);
        return new DirectoryOutcome(created);
    }

    private static readonly Lazy<string> DummyHash =
        new(() => OperatorPasswordHasher.Hash(Guid.NewGuid().ToString("N")));
}
