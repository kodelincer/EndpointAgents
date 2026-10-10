using EndpointAgents.Shared.Security;
using System.Text.RegularExpressions;

namespace ControlPlane.Api.Hermes;

public static partial class PromptRedactor
{
    public static string Redact(string value, params string?[] exactValues)
    {
        var redacted = value;
        foreach (var exact in exactValues.Where(item => !string.IsNullOrWhiteSpace(item))
                     .OrderByDescending(item => item!.Length))
            redacted = redacted.Replace(exact!, "[redacted]", StringComparison.OrdinalIgnoreCase);

        redacted = SensitiveTextRedactor.RedactCredentials(redacted);
        redacted = Ipv4Pattern().Replace(redacted, "[ip-redacted]");
        redacted = PotentialIpv6Pattern().Replace(redacted, static match =>
            global::System.Net.IPAddress.TryParse(match.Value, out var address)
            && address.AddressFamily
                == global::System.Net.Sockets.AddressFamily.InterNetworkV6
                ? "[ip-redacted]"
                : match.Value);
        redacted = MacAddressPattern().Replace(redacted, "[mac-redacted]");
        return redacted;
    }

    /// <summary>Redacts credentials, bearer tokens and MAC addresses but keeps IP addresses.</summary>
    public static string RedactSecrets(string value)
    {
        var redacted = CredentialPattern().Replace(value, "$1=[redacted]");
        redacted = BearerPattern().Replace(redacted, "Bearer [redacted]");
        return MacAddressPattern().Replace(redacted, "[mac-redacted]");
    }

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|token|secret|api[_-]?key|user(?:name)?)\s*[:=]\s*[^\s,;]+")]
    private static partial Regex CredentialPattern();

    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9._~+/\-=]+")]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b")]
    private static partial Regex Ipv4Pattern();

    [GeneratedRegex(@"(?i)(?<![0-9a-f:])(?=[0-9a-f:]*:)[0-9a-f:]{2,}(?![0-9a-f:])")]
    private static partial Regex PotentialIpv6Pattern();

    [GeneratedRegex(@"(?i)\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b")]
    private static partial Regex MacAddressPattern();
}
