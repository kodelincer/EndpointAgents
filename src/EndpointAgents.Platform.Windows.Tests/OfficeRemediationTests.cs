using EndpointAgents.Core.Commands;
using EndpointAgents.Platform.Windows;
using EndpointAgents.Platform.Windows.Ibsng;
using EndpointAgents.Platform.Windows.Office;
using EndpointAgents.Platform.Windows.UserSessions;
using EndpointAgents.Shared.Chat;
using EndpointAgents.Shared.LocalIpc;
using EndpointAgents.Shared.Office;
using EndpointAgents.Shared.Rpc;
using EndpointAgents.Shared.Security;
using EndpointAgents.Tray;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace EndpointAgents.Platform.Windows.Tests;

public sealed class OfficeRemediationTests
{
    [Fact]
    public void RuntimeInspectorReportsOnlyOfficeAppsInTheBoundSession()
    {
        var status = OfficeRuntimeInspector.Evaluate(7,
        [
            ("WINWORD", 7),
            ("EXCEL", 8),
            ("POWERPNT", 7),
            ("VISIO", 7),
            ("WINPROJ", 7),
            ("notepad", 7)
        ]);

        Assert.True(status.InspectionSucceeded);
        Assert.True(status.OfficeApplicationsOpen);
        Assert.Equal(["Word", "PowerPoint", "Visio", "Project"],
            status.RunningApplications);
    }

    [Fact]
    public void RuntimeInspectorReportsReadyWhenOfficeAppsAreClosed()
    {
        var status = OfficeRuntimeInspector.Evaluate(7,
            [("WINWORD", 8), ("notepad", 7)]);

        Assert.True(status.InspectionSucceeded);
        Assert.False(status.OfficeApplicationsOpen);
        Assert.Empty(status.RunningApplications);
    }

    [Theory]
    [InlineData("m365-repair", true)]
    [InlineData("office.fix_2024", true)]
    [InlineData("../tool", false)]
    [InlineData("Tool With Spaces", false)]
    [InlineData("", false)]
    public void CommandAcceptsOnlyStableArtifactIds(string artifactId, bool expected)
    {
        var parameters = JsonSerializer.SerializeToElement(new { artifactId });

        var valid = OfficeRemediationCommandHandler.TryParseArtifactId(parameters, out var parsed);

        Assert.Equal(expected, valid);
        if (expected) Assert.Equal(artifactId, parsed);
    }

    [Fact]
    public void PresentInvalidUserSidIsRejectedInsteadOfSelectingAnotherSession()
    {
        var invalid = JsonSerializer.SerializeToElement(new { userSid = "S-1-invalid" });
        var missing = JsonSerializer.SerializeToElement(new { artifactId = "repair" });

        Assert.False(OfficeRemediationCommandHandler.TryParseUserSid(
            invalid, out _));
        Assert.True(OfficeRemediationCommandHandler.TryParseUserSid(
            missing, out var userSid));
        Assert.Null(userSid);
    }

    [Fact]
    public async Task TrayHashCheckRequiresPeHeaderAndExactSha256()
    {
        var root = Path.Combine(Path.GetTempPath(), $"EndpointAgents-office-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "repair.exe");
            var bytes = new byte[] { (byte)'M', (byte)'Z', 1, 2, 3, 4 };
            await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));

