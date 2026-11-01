using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace ControlPlane.Api.Security;

/// <summary>
/// Helpdesk roles for the operator dashboard. Members handle conversations and will
/// draft skills, reviewers also triage requests and will publish skills, and admins
/// own devices, raw commands, maintenance and accounts.
/// </summary>
public static class OperatorRoles
{
    public const string Member = "member";
    public const string Reviewer = "reviewer";
    public const string Admin = "admin";

    public static readonly string[] All = [Member, Reviewer, Admin];

    public static bool IsValid(string? role) => role is Member or Reviewer or Admin;

    /// <summary>What each role may do, sent to the dashboard so it can hide controls.
    /// The API enforces the same rules through <see cref="OperatorPolicies"/>.</summary>
    public static string[] PermissionsFor(string role) => role switch
    {
        Admin => [.. ReviewerPermissions, "commands.run", "maintenance.set", "devices.manage",
            "users.manage", "environment.manage"],
        Reviewer => ReviewerPermissions,
        Member => MemberPermissions,
        _ => []
    };

    private static readonly string[] MemberPermissions =
        ["dashboard.view", "helpdesk.reply", "helpdesk.diagnose", "skills.draft"];

    private static readonly string[] ReviewerPermissions =
        [.. MemberPermissions, "feature-requests.triage", "skills.publish", "actions.undo"];
}

public static class OperatorPolicies
{
    public const string Operator = "operator";
    public const string Reviewer = "operator-reviewer";
    public const string Admin = "operator-admin";

    /// <summary>Signed in, but still allowed while a temporary password must be changed.</summary>
    public const string SignedIn = "operator-signed-in";

    /// <summary>The agent hub: reads the device token but admits every connection; the hub
    /// methods check the endpoint identity themselves.</summary>
    public const string AgentConnection = "agent-connection";

    public static void Add(AuthorizationOptions options)
    {
        options.AddPolicy(SignedIn, Build());
        options.AddPolicy(Operator, Build(ready: true));
        options.AddPolicy(Reviewer, Build(ready: true, OperatorRoles.Reviewer, OperatorRoles.Admin));
        options.AddPolicy(Admin, Build(ready: true, OperatorRoles.Admin));
        // AllowAnonymous would still run the fallback policy's cookie scheme and replace the
        // device-token identity with an empty one, so the hub names its own scheme instead.
        options.AddPolicy(AgentConnection, new AuthorizationPolicyBuilder(AgentAuthentication.Scheme)
            .RequireAssertion(_ => true).Build());
        // Any route that does not say otherwise needs a signed-in operator, so a new
        // operator API can't ship unauthenticated by accident.
        options.FallbackPolicy = options.GetPolicy(Operator);
    }

    private static AuthorizationPolicy Build(bool ready = false, params string[] roles)
    {
        var builder = new AuthorizationPolicyBuilder(OperatorAuthentication.Scheme)
            .RequireAuthenticatedUser();
        if (ready)
            builder.RequireAssertion(context =>
                !context.User.HasClaim(OperatorAuthentication.MustChangePasswordClaim, "true"));
        if (roles.Length > 0) builder.RequireRole(roles);
        return builder.Build();
    }
}

public static class OperatorAuthentication
{
    public const string Scheme = "OperatorCookie";
    public const string CookieName = "ea_operator";
    public const string StampClaim = "operator_stamp";
    public const string MustChangePasswordClaim = "operator_must_change_password";

    public static string? UserId(ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier);
}
