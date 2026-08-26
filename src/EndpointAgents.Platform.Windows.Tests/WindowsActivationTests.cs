using EndpointAgents.Platform.Windows.Windows;
using EndpointAgents.Core.Commands;
using EndpointAgents.Shared.Rpc;
using EndpointAgents.Shared.Windows;
using EndpointAgents.Shared.Chat;
using EndpointAgents.Tray;
using System.Security.Cryptography;
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

    [Fact]
    public void CommandAcceptsValidControlPlaneAuthorizationAndArtifactPin()
    {
        var requestId = Guid.NewGuid().ToString("N");
        var valid = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["artifactId"] = WindowsRemediationPaths.ArtifactId,
            ["userSid"] = "S-1-5-21-111-222-333-1001",
            [ControlPlaneActionAuthorization.ParameterName] = requestId,
            [ChatActionApproval.ArtifactFileNameParameterName] =
                WindowsRemediationPaths.FileName,
            [ChatActionApproval.ArtifactSha256ParameterName] = new string('A', 64)
        });

        Assert.True(WindowsActivationBugfixCommandHandler.TryParse(valid, out var sid));
        Assert.Equal("S-1-5-21-111-222-333-1001", sid);
        Assert.True(ControlPlaneActionAuthorization.MatchesRequest(valid, requestId));
        Assert.False(ControlPlaneActionAuthorization.MatchesRequest(valid,
            Guid.NewGuid().ToString("N")));
        Assert.True(ChatActionApproval.MatchesArtifact(valid,
            WindowsRemediationPaths.FileName, new string('a', 64)));
    }

    [Fact]
    public async Task CommandRejectsForgedControlPlaneAuthorizationBeforeExecution()
    {
        var requestId = Guid.NewGuid().ToString("N");
        var parameters = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["artifactId"] = WindowsRemediationPaths.ArtifactId,
            ["userSid"] = "S-1-5-21-111-222-333-1001",
            [ControlPlaneActionAuthorization.ParameterName] =
                Guid.NewGuid().ToString("N"),
            [ChatActionApproval.ArtifactFileNameParameterName] =
                WindowsRemediationPaths.FileName,
            [ChatActionApproval.ArtifactSha256ParameterName] = new string('A', 64)
        });
        var handler = new WindowsActivationBugfixCommandHandler(
            null!, null!, null!, null!, null!, null!);

        var response = await handler.HandleAsync(RpcRequest.Create(requestId,
            handler.Method, parameters), new CommandExecutionContext(
            "agent-1", null, null, DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.Equal(400, response.Error?.Code);
        Assert.Contains("ControlPlane authorization", response.Error?.Message);
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

    [Fact]
    public async Task DevelopmentRunnerAcceptsOnlyExactHashPinnedStagingPath()
    {
        var root = Path.Combine(Path.GetTempPath(),
            $"EndpointAgents-Windows-dev-{Guid.NewGuid():N}");
        var bytes = "@echo off\r\nexit /b 0\r\n"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var path = Path.Combine(root, WindowsRemediationPaths.ArtifactId, hash,
            WindowsRemediationPaths.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        try
        {
            Assert.True(await WindowsBugfixRunner.IsApprovedAsync(path, hash, root,
                requireProtectedAcl: false, TestContext.Current.CancellationToken));
            Assert.False(await WindowsBugfixRunner.IsApprovedAsync(path,
                new string('A', 64), root, requireProtectedAcl: false,
                TestContext.Current.CancellationToken));
            Assert.False(await WindowsBugfixRunner.IsApprovedAsync(
                Path.Combine(root, "outside.bat"), hash, root,
                requireProtectedAcl: false, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
