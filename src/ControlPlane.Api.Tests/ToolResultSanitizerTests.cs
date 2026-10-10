using ControlPlane.Api.Tools;
using System.Text.Json;

namespace ControlPlane.Api.Tests;

public sealed class ToolResultSanitizerTests
{
    [Fact]
    public void Sanitize_NeverForwardsBatOutputTailToTheModel()
    {
        var result = ToolResultSanitizer.Sanitize(new
        {
            data = new { State = "PromptTimedOut", OutputTail = "Main menu choice:" }
        });

        using var document = JsonDocument.Parse(result);
        Assert.Equal("[redacted]", document.RootElement.GetProperty("data")
            .GetProperty("OutputTail").GetString());
    }

    [Fact]
    public void Sanitize_RedactsStringsInsideArrays()
    {
        var result = ToolResultSanitizer.Sanitize(new
        {
            values = new[] { "Office server 192.168.1.10", "safe" }
        });

        using var document = JsonDocument.Parse(result);
        var values = document.RootElement.GetProperty("values");
        Assert.Equal("Office server [ip-redacted]", values[0].GetString());
        Assert.Equal("safe", values[1].GetString());
    }

    [Fact]
    public void Sanitize_NetworkMode_KeepsIpAddressesButStillRedactsIdentifiers()
    {
        var result = ToolResultSanitizer.Sanitize(new
        {
            machineName = "HQ-PC-01",
            gateway = "192.168.1.1",
            dns = new[] { "10.0.0.5", "fe80::1" },
            note = "password=hunter2 mac 00-11-22-33-44-55"
        }, keepIpAddresses: true);

        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        Assert.Equal("[redacted]", root.GetProperty("machineName").GetString());
        Assert.Equal("192.168.1.1", root.GetProperty("gateway").GetString());
        Assert.Equal("10.0.0.5", root.GetProperty("dns")[0].GetString());
        Assert.Equal("fe80::1", root.GetProperty("dns")[1].GetString());
        Assert.Equal("password=[redacted] mac [mac-redacted]",
            root.GetProperty("note").GetString());
    }
}
