using ControlPlane.Api.Hermes;

namespace ControlPlane.Api.Tests;

public sealed class PromptRedactorTests
{
    [Fact]
    public void RedactsIdentityCredentialsBearerTokensAndIpv4Addresses()
    {
        var input =
            """
            Machine WS-123 user DOMAIN\alice SID S-1-5-21-1-2-3-1001
            password=hunter2 api_key:abc123 Bearer ey.secret.token
            server 192.0.2.25 fe80::1 mac AA-BB-CC-DD-EE-FF
            """;

        var result = PromptRedactor.Redact(input, "WS-123", @"DOMAIN\alice",
            "S-1-5-21-1-2-3-1001");

        Assert.DoesNotContain("WS-123", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alice", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hunter2", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abc123", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ey.secret.token", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("192.0.2.25", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fe80::1", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AA-BB-CC-DD-EE-FF", result,
            StringComparison.OrdinalIgnoreCase);
    }
}