            Assert.True(await OfficeRemediationLauncher.MatchesExecutableHashAsync(path, hash,
                TestContext.Current.CancellationToken));
            Assert.False(await OfficeRemediationLauncher.MatchesExecutableHashAsync(path,
                new string('A', 64), TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DownloaderAllowsHashVerifiedBatchOnlyWhenPeHeaderIsNotRequired()
    {
        var root = Path.Combine(Path.GetTempPath(), $"EndpointAgents-bat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "office-activation-repair.bat");
            var bytes = "@echo off\r\nexit /b 0\r\n"u8.ToArray();
            await File.WriteAllBytesAsync(path, bytes,
                TestContext.Current.CancellationToken);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));

            Assert.True(await OfficeArtifactDownloader.IsApprovedLocalFileAsync(
                path, hash, TestContext.Current.CancellationToken,
                requirePortableExecutable: false));
            Assert.False(await OfficeArtifactDownloader.IsApprovedLocalFileAsync(
                path, hash, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ArtifactTransportRequiresBearerAndRemoteHttps()
    {
        var withoutCredential = new EndpointArtifactTransport(
            new Uri("http://127.0.0.1:5052/agent-hub"),
            new FakeArtifactAuthorization(null));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            withoutCredential.CreateGetRequestAsync("/api/files/office/catalog",
                TestContext.Current.CancellationToken));

        var remoteHttp = new EndpointArtifactTransport(
            new Uri("http://controlplane.example/agent-hub"),
            new FakeArtifactAuthorization("device-token"));
        Assert.Throws<InvalidOperationException>(() => remoteHttp.CreateClient());

        var loopback = new EndpointArtifactTransport(
            new Uri("http://127.0.0.1:5052/agent-hub"),
            new FakeArtifactAuthorization("device-token"));
        using var request = await loopback.CreateGetRequestAsync(
            "/api/files/office/bugfix/catalog",
            TestContext.Current.CancellationToken);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("device-token", request.Headers.Authorization.Parameter);
    }

    [Fact]
    public void BugfixRunnerUsesHiddenServiceCmdWithRedirectedTerminalStreams()
    {
        var path = Path.Combine(OfficeRemediationPaths.StagingRoot,
            "office-activation-repair", new string('A', 64),
            "office-activation-repair.bat");

        var startInfo = OfficeBugfixRunner.CreateStartInfo(path);

        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            startInfo.FileName);
        Assert.Equal(["/d", "/s", "/c", path], startInfo.ArgumentList);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Empty(startInfo.Verb);
        Assert.Empty(startInfo.UserName);
        Assert.Null(startInfo.Password);
        Assert.True(OfficeBugfixRunner.Timeout < TimeSpan.FromMinutes(10));
    }

    [Fact]
    public async Task BugfixHandlerRefusesToRunWhenKmsIsAlreadyHealthy()
    {
        const string artifactId = "office-activation-repair";
        var artifact = new OfficeBugfixArtifactDescriptor(artifactId,
            "Office activation repair", "Approved KMS repair",
            $"{artifactId}.bat", new string('A', 64), "16.0", "17.0",
            TestPlaybook(), BatchPlaybookPolicy.ComputeSha256(TestPlaybook()),
            $"/api/files/office/bugfix/{artifactId}");
        var probe = new CountingVolumeProbe(new OfficeActivationProbeResult(
            OfficeActivationMechanisms.Ospp, true, true,
            [new OfficeActivationProduct("Office LTSC",
                OfficeLicenseTypes.VolumeKms, OfficeActivationStates.Licensed,
                null, OfficeActivationMechanisms.Ospp)]));
        var handler = new OfficeBugfixCommandHandler(
            new OfficeInventoryCollector(new InstalledOfficeSource()),
            new FakeBugfixCatalog(artifact), new FakeOfficeSessionSource(),
            new OfficeRuntimeInspector(), null!, probe, null!, null!,
            NullLogger<OfficeBugfixCommandHandler>.Instance);
        var request = RpcRequest.Create("healthy-kms",
            "software.office_bugfix_run",
            JsonSerializer.SerializeToElement(new { artifactId }));

        var response = await handler.HandleAsync(request,
            new CommandExecutionContext("agent", null, null, DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);

        Assert.Equal(409, response.Error?.Code);
        Assert.Contains("already has healthy KMS activation",
            response.Error?.Message);
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task DesktopCopyKeepsOnlyAnExactHashVerifiedBatch()
    {
        var root = Path.Combine(Path.GetTempPath(),
            $"EndpointAgents-desktop-{Guid.NewGuid():N}");
        var desktop = Path.Combine(root, "Desktop");
        Directory.CreateDirectory(desktop);
        try
        {
            var source = Path.Combine(root, "source.bat");
            var bytes = "@echo off\r\necho 1\r\n"u8.ToArray();
            await File.WriteAllBytesAsync(source, bytes,
                TestContext.Current.CancellationToken);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));

            using var identity = WindowsIdentity.GetCurrent();
            var session = new EndpointAgents.Platform.Windows.UserSessions.InteractiveUserSession(
                Process.GetCurrentProcess().SessionId, identity.Name,
                null, identity.User!.Value, desktop);
            var destination = await DesktopArtifactFile.CopyForSessionAsync(source,
                session, "office-activation-repair.bat", hash,
                TestContext.Current.CancellationToken);

            Assert.Equal(Path.Combine(desktop, "office-activation-repair.bat"),
                destination);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(destination,
                TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                DesktopArtifactFile.CopyForSessionAsync(source, session,
                    "wrong.bat", new string('A', 64),
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TrayRejectsRemediationOutsideAgentStagingRoot()
    {
        var launch = new OfficeRemediationLaunchRequest("m365-repair", "Microsoft repair",
            "Approved repair", @"C:\Windows\Temp\repair.exe", new string('A', 64));
        var request = new TrayIpcRequest("test", TrayIpcProtocol.OfficeRemediationLaunchMethod,
            string.Empty, string.Empty, null, OfficeRemediation: launch);

        var response = await new OfficeRemediationLauncher().LaunchAsync(request,
            TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Equal("RemediationFileUnavailable", response.State);
    }

    [Fact]
    public async Task ServiceRunnerRejectsAUserWritableDesktopCopy()
    {
        var approved = await OfficeBugfixRunner.IsApprovedAsync(
            Path.Combine(Path.GetTempPath(), "office-activation-repair.bat"),
            "office-activation-repair", "office-activation-repair.bat",
            new string('A', 64), TestContext.Current.CancellationToken);

        Assert.False(approved);
    }

    [Fact]
    public void PrivilegedStagingAclRejectsAnUntrustedWriter()
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var inheritance = InheritanceFlags.ContainerInherit
            | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true,
            preserveInheritance: false);
        security.SetOwner(administrators);
        security.AddAccessRule(new FileSystemAccessRule(system,
            FileSystemRights.FullControl, inheritance, PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(administrators,
            FileSystemRights.FullControl, inheritance, PropagationFlags.None,
            AccessControlType.Allow));

        Assert.True(ProtectedWindowsPath.IsSecure(security,
            requireProtectedAcl: true));

        security.AddAccessRule(new FileSystemAccessRule(users,
            FileSystemRights.Write, inheritance, PropagationFlags.None,
            AccessControlType.Allow));

        Assert.False(ProtectedWindowsPath.IsSecure(security,
            requireProtectedAcl: true));
    }

    [Fact]
    public void ProtectedPathAncestorRejectsAnUntrustedOwnerOrReplacementRight()
    {
        var administrators = new SecurityIdentifier(
            WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true,
            preserveInheritance: false);
        security.SetOwner(administrators);
        security.AddAccessRule(new FileSystemAccessRule(users,
            FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(users,
            FileSystemRights.FullControl, InheritanceFlags.ContainerInherit
                | InheritanceFlags.ObjectInherit,
            PropagationFlags.InheritOnly, AccessControlType.Allow));

        Assert.True(ProtectedWindowsPath.IsSafeAncestor(security));

        security.AddAccessRule(new FileSystemAccessRule(users,
            FileSystemRights.DeleteSubdirectoriesAndFiles,
            AccessControlType.Allow));
        Assert.False(ProtectedWindowsPath.IsSafeAncestor(security));

        security.RemoveAccessRuleAll(new FileSystemAccessRule(users,
            FileSystemRights.FullControl, AccessControlType.Allow));
        security.SetOwner(users);
        Assert.False(ProtectedWindowsPath.IsSafeAncestor(security));
    }

    [Theory]
    [InlineData("14.0", true)]
    [InlineData("14.99.1", true)]
    [InlineData("15.0", false)]
    [InlineData("13.9", false)]
    public void BugfixCompatibilityUsesExclusiveMaximum(string officeVersion,
        bool expected)
    {
        var artifact = new OfficeBugfixArtifactDescriptor("office-activation-repair",
            "Office activation repair", "Approved batch tool",
            "office-activation-repair.bat", new string('A', 64),
            "14.0", "15.0", TestPlaybook(),
            BatchPlaybookPolicy.ComputeSha256(TestPlaybook()),
            "/api/files/office/bugfix/office-bugfix-14");

        Assert.Equal(expected, OfficeBugfixCommandHandler.IsCompatible(
            artifact, Inventory(officeVersion, "ProPlus2021Volume"),
            new Version(10, 0, 22631)));
    }

    [Fact]
    public void BugfixCompatibilityChecksExactOfficeProductIdsAndWindowsBuild()
    {
        var artifact = new OfficeBugfixArtifactDescriptor("office-2021-repair",
            "Office 2021 repair", "Approved batch tool", "office-2021-repair.bat",
            new string('A', 64), "16.0", "17.0", TestPlaybook(),
            BatchPlaybookPolicy.ComputeSha256(TestPlaybook()),
            "/api/files/office/bugfix/office-2021-repair",
            ["ProPlus2021Volume", "VisioPro2021Volume"], "10.0.22000", "10.0.30000");

        Assert.True(OfficeBugfixCommandHandler.IsCompatible(artifact,
            Inventory("16.0.14332", "ProPlus2021Volume,VisioPro2021Volume"),
            new Version(10, 0, 22631)));
        Assert.False(OfficeBugfixCommandHandler.IsCompatible(artifact,
            Inventory("16.0.14332", "ProPlus2019Volume"),
            new Version(10, 0, 22631)));
        Assert.False(OfficeBugfixCommandHandler.IsCompatible(artifact,
            Inventory("16.0.14332", "ProPlus2021Volume,VisioPro2019Volume"),
            new Version(10, 0, 22631)));
        Assert.False(OfficeBugfixCommandHandler.IsCompatible(artifact,
            Inventory("16.0.14332", null), new Version(10, 0, 22631)));
        Assert.False(OfficeBugfixCommandHandler.IsCompatible(artifact,
            Inventory("16.0.14332", "ProPlus2021Volume"),
            new Version(10, 0, 19045)));
    }

    [Fact]
    public void OneOfficeBatchCanCoverSeveralProductAndWindowsRanges()
    {
        var artifact = new OfficeBugfixArtifactDescriptor("office-shared-repair",
            "Shared repair", "Approved batch tool", "office-shared-repair.bat",
            new string('A', 64), "16.0", "17.0", TestPlaybook(),
            BatchPlaybookPolicy.ComputeSha256(TestPlaybook()),
            "/api/files/office/bugfix/office-shared-repair",
            ["ProPlus2019Volume"], "10.0.19041", "10.0.22000",
            AdditionalCompatibility:
            [
                new OfficeBugfixCompatibility("16.0", "17.0",
                    ["ProPlus2021Volume"], "10.0.22000", "10.0.26000"),
                new OfficeBugfixCompatibility("16.0", "17.0",
                    ["ProPlus2024Volume"], "10.0.26000", "10.0.30000")
            ]);

        Assert.True(OfficeBugfixCommandHandler.IsCompatible(artifact,
            Inventory("16.0.1", "ProPlus2019Volume"),
            new Version(10, 0, 19045)));
        Assert.True(OfficeBugfixCommandHandler.IsCompatible(artifact,
            Inventory("16.0.1", "ProPlus2021Volume"),
            new Version(10, 0, 22631)));
        Assert.True(OfficeBugfixCommandHandler.IsCompatible(artifact,
            Inventory("16.0.1", "ProPlus2024Volume"),
            new Version(10, 0, 26100)));
        Assert.False(OfficeBugfixCommandHandler.IsCompatible(artifact,
            Inventory("16.0.1", "ProPlus2021Volume"),
            new Version(10, 0, 19045)));
        Assert.False(OfficeBugfixCommandHandler.IsCompatible(artifact,
            Inventory("16.0.1", "ProPlus2019Volume,ProPlus2021Volume"),
            new Version(10, 0, 22631)));
    }

    [Fact]
    public void TrayKeepsMicrosoftAndAdministratorHashTrustSeparate()
    {
        Assert.True(OfficeRemediationLauncher.TryResolveTrustPolicy(
            OfficeArtifactTrustPolicies.MicrosoftSigned,
            out var requiresMicrosoftSignature, out _));
        Assert.True(requiresMicrosoftSignature);

        Assert.True(OfficeRemediationLauncher.TryResolveTrustPolicy(
            OfficeArtifactTrustPolicies.AdministratorApprovedSha256,
            out requiresMicrosoftSignature, out _));
        Assert.False(requiresMicrosoftSignature);

        Assert.False(OfficeRemediationLauncher.TryResolveTrustPolicy(
            "Unknown", out _, out _));
    }

    [Fact]
    public void OfficeBugfixAuthorizationPinsBatAndPlaybookRevisions()
    {
        var original = TestPlaybook();
        var changed = original with
        {
            Steps =
            [
                original.Steps[0],
                original.Steps[1] with { Send = "N" }
            ]
        };
        var originalHash = BatchPlaybookPolicy.ComputeSha256(original);
        var parameters = JsonSerializer.SerializeToElement(
            new Dictionary<string, string>
            {
                [ChatActionApproval.ArtifactFileNameParameterName] = "repair.bat",
                [ChatActionApproval.ArtifactSha256ParameterName] = new string('A', 64),
                [ChatActionApproval.PlaybookSha256ParameterName] = originalHash
            });

        Assert.True(ChatActionApproval.MatchesArtifactAndPlaybook(parameters,
            "repair.bat", new string('A', 64), originalHash));
        Assert.False(ChatActionApproval.MatchesArtifactAndPlaybook(parameters,
            "repair.bat", new string('C', 64), originalHash));
        Assert.False(ChatActionApproval.MatchesArtifactAndPlaybook(parameters,
            "repair.bat", new string('A', 64),
            BatchPlaybookPolicy.ComputeSha256(changed)));
    }

    [Fact]
    public void OfficeBugfixRpcCannotOverridePlaybookResponses()
    {
        var attemptedOverride = JsonSerializer.SerializeToElement(new
        {
            artifactId = "office-activation-repair",
            send = "N",
            response = "2"
        });
        var serverScoped = JsonSerializer.SerializeToElement(
            new Dictionary<string, string>
            {
                ["artifactId"] = "office-activation-repair",
                ["userSid"] = "S-1-5-21-1-2-3-1001",
                [ChatActionApproval.ArtifactFileNameParameterName] =
                    "office-activation-repair.bat",
                [ChatActionApproval.ArtifactSha256ParameterName] = new string('A', 64),
                [ChatActionApproval.PlaybookSha256ParameterName] = new string('B', 64)
            });

        Assert.False(OfficeBugfixCommandHandler.HasOnlyApprovedParameters(
            attemptedOverride));
        Assert.True(OfficeBugfixCommandHandler.HasOnlyApprovedParameters(
            serverScoped));
    }

    [Fact]
    public async Task InteractiveRunnerCompletesDelayedNestedLineAndKeyMenus()
    {
        var playbook = new BatchPlaybook(1,
        [
            new BatchPlaybookStep("Select an option", "1", "line", 5),
            new BatchPlaybookStep("Select Office operation", "2", "line", 5),
            new BatchPlaybookStep("Continue [Y/N]", "Y", "key", 5)
        ]);
        var result = await RunFixtureAsync(
            """
            @echo off
            setlocal
            <nul set /p "=Files are extracting... "
            >nul ping -n 2 127.0.0.1
            <nul set /p "=Select "
            >nul ping -n 2 127.0.0.1
            <nul set /p "=an option: "
            set /p "first="
            if not "%first%"=="1" exit /b 11
            >&2 <nul set /p "=Select Office operation: "
            set /p "second="
            if not "%second%"=="2" exit /b 12
            choice /c YN /n /m "Continue [Y/N]: "
            if errorlevel 2 exit /b 13
            exit /b 0
            """, playbook,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.State == OfficeBugfixRunStates.Completed,
            result.ToString());
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(3, result.CompletedStepCount);
        Assert.Null(result.FailedStepIndex);
    }

    [Fact]
    public async Task CheckedInSampleBatCompletesApprovedMenuPlaybook()
    {
        var path = Path.Combine(AppContext.BaseDirectory,
            "office-activation-repair.bat");
        var playbook = new BatchPlaybook(1,
        [
            new BatchPlaybookStep("Select an item:", "1", "line", 5),
            new BatchPlaybookStep("Continue with harmless sample? [Y/N]:",
                "Y", "key", 5)
        ]);

        var result = await OfficeBugfixRunner.RunProcessAsync(
            OfficeBugfixRunner.CreateStartInfo(path), playbook,
            TestContext.Current.CancellationToken);

        Assert.Equal(OfficeBugfixRunStates.Completed, result.State);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, result.CompletedStepCount);
    }

    [Fact]
    public void OfficeRemediationOptionsDefaultToProtectedAdministratorStaging()
    {
        var options = new OfficeRemediationOptions();

        Assert.Equal(OfficeRemediationPaths.StagingRoot, options.StagingRoot);
        Assert.True(options.RequireProtectedAcl);
        Assert.True(options.RequireAdministrator);
        Assert.True(options.IsAllowedFor(isDevelopment: false));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void RelaxedOfficeRemediationIsAllowedOnlyInDevelopment(
        bool requireProtectedAcl, bool requireAdministrator, bool protectedRoot)
    {
        var options = new OfficeRemediationOptions
        {
            StagingRoot = protectedRoot ? OfficeRemediationPaths.StagingRoot
                : ".local\\OfficeRemediation",
            RequireProtectedAcl = requireProtectedAcl,
            RequireAdministrator = requireAdministrator
        };

        Assert.False(options.IsAllowedFor(isDevelopment: false));
        Assert.True(options.IsAllowedFor(isDevelopment: true));
    }

    [Fact]
    public async Task DevelopmentRunnerRunsHashPinnedSampleFromLocalStaging()
    {
        const string artifactId = "office-activation-repair";
        var bytes = await File.ReadAllBytesAsync(Path.Combine(
            AppContext.BaseDirectory, $"{artifactId}.bat"),
            TestContext.Current.CancellationToken);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        var root = Path.Combine(Path.GetTempPath(),
            $"EndpointAgents-Office-dev-{Guid.NewGuid():N}");
        var directory = Path.Combine(root, artifactId, sha256);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{artifactId}.bat");
        await File.WriteAllBytesAsync(path, bytes,
            TestContext.Current.CancellationToken);
        var playbook = new BatchPlaybook(1,
        [
            new BatchPlaybookStep("Select an item:", "1", "line", 5),
            new BatchPlaybookStep("Continue with harmless sample? [Y/N]:",
                "Y", "key", 5)
        ]);
        var descriptor = new OfficeBugfixArtifactDescriptor(artifactId,
            "Sample", "Harmless test", $"{artifactId}.bat", sha256, "16.0", "17.0",
            playbook, BatchPlaybookPolicy.ComputeSha256(playbook),
            $"/api/files/office/bugfix/{artifactId}");
        var runner = new OfficeBugfixRunner(Microsoft.Extensions.Options.Options.Create(
            new OfficeRemediationOptions
            {
                StagingRoot = root, RequireProtectedAcl = false,
                RequireAdministrator = false
            }));
        try
        {
            var result = await runner.RunAsync(path, descriptor,
                TestContext.Current.CancellationToken);

            Assert.Equal(OfficeBugfixRunStates.Completed, result.State);
            Assert.Equal(0, result.ExitCode);
            await File.WriteAllTextAsync(path, "@echo tampered",
                TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(
                path, descriptor, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InteractiveRunnerDoesNotReuseStaleMenuText()
    {
        var playbook = new BatchPlaybook(1,
        [
            new BatchPlaybookStep("MENU", "1", "line", 5),
            new BatchPlaybookStep("MENU", "2", "line", 5)
        ]);
        var result = await RunFixtureAsync(
            """
            @echo off
            <nul set /p "=MENU: "
            set /p "first="
            >nul ping -n 30 127.0.0.1
            <nul set /p "=NEXT: "
            set /p "second="
            exit /b 0
            """, playbook, TimeSpan.FromSeconds(60),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.State == OfficeBugfixRunStates.PromptTimedOut,
            result.ToString());
        Assert.Equal(1, result.CompletedStepCount);
        Assert.Equal(1, result.FailedStepIndex);
    }

    [Fact]
    public async Task InteractiveRunnerReportsEarlyExitAndNonzeroExit()
    {
        var early = await RunFixtureAsync(
            """
            @echo off
            <nul set /p "=First: "
            set /p "first="
            exit /b 0
            """,
            new BatchPlaybook(1,
            [
                new BatchPlaybookStep("First", "1", "line", 5),
                new BatchPlaybookStep("Second", "2", "line", 5)
            ]), cancellationToken: TestContext.Current.CancellationToken);
        var failed = await RunFixtureAsync(
            """
            @echo off
            <nul set /p "=Run: "
            set /p "value="
            exit /b 7
            """,
            new BatchPlaybook(1,
                [new BatchPlaybookStep("Run", "Y", "line", 5)]),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(OfficeBugfixRunStates.ExitedBeforePlaybookCompleted,
            early.State);
        Assert.Equal(1, early.CompletedStepCount);
        Assert.Equal(1, early.FailedStepIndex);
        Assert.Equal(OfficeBugfixRunStates.ProcessFailed, failed.State);
        Assert.Equal(7, failed.ExitCode);
    }

    [Fact]
    public async Task InteractiveRunnerReportsOutputOverflowAndCancellation()
    {
        var playbook = new BatchPlaybook(1,
            [new BatchPlaybookStep("Never", "1", "line", 5)]);
        var overflow = await RunFixtureAsync(
            """
            @echo off
            for /L %%i in (1,1,100) do <nul set /p "=0123456789"
            set /p "value=Never: "
            """, playbook, outputLimitBytes: 128,
            cancellationToken: TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(200));
        var cancelled = await RunFixtureAsync(
            """
            @echo off
            set /p "value=Waiting: "
            """, playbook, cancellationToken: cancellation.Token);

        Assert.Equal(OfficeBugfixRunStates.OutputLimitExceeded, overflow.State);
        Assert.Equal(0, overflow.CompletedStepCount);
        Assert.Equal(OfficeBugfixRunStates.Cancelled, cancelled.State);
    }

    [Theory]
    [InlineData(OfficeBugfixRunStates.PromptTimedOut, 408)]
    [InlineData(OfficeBugfixRunStates.ExitedBeforePlaybookCompleted, 409)]
    [InlineData(OfficeBugfixRunStates.OutputLimitExceeded, 413)]
    [InlineData(OfficeBugfixRunStates.Cancelled, 499)]
    [InlineData(OfficeBugfixRunStates.ProcessFailed, 500)]
    public void HandlerMapsInteractionFailures(string state, int expectedCode)
    {
        var failed = OfficeBugfixCommandHandler.TryGetExecutionFailure(
            new OfficeBugfixRunResult(42, null, state, 1, 1),
            out var code, out _);

        Assert.True(failed);
        Assert.Equal(expectedCode, code);
    }

    [Fact]
    public void HandlerRequiresCompletedPlaybookAndHealthyActivationPostCheck()
    {
        var completed = new OfficeBugfixRunResult(42, 0,
            OfficeBugfixRunStates.Completed, 3, null);
        var healthy = new OfficeActivationProbeResult(
            OfficeActivationMechanisms.Ospp, true, true,
            [new OfficeActivationProduct("Office LTSC",
                OfficeLicenseTypes.VolumeKms, OfficeActivationStates.Licensed,
                null, OfficeActivationMechanisms.Ospp)]);
        var unhealthy = healthy with
        {
            Products =
            [
                new OfficeActivationProduct("Office LTSC",
                    OfficeLicenseTypes.VolumeKms,
                    OfficeActivationStates.Unlicensed, null,
                    OfficeActivationMechanisms.Ospp)
            ]
        };

        Assert.True(OfficeBugfixCommandHandler.IsVerifiedSuccess(completed, healthy));
        Assert.False(OfficeBugfixCommandHandler.IsVerifiedSuccess(completed, unhealthy));
        Assert.False(OfficeBugfixCommandHandler.IsVerifiedSuccess(
            completed with { State = OfficeBugfixRunStates.ProcessFailed }, healthy));
    }

    private static async Task<OfficeBugfixRunResult> RunFixtureAsync(string batch,
        BatchPlaybook playbook, TimeSpan? overallTimeout = null,
        int outputLimitBytes = OfficeBugfixRunner.OutputLimitBytes,
        CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(Path.GetTempPath(),
            $"EndpointAgents-playbook-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "fixture.bat");
            await File.WriteAllTextAsync(path,
                batch.Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace("\n", "\r\n", StringComparison.Ordinal),
                cancellationToken.IsCancellationRequested
                    ? CancellationToken.None : cancellationToken);
            return await OfficeBugfixRunner.RunProcessAsync(
                OfficeBugfixRunner.CreateStartInfo(path), playbook,
                cancellationToken, overallTimeout, outputLimitBytes);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static BatchPlaybook TestPlaybook() => new(1,
    [
        new BatchPlaybookStep("Select an item", "1", "line", 30),
        new BatchPlaybookStep("Continue", "Y", "key", 30)
    ]);

    private static OfficeInventory Inventory(string version, string? productReleaseIds) =>
        new(true, version, "x64", [], productReleaseIds is null ? [] :
        [new OfficeClickToRunInstallation("Registry64", version, "x64",
            null, null, productReleaseIds, null)], [], []);

    private sealed class FakeArtifactAuthorization(string? token)
        : IEndpointArtifactAuthorizationProvider
    {
        public Task<string?> GetBearerTokenAsync(CancellationToken cancellationToken) =>
            Task.FromResult(token);
    }

    private sealed class InstalledOfficeSource : IOfficeInstallationSource
    {
        public OfficeClickToRunInstallation? ReadClickToRun(RegistryView view) =>
            view == RegistryView.Registry64
                ? new OfficeClickToRunInstallation("Registry64", "16.0", "x64",
                    null, null, "ProPlus2024Volume", @"C:\Office")
                : null;

        public IReadOnlyList<OfficeInstalledProduct> ReadInstalledProducts(
            RegistryView view) => [];

        public IReadOnlyList<OfficeInstalledApplication> ReadInstalledApplications(
            RegistryView view, IReadOnlyCollection<string> installationPaths) => [];
    }

    private sealed class FakeBugfixCatalog(OfficeBugfixArtifactDescriptor artifact)
        : IOfficeBugfixCatalogClient
    {
        public Task<IReadOnlyList<OfficeBugfixArtifactDescriptor>> GetArtifactsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OfficeBugfixArtifactDescriptor>>([artifact]);
    }

    private sealed class FakeOfficeSessionSource : IOfficeUserSessionSource
    {
        public InteractiveUserSession GetActiveSession(string? userSid = null) =>
            new(int.MaxValue, "test-user", "TEST",
                "S-1-5-21-1-2-3-1001", @"C:\Users\test-user\Desktop");
    }

    private sealed class CountingVolumeProbe(OfficeActivationProbeResult result)
        : IOfficeVolumeActivationProbe
    {
        public int Calls { get; private set; }

        public Task<OfficeActivationProbeResult> ProbeAsync(OfficeInventory inventory,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }
}
