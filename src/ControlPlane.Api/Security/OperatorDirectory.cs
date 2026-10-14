using ControlPlane.Api.Actions;
using Microsoft.Extensions.Options;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Text;

namespace ControlPlane.Api.Security;

/// <summary>
/// Optional Active Directory sign-in for dashboard operators, next to the local accounts an
/// administrator creates on the control-plane server. Off until <c>OperatorDirectory:Enabled</c>
/// is set (the server address and search base come from the <c>ActiveDirectory</c> section).
/// </summary>
public sealed class OperatorDirectoryOptions
{
    public bool Enabled { get; set; }
    /// <summary>NetBIOS domain name used for the sign-in bind, e.g. "SHIRAZMETRO".</summary>
    public string Domain { get; set; } = "";
    /// <summary>Members of this group (name or sAMAccountName, nested groups included) are
    /// created as admins at their first sign-in. Leave all three empty to allow only
    /// directory users an admin has added by name.</summary>
    public string AdminGroup { get; set; } = "";
    public string ReviewerGroup { get; set; } = "";
    public string MemberGroup { get; set; } = "";
}

public static class OperatorSources
{
    public const string Local = "local";
    public const string Directory = "directory";
}

/// <summary>A user as the directory describes them.</summary>
public sealed record DirectoryPerson(string UserName, string DisplayName, string? Role);

public enum DirectorySignInStatus { Accepted, Rejected, Unavailable }

public sealed record DirectorySignIn(DirectorySignInStatus Status, DirectoryPerson? Person = null);

public interface IOperatorDirectory
{
    bool Configured { get; }
    string Domain { get; }

    /// <summary>Checks the password with the directory itself; the control plane never
    /// stores or sees it again. <see cref="DirectoryPerson.Role"/> is the highest role of the
    /// mapped groups the user belongs to, or null.</summary>
    Task<DirectorySignIn> SignInAsync(string userName, string password,
        CancellationToken cancellationToken);

    /// <summary>Finds a user by name with the service identity, for adding by name.</summary>
    Task<DirectoryPerson?> FindAsync(string userName, CancellationToken cancellationToken);
}

