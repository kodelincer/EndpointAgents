using Microsoft.Extensions.Options;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Security.Principal;

namespace ControlPlane.Api.Actions;

/// <summary>
/// Read-only Active Directory lookup of the signed-in user's own account. Off until an
/// administrator sets <c>ActiveDirectory:Enabled</c>; by default it binds as the control-plane
/// service's own identity, which only needs ordinary read rights. It never unlocks, enables or
/// resets anything; those stay with the helpdesk.
/// </summary>
public sealed class ActiveDirectoryOptions
{
    public bool Enabled { get; set; }
    /// <summary>Domain or domain-controller name, e.g. "corp.example".</summary>
    public string Server { get; set; } = "";
    /// <summary>Search base, e.g. "DC=corp,DC=example".</summary>
    public string BaseDn { get; set; } = "";
    /// <summary>Optional read-only account; leave empty to use the service identity.</summary>
    public string BindUser { get; set; } = "";
    public string BindPassword { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 10;
}

public sealed record AccountStatus(bool Found, bool? Enabled, bool? LockedOut,
    bool? PasswordExpired, bool? MustChangePassword, int? PasswordAgeDays,
    bool? PasswordNeverExpires, DateTimeOffset? AccountExpiresUtc, bool? AccountExpired,
    DateTimeOffset? LastLogonUtc);

public sealed class ActiveDirectoryAccountChecker(IOptions<ActiveDirectoryOptions> options,
    ILogger<ActiveDirectoryAccountChecker> logger)
{
    private const int AccountDisabled = 0x2;
    private const int DontExpirePassword = 0x10000;
    private const int ComputedLockout = 0x10;
    private const int ComputedPasswordExpired = 0x800000;

    public bool Configured => options.Value is { Enabled: true } value
        && !string.IsNullOrWhiteSpace(value.Server) && !string.IsNullOrWhiteSpace(value.BaseDn);

    public async Task<AccountStatus?> GetStatusAsync(string userSid,
        CancellationToken cancellationToken)
    {
        if (!Configured) return null;
        SecurityIdentifier sid;
        try { sid = new SecurityIdentifier(userSid); }
        catch (ArgumentException) { return new AccountStatus(false, null, null, null, null, null, null, null, null, null); }

        return await Task.Run(() => Lookup(sid), cancellationToken);
    }

    private AccountStatus? Lookup(SecurityIdentifier sid)
    {
        var settings = options.Value;
        try
        {
            using var connection = new LdapConnection(new LdapDirectoryIdentifier(settings.Server));
            connection.Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 2, 60));
            connection.SessionOptions.ProtocolVersion = 3;
            connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
            connection.AuthType = AuthType.Negotiate;
            if (!string.IsNullOrWhiteSpace(settings.BindUser))
                connection.Credential = new NetworkCredential(settings.BindUser, settings.BindPassword);
            connection.Bind();

            var bytes = new byte[sid.BinaryLength];
            sid.GetBinaryForm(bytes, 0);
            var filter = "(&(objectCategory=person)(objectClass=user)(objectSid="
                + string.Concat(bytes.Select(item => $"\\{item:x2}")) + "))";
            var search = (SearchResponse)connection.SendRequest(new SearchRequest(settings.BaseDn,
                filter, SearchScope.Subtree, "distinguishedName", "userAccountControl",
                "pwdLastSet", "accountExpires", "lastLogonTimestamp"));
            if (search.Entries.Count == 0)
                return new AccountStatus(false, null, null, null, null, null, null, null, null, null);
            var entry = search.Entries[0];

            // msDS-User-Account-Control-Computed is only returned by a base-scope read.
            var computed = (SearchResponse)connection.SendRequest(new SearchRequest(
                entry.DistinguishedName, "(objectClass=*)", SearchScope.Base,
                "msDS-User-Account-Control-Computed"));
            var control = Int(entry, "userAccountControl") ?? 0;
            var computedFlags = computed.Entries.Count > 0
                ? Int(computed.Entries[0], "msDS-User-Account-Control-Computed") ?? 0 : 0;
            var pwdLastSet = Long(entry, "pwdLastSet");
            var accountExpires = FileTime(Long(entry, "accountExpires"));
            var now = DateTimeOffset.UtcNow;
            return new AccountStatus(
                true,
                (control & AccountDisabled) == 0,
                (computedFlags & ComputedLockout) != 0,
                (computedFlags & ComputedPasswordExpired) != 0,
                pwdLastSet == 0,
                FileTime(pwdLastSet) is { } set ? (int)(now - set).TotalDays : null,
                (control & DontExpirePassword) != 0,
                accountExpires,
                accountExpires is { } expires && expires < now,
                FileTime(Long(entry, "lastLogonTimestamp")));
        }
        catch (Exception exception) when (exception is LdapException or DirectoryOperationException
            or InvalidOperationException or PlatformNotSupportedException or DllNotFoundException)
        {
            logger.LogWarning("Active Directory account lookup failed: {Message}", exception.Message);
            return null;
        }
    }

    private static int? Int(SearchResultEntry entry, string name) =>
        Long(entry, name) is { } value ? (int)value : null;

    private static long? Long(SearchResultEntry entry, string name) =>
        entry.Attributes.Contains(name) && entry.Attributes[name].Count > 0
            && long.TryParse(entry.Attributes[name][0]?.ToString(), out var value) ? value : null;

    private static DateTimeOffset? FileTime(long? value) =>
        value is > 0 and < 0x7FFFFFFFFFFFFFFF
            ? DateTimeOffset.FromFileTime(value.Value).ToUniversalTime() : null;
}
