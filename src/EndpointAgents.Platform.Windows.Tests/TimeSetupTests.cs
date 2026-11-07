using EndpointAgents.Platform.Windows.Actions.SystemSide;

namespace EndpointAgents.Platform.Windows.Tests;

public sealed class TimeSetupTests
{
    [Fact]
    public void ServerIsWrittenAsOneClientModePeer() =>
        Assert.Equal("time.corp.local,0x9", TimeSetup.NtpServerValue("time.corp.local"));

    [Theory]
    [InlineData("NTP", "time.corp.local,0x9", "time.corp.local", true)]
    [InlineData("NTP", "TIME.corp.local,0x9", "time.corp.local", true)]
    [InlineData("NTP", "other.local,0x9 time.corp.local,0x9", "time.corp.local", true)]
    [InlineData("NT5DS", "time.corp.local,0x9", "time.corp.local", false)]
    [InlineData("NTP", "time.windows.com,0x9", "time.corp.local", false)]
    [InlineData("NTP", null, "time.corp.local", false)]
    [InlineData(null, null, "time.corp.local", false)]
    public void RegistryMatchOnlyWhenTheOrganisationServerIsTheManualSource(
        string? type, string? ntpServer, string host, bool expected) =>
        Assert.Equal(expected, TimeSetup.Matches(type, ntpServer, host));
}