public sealed class OperatorDirectory(IOptions<OperatorDirectoryOptions> sign,
    IOptions<ActiveDirectoryOptions> directory, ILogger<OperatorDirectory> logger)
    : IOperatorDirectory
{
    public bool Configured => sign.Value.Enabled
        && !string.IsNullOrWhiteSpace(directory.Value.Server)
        && !string.IsNullOrWhiteSpace(directory.Value.BaseDn);

    public string Domain => sign.Value.Domain;

    /// <summary>"DOMAIN\sara", "sara@corp.example" and "sara" all mean the account "sara".</summary>
    public static string AccountName(string? input)
    {
        var name = (input ?? string.Empty).Trim();
        var slash = name.LastIndexOf('\\');
        if (slash >= 0) name = name[(slash + 1)..];
        var at = name.IndexOf('@');
        if (at > 0) name = name[..at];
        return name.ToLowerInvariant();
    }

    /// <summary>Escapes a value for an LDAP search filter (RFC 4515).</summary>
    public static string EscapeFilter(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(character switch
            {
                '\\' => "\\5c", '*' => "\\2a", '(' => "\\28", ')' => "\\29", '\0' => "\\00",
                _ => character.ToString()
            });
        return builder.ToString();
    }

    public async Task<DirectorySignIn> SignInAsync(string userName, string password,
        CancellationToken cancellationToken)
    {
        var account = AccountName(userName);
        if (!Configured || account.Length == 0 || string.IsNullOrEmpty(password))
            return new DirectorySignIn(DirectorySignInStatus.Rejected);
        return await Task.Run(() => SignIn(account, password), cancellationToken);
    }

    public async Task<DirectoryPerson?> FindAsync(string userName, CancellationToken cancellationToken)
    {
        var account = AccountName(userName);
        if (!Configured || account.Length == 0) return null;
        return await Task.Run(() =>
        {
            try
            {
                using var connection = Connect();
                if (!string.IsNullOrWhiteSpace(directory.Value.BindUser))
                    connection.Credential = new NetworkCredential(directory.Value.BindUser,
                        directory.Value.BindPassword);
                connection.Bind();
                return Find(connection, account, readGroups: false)?.Person;
            }
            catch (Exception exception) when (IsDirectoryFailure(exception))
            {
                logger.LogWarning("Active Directory user lookup failed: {Message}", exception.Message);
                return null;
            }
        }, cancellationToken);
    }

    private DirectorySignIn SignIn(string account, string password)
    {
        try
        {
            using var connection = Connect();
            // Negotiate never sends the password in clear text; a wrong one fails the bind.
            connection.Credential = new NetworkCredential(account, password,
                string.IsNullOrWhiteSpace(sign.Value.Domain) ? null : sign.Value.Domain);
            try { connection.Bind(); }
            catch (LdapException exception) when (exception.ErrorCode == 49)
            {
                return new DirectorySignIn(DirectorySignInStatus.Rejected);
            }

            var found = Find(connection, account, readGroups: true);
            return found is null || found.Disabled
                ? new DirectorySignIn(DirectorySignInStatus.Rejected)
                : new DirectorySignIn(DirectorySignInStatus.Accepted, found.Person);
        }
        catch (Exception exception) when (IsDirectoryFailure(exception))
        {
            logger.LogWarning("Active Directory sign-in could not reach the directory: {Message}",
                exception.Message);
            return new DirectorySignIn(DirectorySignInStatus.Unavailable);
        }
    }

    private sealed record Found(DirectoryPerson Person, bool Disabled);

    private Found? Find(LdapConnection connection, string account, bool readGroups)
    {
        var search = (SearchResponse)connection.SendRequest(new SearchRequest(directory.Value.BaseDn,
            $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={EscapeFilter(account)}))",
            SearchScope.Subtree, "distinguishedName", "displayName", "sAMAccountName",
            "userAccountControl"));
        if (search.Entries.Count != 1) return null;
        var entry = search.Entries[0];
        var name = Text(entry, "sAMAccountName") ?? account;
        var display = Text(entry, "displayName") ?? name;
        var disabled = int.TryParse(Text(entry, "userAccountControl"), out var control)
            && (control & 0x2) != 0;
        var role = readGroups ? RoleFor(connection, entry.DistinguishedName) : null;
        return new Found(new DirectoryPerson(name.ToLowerInvariant(), display, role), disabled);
    }

    /// <summary>The highest role among the mapped groups the user is in (nested groups count).</summary>
    private string? RoleFor(LdapConnection connection, string userDn)
    {
        foreach (var (group, role) in new[]
                 {
                     (sign.Value.AdminGroup, OperatorRoles.Admin),
                     (sign.Value.ReviewerGroup, OperatorRoles.Reviewer),
                     (sign.Value.MemberGroup, OperatorRoles.Member)
                 })
        {
            if (string.IsNullOrWhiteSpace(group)) continue;
            var groups = (SearchResponse)connection.SendRequest(new SearchRequest(directory.Value.BaseDn,
                $"(&(objectClass=group)(|(sAMAccountName={EscapeFilter(group.Trim())})(cn={EscapeFilter(group.Trim())})))",
                SearchScope.Subtree, "distinguishedName"));
            if (groups.Entries.Count != 1) continue;
            // 1.2.840.113556.1.4.1941 is the nested-membership matching rule.
            var member = (SearchResponse)connection.SendRequest(new SearchRequest(userDn,
                $"(memberOf:1.2.840.113556.1.4.1941:={EscapeFilter(groups.Entries[0].DistinguishedName)})",
                SearchScope.Base, "distinguishedName"));
            if (member.Entries.Count > 0) return role;
        }
        return null;
    }

    private LdapConnection Connect()
    {
        var connection = new LdapConnection(new LdapDirectoryIdentifier(directory.Value.Server));
        connection.Timeout = TimeSpan.FromSeconds(Math.Clamp(directory.Value.TimeoutSeconds, 2, 60));
        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        connection.AuthType = AuthType.Negotiate;
        return connection;
    }

    private static string? Text(SearchResultEntry entry, string name) =>
        entry.Attributes.Contains(name) && entry.Attributes[name].Count > 0
            ? entry.Attributes[name][0]?.ToString() : null;

    private static bool IsDirectoryFailure(Exception exception) => exception is LdapException
        or DirectoryOperationException or InvalidOperationException
        or PlatformNotSupportedException or DllNotFoundException;
}
