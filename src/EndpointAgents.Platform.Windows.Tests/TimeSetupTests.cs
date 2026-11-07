using EndpointAgents.Platform.Windows.Actions.SystemSide;

namespace EndpointAgents.Platform.Windows.Tests;

public sealed class TimeSetupTests
{
    [Fact]
    public void ServersAreWrittenAsClientModePeers() =>
        Assert.Equal("172.16.0.2,0x9 time.windows.com,0x9",
            TimeSetup.NtpServerValue(["172.16.0.2", "time.windows.com"]));

    [Theory]
    [InlineData("NTP", "time.corp.local,0x9", "time.corp.local", true)]
    [InlineData("NTP", "TIME.corp.local,0x9", "time.corp.local", true)]
    [InlineData("NTP", "other.local,0x9 time.corp.local,0x9", "time.corp.local", true)]
    [InlineData("NT5DS", "time.corp.local,0x9", "time.corp.local", false)]
    [InlineData("NTP", "time.windows.com,0x9", "time.corp.local", false)]
    [InlineData("NTP", null, "time.corp.local", false)]
    [InlineData(null, null, "time.corp.local", false)]
    public void RegistryMatchOnlyWhenTheServerIsTheManualSource(
        string? type, string? ntpServer, string host, bool expected) =>
        Assert.Equal(expected, TimeSetup.Matches(type, ntpServer, [host]));

    [Fact]
    public void EveryConfiguredServerMustBeInTheRegistry()
    {
        Assert.True(TimeSetup.Matches("NTP", "a.local,0x9 b.local,0x9", ["a.local", "b.local"]));
        Assert.False(TimeSetup.Matches("NTP", "a.local,0x9", ["a.local", "b.local"]));
        Assert.False(TimeSetup.Matches("NTP", "a.local,0x9", []));
    }
}
