using EndpointAgents.Platform.Windows.Ibsng;
using EndpointAgents.Shared.Ibsng;
using EndpointAgents.Tray;
using System.Text.Json;

namespace EndpointAgents.Platform.Windows.Tests;

public sealed class IbsngConnectionLogParserTests
{
    [Fact]
    public void ScreenshotSequenceUsesMostRecentConnectionEvent()
    {
        string[] lines =
        [
            "Connected.",
            "Ready ...",
            "Disconnected, connection duration: 01:06:31",
            "Connecting to 192.168.10.4...",
            "Authenticating...",
            "Connected."
        ];

        Assert.Equal(IbsngConnectionState.Connected,
            IbsngConnectionLogParser.ParseState(lines));
    }

    [Theory]
    [InlineData("Disconnected, connection duration: 00:00:03", IbsngConnectionState.Disconnected)]
    [InlineData("Authenticating...", IbsngConnectionState.Authenticating)]
    [InlineData("Connecting to vpn.example.com...", IbsngConnectionState.Connecting)]
    [InlineData("Authentication failed: invalid credentials", IbsngConnectionState.Failed)]
    public void RecognizesConnectionEvents(string line, IbsngConnectionState expected)
    {
        Assert.Equal(expected, IbsngConnectionLogParser.ParseState([line]));
    }

    [Fact]
    public void SanitizerRedactsCredentialLikeValuesAndBoundsLines()
    {
        var text = string.Join('\n', Enumerable.Range(0, 30)
            .Select(index => index == 29 ? "password=super-secret" : $"Connected. {index}"));

        var lines = IbsngConnectionLogParser.Sanitize(text);

        Assert.Equal(20, lines.Count);
        Assert.DoesNotContain(lines, line => line.Contains("super-secret", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("[redacted]", StringComparison.Ordinal));
    }

    [Fact]
    public void PublicRequestAcceptsOnlyHostAndAllowListedProtocol()
    {
        var parameters = JsonSerializer.SerializeToElement(new
        {
            serverHost = "vpn.example.com",
            protocol = "L2TP",
            includeDialerLog = true
        });

        var valid = IbsngDiagnoseCommandHandler.TryParse(parameters, null,
            out var request, out var error);

        Assert.True(valid, error);
        Assert.Equal("vpn.example.com", request!.ServerHost);
        Assert.Equal(IbsngVpnProtocol.L2tp, request.Protocol);
    }

    [Theory]
    [InlineData("https://vpn.example.com/path")]
    [InlineData("user@vpn.example.com")]
    [InlineData("")]
    public void PublicRequestRejectsNonHostTargets(string host)
    {
        var parameters = JsonSerializer.SerializeToElement(new { serverHost = host });

        Assert.False(IbsngDiagnoseCommandHandler.TryParse(parameters, null,
            out _, out _));
    }

    [Fact]
    public void RasConnectionIsAuthoritativeWhenDialerLogIsUnavailable()
    {
        var ui = new IbsngDialerUiSnapshot(IbsngUiReadState.LogControlNotAccessible,
            42, true, IbsngConnectionState.Unknown, [], "Owner-drawn log");
        IbsngRasConnectionSnapshot[] ras =
        [
            new("IBSng", "vpn", "WAN Miniport (L2TP)", "Connected", 0)
        ];

        var result = IbsngConnectionInspector.Resolve(IbsngVpnProtocol.L2tp, ui, ras);

        Assert.Equal(IbsngConnectionState.Connected, result.State);
    }

    [Fact]
    public void UiConnectedWithoutMatchingRasIsInconsistent()
    {
        var ui = new IbsngDialerUiSnapshot(IbsngUiReadState.Success, 42, true,
            IbsngConnectionState.Connected, ["Connected."]);

        var result = IbsngConnectionInspector.Resolve(IbsngVpnProtocol.L2tp, ui, []);

        Assert.Equal(IbsngConnectionState.Inconsistent, result.State);
    }

    [Fact]
    public void BlankCommandHostFallsBackToConfiguredServer()
    {
        var parameters = JsonSerializer.SerializeToElement(new
        {
            serverHost = "",
            protocol = "Auto"
        });

        Assert.True(IbsngDiagnoseCommandHandler.TryParse(parameters, "192.168.10.4",
            out var request, out var error), error);
        Assert.Equal("192.168.10.4", request!.ServerHost);
    }
}
