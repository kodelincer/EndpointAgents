using EndpointAgents.Platform.Windows.Windows;
using EndpointAgents.Core.Commands;
using EndpointAgents.Shared.Rpc;
using EndpointAgents.Shared.Windows;
using EndpointAgents.Shared.Chat;
using EndpointAgents.Shared.Office;
using EndpointAgents.Tray;
using Microsoft.Extensions.Options;
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
    public void CommandAcceptsCatalogArtifactAndOptionalUserScope()
    {
        var valid = JsonSerializer.SerializeToElement(new
        {
            artifactId = WindowsRemediationPaths.ArtifactId,
            userSid = "S-1-5-21-111-222-333-1001"
        });
        var alternate = JsonSerializer.SerializeToElement(new
        {
            artifactId = "windows-11-repair"
        });
        var wrong = JsonSerializer.SerializeToElement(new
        {
            artifactId = "../other"
        });

        Assert.True(WindowsActivationBugfixCommandHandler.TryParse(valid, out var sid));
        Assert.Equal("S-1-5-21-111-222-333-1001", sid);
        Assert.True(WindowsActivationBugfixCommandHandler.TryParse(alternate,
            out _));
        Assert.False(WindowsActivationBugfixCommandHandler.TryParse(wrong, out _));
    }

    [Fact]
    public void WindowsRepairCompatibilityChecksBuildAndEdition()
    {
        var artifact = new WindowsBugfixArtifactDescriptor("windows-11-repair",
            "Windows 11 repair", "Test", "windows-11-repair.bat",
            new string('A', 64),
            "/api/files/windows/bugfix/windows-11-repair",
            "10.0.22000", "10.0.30000", ["Professional"]);
        var report = new WindowsActivationReport("Windows 11 Pro",
            "Professional", "24H2", "26100", "X64",
            WindowsActivationStates.Unlicensed, 0, null, null, null, true);

        Assert.True(WindowsBugfixCompatibility.IsCompatible(artifact, report,
            new Version(10, 0, 26100)));
        Assert.False(WindowsBugfixCompatibility.IsCompatible(artifact, report,
            new Version(10, 0, 19045)));
        Assert.False(WindowsBugfixCompatibility.IsCompatible(artifact,
            report with { EditionId = "Enterprise" },
            new Version(10, 0, 26100)));
        var legacy = artifact with
        {
            ArtifactId = WindowsRemediationPaths.ArtifactId,
            MinWindowsVersion = null,
            MaxWindowsVersionExclusive = null
        };
        Assert.Equal([artifact.ArtifactId],
            WindowsBugfixCompatibility.SelectCompatibleArtifactIds(
                [legacy, artifact], report, new Version(10, 0, 26100)));
        Assert.Empty(WindowsBugfixCompatibility.SelectCompatibleArtifactIds(
            [legacy, artifact], report, new Version(10, 0, 19045)));
        Assert.Equal([legacy.ArtifactId],
            WindowsBugfixCompatibility.SelectCompatibleArtifactIds(
                [legacy], report, new Version(10, 0, 19045)));
        var shared = artifact with
        {
            AdditionalCompatibility =
            [
                new WindowsBugfixCompatibilityRange("10.0.19041",
                    "10.0.19046", ["Enterprise"]),
                new WindowsBugfixCompatibilityRange("10.0.30000",
                    "10.0.31000", ["Professional"])
            ]
        };
        Assert.True(WindowsBugfixCompatibility.IsCompatible(shared,
            report with { EditionId = "Enterprise" },
            new Version(10, 0, 19045)));
        Assert.False(WindowsBugfixCompatibility.IsCompatible(shared,
            report with { EditionId = "Enterprise" },
            new Version(10, 0, 19046)));
        Assert.True(WindowsBugfixCompatibility.IsCompatible(shared, report,
            new Version(10, 0, 30000)));
        Assert.False(WindowsBugfixCompatibility.IsCompatible(shared,
            report with { EditionId = "Enterprise" },
            new Version(10, 0, 30000)));
    }

    [Fact]
    public async Task WindowsRunnerAnswersMenuAndVerifiesSupportingFile()
    {
        const string artifactId = "windows-menu-demo";
        var root = Path.Combine(Path.GetTempPath(),
            $"EndpointAgents-Windows-menu-{Guid.NewGuid():N}");
        var batch = """
            @echo off
            <nul set /p "=Select an item: "
            >nul ping -n 2 127.0.0.1
            set /p "first="
            if not "%first%"=="1" exit /b 11
            <nul set /p "=Select mode: "
            >nul ping -n 2 127.0.0.1
            set /p "second="
            if not "%second%"=="A" exit /b 12
            if not exist "%~dp0helper.txt" exit /b 13
            exit /b 0
            """.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\n", "\r\n", StringComparison.Ordinal);
        var batchBytes = System.Text.Encoding.ASCII.GetBytes(batch);
        var batchHash = Convert.ToHexString(SHA256.HashData(batchBytes));
        var helperBytes = "harmless helper"u8.ToArray();
        var helperHash = Convert.ToHexString(SHA256.HashData(helperBytes));
        var support = new BatchSupportFile("helper.txt", helperHash,
            $"/api/files/windows/bugfix/{artifactId}/file/helper.txt");
        var bundleHash = BatchSupportFilePolicy.ComputeBundleSha256(batchHash,
            [support]);
        var directory = Path.Combine(root, artifactId, bundleHash);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{artifactId}.bat");
        await File.WriteAllBytesAsync(path, batchBytes,
            TestContext.Current.CancellationToken);
        var helperPath = Path.Combine(directory, "helper.txt");
        await File.WriteAllBytesAsync(helperPath, helperBytes,
            TestContext.Current.CancellationToken);
        var playbook = new BatchPlaybook(1,
        [
            new BatchPlaybookStep("Select an item:", "1", "line", 5),
            new BatchPlaybookStep("Select mode:", "A", "line", 5)
        ]);
        var descriptor = new WindowsBugfixArtifactDescriptor(artifactId,
            "Menu demo", "Harmless test", $"{artifactId}.bat", batchHash,
            $"/api/files/windows/bugfix/{artifactId}", "10.0.22000",
            "10.0.30000", [], playbook,
            BatchPlaybookPolicy.ComputeSha256(playbook), [support], bundleHash);
        var runner = new WindowsBugfixRunner(Options.Create(
            new WindowsRemediationOptions
            {
                StagingRoot = root, RequireAdministrator = false,
                RequireProtectedAcl = false
            }));
        try
        {
            var result = await runner.RunAsync(path, descriptor,
                TestContext.Current.CancellationToken);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("Completed", result.State);
            Assert.Equal(2, result.CompletedStepCount);

            await File.WriteAllTextAsync(helperPath, "changed",
                TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                runner.RunAsync(path, descriptor,
                    TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, recursive: true); }
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
