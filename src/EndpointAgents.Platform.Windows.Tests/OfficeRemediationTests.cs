using EndpointAgents.Platform.Windows;
using EndpointAgents.Platform.Windows.Office;
using EndpointAgents.Shared.LocalIpc;
using EndpointAgents.Shared.Office;
using EndpointAgents.Shared.Security;
using EndpointAgents.Tray;
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
    public void BugfixRunnerUsesHiddenServiceCmdWithOnlyFixedMenuInput()
    {
        var path = Path.Combine(OfficeRemediationPaths.StagingRoot,
            "office-activation-repair", new string('A', 64),
            "office-activation-repair.bat");

        var startInfo = OfficeBugfixRunner.CreateStartInfo(path);

        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            startInfo.FileName);
        Assert.Equal(["/d", "/s", "/c", path], startInfo.ArgumentList);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Empty(startInfo.Verb);
        Assert.Empty(startInfo.UserName);
        Assert.Null(startInfo.Password);
        Assert.True(OfficeBugfixRunner.Timeout < TimeSpan.FromMinutes(10));
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
            "14.0", "15.0", "/api/files/office/bugfix/office-bugfix-14");

        Assert.Equal(expected, OfficeBugfixCommandHandler.IsCompatible(
            artifact, Version.Parse(officeVersion)));
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

    private sealed class FakeArtifactAuthorization(string? token)
        : IEndpointArtifactAuthorizationProvider
    {
        public Task<string?> GetBearerTokenAsync(CancellationToken cancellationToken) =>
            Task.FromResult(token);
    }
}
