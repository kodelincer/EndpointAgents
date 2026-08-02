using ControlPlane.Api.Tools;
using System.Text.Json;

namespace ControlPlane.Api.Tests;

public sealed class ToolResultSanitizerTests
{
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
}
