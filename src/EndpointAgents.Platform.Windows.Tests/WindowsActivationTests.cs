using EndpointAgents.Platform.Windows.Windows;
using EndpointAgents.Shared.Windows;
using EndpointAgents.Shared.Chat;
using EndpointAgents.Tray;
using System.Text.Json;

namespace EndpointAgents.Platform.Windows.Tests;

public sealed class WindowsActivationTests
{
    [Theory]
    [InlineData(0, WindowsActivationStates.Unlicensed)]
    [InlineData(1, WindowsActivationStates.Licensed)]
    [InlineData(2, WindowsActivationStates.Grace)]
    [InlineData(5, WindowsActivationStates.Notification)]
    [InlineData(99, WindowsActivationStates.Unknown)]
    public void MapsWindowsLicenseStatus(int status, string expected)
    {
        Assert.Equal(expected, WindowsActivationProbe.MapState(status));
    }

    [Fact]
    public void ParsesOnlySanitizedActivationFields()
    {
        const string json =
            """{"Name":"Windows","Description":"Windows, RETAIL channel","LicenseStatus":1,"LicenseStatusReason":0,"GracePeriodRemaining":0,"ProductKeyChannel":"Retail","PartialProductKey":"SECRET"}""";

        var report = WindowsActivationProbe.Parse(json,
            new WindowsMachineInfo("Windows 11 Pro", "Professional", "24H2",
                "26100.1", "X64"));
        var serialized = JsonSerializer.Serialize(report);

        Assert.True(report.ProbeSucceeded);
        Assert.Equal(WindowsActivationStates.Licensed, report.State);
        Assert.Equal("Retail", report.Channel);
        Assert.DoesNotContain("SECRET", serialized);
        Assert.DoesNotContain("PartialProductKey", serialized);
    }

    [Fact]
    public void CommandAcceptsOnlyFixedArtifactAndOptionalUserScope()
    {
        var valid = JsonSerializer.SerializeToElement(new
        {
            artifactId = WindowsRemediationPaths.ArtifactId,
            userSid = "S-1-5-21-111-222-333-1001"
        });
        var wrong = JsonSerializer.SerializeToElement(new { artifactId = "other" });

        Assert.True(WindowsActivationBugfixCommandHandler.TryParse(valid, out var sid));
        Assert.Equal("S-1-5-21-111-222-333-1001", sid);
        Assert.False(WindowsActivationBugfixCommandHandler.TryParse(wrong, out _));
    }

    [Theory]
    [InlineData("yes", true)]
    [InlineData("بله!", true)]
    [InlineData("no", false)]
    [InlineData("لغو", false)]
    public void ParsesOnlyExactChatActionReplies(string text, bool expected)
    {
        Assert.True(ChatActionReply.TryParse(text, out var approved));
        Assert.Equal(expected, approved);
        Assert.False(ChatActionReply.TryParse($"please {text} now", out _));
    }

    [Fact]
    public void HelloCommandAcceptsOnlyFixedArtifactAndValidServerApprovalMarker()
    {
        var approvalId = Guid.NewGuid().ToString("N");
        var valid = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["artifactId"] = WindowsHelloSamplePaths.ArtifactId,
            ["userSid"] = "S-1-5-21-111-222-333-1001",
            [ChatActionApproval.ParameterName] = approvalId,
            [ChatActionApproval.ArtifactFileNameParameterName] =
                WindowsHelloSamplePaths.FileName,
            [ChatActionApproval.ArtifactSha256ParameterName] = new string('A', 64)
        });
        var forged = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["artifactId"] = WindowsHelloSamplePaths.ArtifactId,
            [ChatActionApproval.ParameterName] = "not-valid"
        });

        Assert.True(WindowsHelloSampleCommandHandler.TryParse(valid, out var sid,
            out var chatApproved));
        Assert.Equal("S-1-5-21-111-222-333-1001", sid);
        Assert.True(chatApproved);
        Assert.True(ChatActionApproval.MatchesRequest(valid, approvalId));
        Assert.False(ChatActionApproval.MatchesRequest(valid,
            Guid.NewGuid().ToString("N")));
        Assert.True(ChatActionApproval.MatchesArtifact(valid,
            WindowsHelloSamplePaths.FileName, new string('a', 64)));
        Assert.False(ChatActionApproval.MatchesArtifact(valid,
            WindowsHelloSamplePaths.FileName, new string('B', 64)));
        Assert.False(WindowsHelloSampleCommandHandler.TryParse(forged, out _, out _));
    }

    [Fact]
    public void HelloLauncherUsesVisibleSystemCmdWithoutElevation()
    {
        var path = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.DesktopDirectory),
            WindowsHelloSamplePaths.FileName);
        var startInfo = WindowsHelloLauncher.CreateStartInfo(path);

        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            startInfo.FileName);
        Assert.Equal(["/d", "/s", "/c", path], startInfo.ArgumentList);
        Assert.False(startInfo.UseShellExecute);
        Assert.False(startInfo.CreateNoWindow);
        Assert.Empty(startInfo.Verb);
    }

    [Fact]
    public void RunnerUsesFixedSystemCmdWithoutArgumentsOrInput()
    {
        var path = Path.Combine(WindowsRemediationPaths.StagingRoot,
            WindowsRemediationPaths.ArtifactId, new string('A', 64),
            WindowsRemediationPaths.FileName);

        var startInfo = WindowsBugfixRunner.CreateStartInfo(path);

        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            startInfo.FileName);
        Assert.Equal(["/d", "/s", "/c", path], startInfo.ArgumentList);
        Assert.False(startInfo.RedirectStandardInput);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
    }
}
