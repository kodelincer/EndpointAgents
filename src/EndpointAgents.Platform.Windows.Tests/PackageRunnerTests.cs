using EndpointAgents.Platform.Windows.Office;
using EndpointAgents.Platform.Windows.Packages;
using EndpointAgents.Shared.Office;
using EndpointAgents.Shared.Packages;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace EndpointAgents.Platform.Windows.Tests;

/// <summary>Playbook version 3 and the general package runner.</summary>
public sealed class PackageRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        $"EndpointAgents-package-run-{Guid.NewGuid():N}");

    public PackageRunnerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static BatchPlaybook V3(params BatchPlaybookStep[] steps) => new(3, steps);

    // ---- Policy ----

    [Fact]
    public void Version3AllowsNoStepsAndFillsDefaults()
    {
        Assert.True(BatchPlaybookPolicy.TryNormalize(V3(), out var normalized,
            out var error), error);

        Assert.Empty(normalized!.Steps);
        Assert.Empty(normalized.Arguments!);
        Assert.Equal(BatchPlaybookPolicy.DefaultRunTimeoutSeconds,
            normalized.TimeoutSeconds);
        Assert.Equal([0], normalized.SuccessExitCodes);
        Assert.Empty(normalized.RestartExitCodes!);
    }

    [Fact]
    public void Version3HashCoversArgumentsTimeoutAndRestartCodes()
    {
        var baseline = BatchPlaybookPolicy.ComputeSha256(V3());

        Assert.NotEqual(baseline, BatchPlaybookPolicy.ComputeSha256(
            V3() with { Arguments = ["/quiet"] }));
        Assert.NotEqual(baseline, BatchPlaybookPolicy.ComputeSha256(
            V3() with { TimeoutSeconds = 600 }));
        Assert.NotEqual(baseline, BatchPlaybookPolicy.ComputeSha256(
            V3() with { RestartExitCodes = [3010] }));
    }

    public static TheoryData<BatchPlaybook> InvalidVersion3Playbooks() =>
    [
        new BatchPlaybook(2, []),
        new BatchPlaybook(2, [new BatchPlaybookStep("P", "1", "line", 30)],
            Arguments: ["/quiet"]),
        new BatchPlaybook(2, [new BatchPlaybookStep("P", "1", "line", 30)],
            TimeoutSeconds: 60),
        V3() with { Arguments = ["a & b"] },
        V3() with { Arguments = ["say \"hi\""] },
        V3() with { Arguments = ["%TEMP%"] },
        V3() with { Arguments = ["(x)"] },
        V3() with { Arguments = ["/key:AAAAA-BBBBB-CCCCC-DDDDD-EEEEE"] },
        V3() with { Arguments = ["--password=x"] },
        V3() with { Arguments = [" "] },
        V3() with { Arguments = Enumerable.Repeat("/q", 17).ToArray() },
        V3() with { TimeoutSeconds = 29 },
        V3() with { TimeoutSeconds = 3601 },
        V3() with { RestartExitCodes = [0] },
        V3() with { RestartExitCodes = [3010, 3010] }
    ];

    [Theory]
    [MemberData(nameof(InvalidVersion3Playbooks))]
    public void PolicyRejectsInvalidVersion3Playbooks(BatchPlaybook playbook) =>
        Assert.False(BatchPlaybookPolicy.TryNormalize(playbook, out _, out _));

    [Fact]
    public void Version2HashIsUnchangedByVersion3()
    {
        // Pinned before version 3 existed; published v2 packages must keep their hash.
        var playbook = new BatchPlaybook(2,
            [new BatchPlaybookStep("Select an option", "1", "line", 30)],
            SuccessMarker: "Done");
        var json = JsonSerializer.Serialize(playbook,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var roundTripped = JsonSerializer.Deserialize<BatchPlaybook>(json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(BatchPlaybookPolicy.ComputeSha256(playbook),
            BatchPlaybookPolicy.ComputeSha256(roundTripped));
    }

    // ---- Running ----

    [Fact]
    public async Task NoInputBatRunsToTheEndThroughPauseAndSetP()
    {
        var path = await WriteBatAsync("""
            @echo off
            pause
            set /p "x=Type something: "
            echo finished
            exit /b 0
            """);
        var clock = Stopwatch.StartNew();

        var result = await RunAsync(path, PackageFileTypes.Bat,
            V3() with { SuccessMarker = "finished" });

        Assert.Equal(OfficeBugfixRunStates.Completed, result.State);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), clock.Elapsed.ToString());
    }

    [Fact]
    public async Task FixedArgumentsReachTheBatLiterally()
    {
        var path = await WriteBatAsync("""
            @echo off
            if not "%~1"=="/quiet" exit /b 5
            if not "%~2"=="two words" exit /b 6
            if not "%~3"=="C:\Temp\x" exit /b 7
            exit /b 0
            """);

        var result = await RunAsync(path, PackageFileTypes.Bat,
            V3() with { Arguments = ["/quiet", "two words", @"C:\Temp\x"] });

        Assert.True(result.State == OfficeBugfixRunStates.Completed,
            $"{result.State} exit {result.ExitCode}");
    }

    [Fact]
    public async Task RestartExitCodeCountsAsSuccess()
    {
        var path = await WriteBatAsync("@echo off\r\nexit /b 3010\r\n");
        var playbook = V3() with { RestartExitCodes = [3010] };

        var result = await RunAsync(path, PackageFileTypes.Bat, playbook);

        Assert.Equal(OfficeBugfixRunStates.Completed, result.State);
        Assert.Equal(3010, result.ExitCode);
        Assert.True(BatchPlaybookPolicy.IsRestartExitCode(playbook, result.ExitCode));
    }

    [Fact]
    public async Task UnlistedExitCodeFails()
    {
        var path = await WriteBatAsync("@echo off\r\nexit /b 3010\r\n");

        var result = await RunAsync(path, PackageFileTypes.Bat, V3());

        Assert.Equal(OfficeBugfixRunStates.ProcessFailed, result.State);
    }

    [Fact]
    public async Task LongRunIsStoppedAtTheOverallTimeout()
    {
        var path = await WriteBatAsync("@echo off\r\n>nul ping -n 60 127.0.0.1\r\nexit /b 0\r\n");

        var result = await OfficeBugfixRunner.RunProcessAsync(
            PackageRunner.CreateStartInfo(path, PackageFileTypes.Bat, []), V3(),
            TestContext.Current.CancellationToken, TimeSpan.FromSeconds(3));

        Assert.Equal(OfficeBugfixRunStates.TimedOut, result.State);
    }

    [Fact]
    public async Task ConsoleExeRunsDirectlyWithItsArguments()
    {
        var exe = Path.Combine(Environment.SystemDirectory, "where.exe");

        var found = await RunAsync(exe, PackageFileTypes.Exe,
            V3() with { Arguments = ["cmd.exe"], SuccessMarker = "cmd.exe" });
        var missing = await RunAsync(exe, PackageFileTypes.Exe,
            V3() with { Arguments = ["no-such-tool-xyz.exe"] });

        Assert.Equal(OfficeBugfixRunStates.Completed, found.State);
        Assert.Equal(OfficeBugfixRunStates.ProcessFailed, missing.State);
    }

    [Fact]
    public void ExeStartsDirectlyAndBatStartsThroughCmd()
    {
        var exe = PackageRunner.CreateStartInfo(@"C:\x\tool.exe", PackageFileTypes.Exe,
            ["/s", "a b"]);
        var bat = PackageRunner.CreateStartInfo(@"C:\x\tool.bat", PackageFileTypes.Bat,
            ["/s", "a b"]);

        Assert.Equal(@"C:\x\tool.exe", exe.FileName);
        Assert.Equal(["/s", "a b"], exe.ArgumentList);
        Assert.EndsWith("cmd.exe", bat.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/d /s /c \"\"C:\\x\\tool.bat\" /s \"a b\"\"", bat.Arguments);
        Assert.True(exe.CreateNoWindow && bat.CreateNoWindow);
    }

    [Fact]
    public async Task RunnerRunsTheStagedSamplePackageAndRejectsATamperedCopy()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "EndpointAgents.slnx")))
            root = Path.GetDirectoryName(root)!;
        var bytes = await File.ReadAllBytesAsync(Path.Combine(root, "deploy", "samples",
            "no-input-repair-sample.bat"), TestContext.Current.CancellationToken);
        var playbook = V3() with
        {
            Arguments = ["/quiet"], RestartExitCodes = [3010],
            SuccessMarker = "Spooler reset completed.", FailureMarkers = ["ERROR:"]
        };
        BatchPlaybookPolicy.TryNormalize(playbook, out var normalized, out _);
        var descriptor = Descriptor() with
        {
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            Playbook = normalized!,
            PlaybookSha256 = BatchPlaybookPolicy.ComputeSha256(normalized!)
        };
        var client = new PackageArtifactClient(
            new EndpointArtifactTransport(new Uri("http://127.0.0.1:5052/agent-hub"),
                new NoToken()),
            Microsoft.Extensions.Options.Options.Create(
                new EndpointAgents.Platform.Windows.Windows.WindowsRemediationOptions
                {
                    StagingRoot = _root,
                    RequireProtectedAcl = false,
                    RequireAdministrator = false
                }));
        var staged = client.GetStagedPath(descriptor);
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        await File.WriteAllBytesAsync(staged, bytes, TestContext.Current.CancellationToken);
        using var adminKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(adminKey.ExportSubjectPublicKeyInfo());
        var signed = Signed(descriptor, adminKey, publicKey);
        var runner = new PackageRunner(client, Microsoft.Extensions.Options.Options.Create(
            new PackageSigningOptions { TrustedPublicKeys = [publicKey] }));

        var result = await runner.RunAsync(staged, signed,
            TestContext.Current.CancellationToken);
        // Unsigned, or signed by a key the endpoint does not trust: refused before running.
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var foreign = Signed(descriptor, otherKey,
            Convert.ToBase64String(otherKey.ExportSubjectPublicKeyInfo()));
        await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(staged,
            descriptor, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(staged,
            foreign, TestContext.Current.CancellationToken));
        await File.AppendAllTextAsync(staged, "rem tampered\r\n",
            TestContext.Current.CancellationToken);

        Assert.True(result.State == OfficeBugfixRunStates.Completed, result.ToString());
        Assert.False(result.RestartRequired);
        await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(staged,
            signed, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void SignatureCoversEveryPinnedValue()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var signed = Signed(Descriptor(), key, publicKey);

        Assert.True(PackageSignaturePolicy.Verify(signed, [publicKey]));
        Assert.False(PackageSignaturePolicy.Verify(signed, []));
        Assert.False(PackageSignaturePolicy.Verify(signed with { Sha256 = new string('B', 64) },
            [publicKey]));
        Assert.False(PackageSignaturePolicy.Verify(
            signed with { PlaybookSha256 = new string('C', 64) }, [publicKey]));
        Assert.False(PackageSignaturePolicy.Verify(signed with { Publisher = "CN=Other" },
            [publicKey]));
        Assert.False(PackageSignaturePolicy.Verify(signed with { PackageId = "other" },
            [publicKey]));
        Assert.False(PackageSignaturePolicy.Verify(signed with
            {
                Signature = signed.Signature! with { Value = "not base64!" }
            }, [publicKey]));
    }

    [Fact]
    public void AcceptedRisksAreCoveredByTheSignatureAndCannotBeAddedLater()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var baseline = Descriptor();
        var accepted = baseline with { AcceptedRisks = ["self-elevation"] };
        var signed = Signed(accepted, key, publicKey);

        Assert.True(PackageSignaturePolicy.Verify(signed, [publicKey]));
        // A package signed with no accepted risks keeps its original statement and signature.
        Assert.Equal(
            PackageSignaturePolicy.BuildStatement("p", "p.bat", new string('A', 64), null, null, null),
            PackageSignaturePolicy.BuildStatement("p", "p.bat", new string('A', 64), null, null, null, []));
        // Adding, removing or changing a risk breaks the signature.
        Assert.False(PackageSignaturePolicy.Verify(signed with { AcceptedRisks = null }, [publicKey]));
        Assert.False(PackageSignaturePolicy.Verify(
            signed with { AcceptedRisks = ["self-elevation", "embedded-payload"] }, [publicKey]));
        Assert.False(PackageSignaturePolicy.Verify(
            signed with { AcceptedRisks = ["embedded-payload"] }, [publicKey]));
        var unsignedBaseline = Signed(baseline, key, publicKey);
        Assert.False(PackageSignaturePolicy.Verify(
            unsignedBaseline with { AcceptedRisks = ["self-elevation"] }, [publicKey]));
        // Order and case do not matter.
        Assert.True(PackageSignaturePolicy.Verify(Signed(
            baseline with { AcceptedRisks = ["embedded-payload", "self-elevation"] }, key, publicKey)
            with { AcceptedRisks = ["Self-Elevation", "EMBEDDED-PAYLOAD"] }, [publicKey]));
    }

    [Fact]
    public void ExeWithoutPinnedPublisherIsRejected()
    {
        var exe = Descriptor() with
        {
            FileType = PackageFileTypes.Exe, FileName = "spooler-reset.exe"
        };

        Assert.False(PackageDescriptorPolicy.TryValidate(exe, out _));
        Assert.True(PackageDescriptorPolicy.TryValidate(
            exe with { Publisher = "CN=Contoso" }, out _));
    }

    private static PackageDescriptor Signed(PackageDescriptor descriptor, ECDsa key,
        string publicKey) => descriptor with
    {
        Signature = PackageSignaturePolicy.Sign(PackageSignaturePolicy.BuildStatement(
                descriptor.PackageId, descriptor.FileName, descriptor.Sha256,
                descriptor.PlaybookSha256, descriptor.BundleSha256, descriptor.Publisher,
                descriptor.AcceptedRisks),
            key, publicKey)
    };

    private sealed class NoToken : IEndpointArtifactAuthorizationProvider
    {
        public Task<string?> GetBearerTokenAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
    }

    // ---- Descriptor and command checks ----

    [Fact]
    public void DescriptorPolicyRejectsTamperedRevisions()
    {
        var valid = Descriptor();

        Assert.True(PackageDescriptorPolicy.TryValidate(valid, out _));
        Assert.False(PackageDescriptorPolicy.TryValidate(
            valid with { DownloadPath = "/api/files/windows/bugfix/x" }, out _));
        Assert.False(PackageDescriptorPolicy.TryValidate(
            valid with { PlaybookSha256 = new string('0', 64) }, out _));
        Assert.False(PackageDescriptorPolicy.TryValidate(
            valid with { FileType = PackageFileTypes.Exe }, out _));
        Assert.False(PackageDescriptorPolicy.TryValidate(
            valid with { Playbook = valid.Playbook with { Arguments = ["a&b"] } }, out _));
        Assert.False(PackageDescriptorPolicy.TryValidate(
            valid with { PackageId = "Bad Id" }, out _));
    }

    [Fact]
    public void CompatibilityUsesTheWindowsVersionRange()
    {
        var descriptor = Descriptor() with
        {
            MinWindowsVersion = "10.0.19041",
            MaxWindowsVersionExclusive = "10.0.22000"
        };

        Assert.True(PackageDescriptorPolicy.IsCompatible(descriptor,
            new Version(10, 0, 19045)));
        Assert.False(PackageDescriptorPolicy.IsCompatible(descriptor,
            new Version(10, 0, 22631)));
        Assert.False(PackageDescriptorPolicy.IsCompatible(descriptor,
            new Version(10, 0, 17763)));
    }

    [Fact]
    public void CommandAcceptsOnlyThePackageIdAndControlPlanePins()
    {
        var valid = JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            ["artifactId"] = "spooler-reset",
            ["userSid"] = "S-1-5-21-1-2-3-1001",
            ["_controlPlaneAuthorizationId"] = "abc",
            ["_approvedArtifactSha256"] = new string('A', 64)
        });
        var withArguments = JsonSerializer.SerializeToElement(new
        {
            artifactId = "spooler-reset",
            arguments = new[] { "/x" }
        });

        Assert.True(PackageRunCommandHandler.HasOnlyApprovedParameters(valid));
        Assert.False(PackageRunCommandHandler.HasOnlyApprovedParameters(withArguments));
    }

    private static PackageDescriptor Descriptor()
    {
        var playbook = V3() with { Arguments = ["/quiet"] };
        BatchPlaybookPolicy.TryNormalize(playbook, out var normalized, out _);
        return new PackageDescriptor("spooler-reset", "Reset spooler", "Clears jobs",
            PackageFileTypes.Bat, "spooler-reset.bat", new string('A', 64),
            PackageRoutes.Download("spooler-reset"), normalized!,
            BatchPlaybookPolicy.ComputeSha256(normalized!), [], null, null, null, null,
            ["Printing is stuck"]);
    }

    private async Task<string> WriteBatAsync(string content)
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.bat");
        await File.WriteAllTextAsync(path, content.Replace("\r\n", "\n")
                .Replace("\n", "\r\n"), TestContext.Current.CancellationToken);
        return path;
    }

    private static Task<OfficeBugfixRunResult> RunAsync(string path, string fileType,
        BatchPlaybook playbook)
    {
        Assert.True(BatchPlaybookPolicy.TryNormalize(playbook, out var normalized,
            out var error), error);
        return OfficeBugfixRunner.RunProcessAsync(
            PackageRunner.CreateStartInfo(path, fileType, normalized!.Arguments ?? []),
            normalized, TestContext.Current.CancellationToken);
    }
}
