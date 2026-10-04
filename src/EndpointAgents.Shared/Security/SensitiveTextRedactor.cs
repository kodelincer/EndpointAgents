using System.Text.RegularExpressions;

namespace EndpointAgents.Shared.Security;

/// <summary>
/// Masks values that look like credentials, bearer tokens, product keys, or long
/// opaque tokens. Used for any free text that leaves the process that produced it.
/// </summary>
public static partial class SensitiveTextRedactor
{
    public static string Redact(string value)
    {
        var redacted = CredentialPattern().Replace(value, "$1=[redacted]");
        redacted = BearerPattern().Replace(redacted, "Bearer [redacted]");
        redacted = ProductKeyPattern().Replace(redacted, "[key-redacted]");
        return OpaqueTokenPattern().Replace(redacted, "[token-redacted]");
    }

    /// <summary>Credential assignments and bearer tokens only.</summary>
    public static string RedactCredentials(string value) =>
        BearerPattern().Replace(
            CredentialPattern().Replace(value, "$1=[redacted]"), "Bearer [redacted]");

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|token|secret|api[_-]?key|user(?:name)?)\s*[:=]\s*[^\s,;]+")]
    private static partial Regex CredentialPattern();

    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9._~+/\-=]+")]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"(?i)\b(?:[A-Z0-9]{5}-){4}[A-Z0-9]{5}\b")]
    private static partial Regex ProductKeyPattern();

    [GeneratedRegex(@"\b[A-Za-z0-9+/_\-]{32,}={0,2}")]
    private static partial Regex OpaqueTokenPattern();
}
