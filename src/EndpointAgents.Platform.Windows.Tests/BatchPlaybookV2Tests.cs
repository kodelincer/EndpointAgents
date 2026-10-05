using EndpointAgents.Platform.Windows.Office;
using EndpointAgents.Shared.Office;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Security.Cryptography;

namespace EndpointAgents.Platform.Windows.Tests;

/// <summary>Runs deploy/samples/menu-scenarios-sample.bat through the real runner.</summary>
public sealed class BatchPlaybookV2Tests
{
    private const int Wait = 30;

    private static BatchPlaybookStep Step(string expect, string send,
        string submit = "line", bool optional = false, int maxRepeats = 1,
        params BatchPlaybookChoice[] alternatives) =>
        new(expect, send, submit, Wait, alternatives.Length == 0 ? null : alternatives,
            optional, maxRepeats);

    private static BatchPlaybook V2(params BatchPlaybookStep[] steps) => new(2, steps);

    // ---- Today's linear version 1 playbooks and their limits ----

    [Fact]
    public async Task V1PlaybookCannotSkipAPromptThatDoesNotAppear()
    {
        var playbook = new BatchPlaybook(1,
        [
            new BatchPlaybookStep("Main menu choice:", "2", "line", 5),
            new BatchPlaybookStep("Press any key", "x", "key", 5)
        ]);

        var result = await RunSampleAsync(playbook);

        Assert.Equal(OfficeBugfixRunStates.ExitedBeforePlaybookCompleted, result.State);
        Assert.Equal(1, result.FailedStepIndex);
    }

    [Fact]
    public async Task V1PlaybookReportsAPrintedFailureAsCompleted()
    {
        var playbook = new BatchPlaybook(1,
            [new BatchPlaybookStep("Main menu choice:", "3", "line", Wait)]);

        var result = await RunSampleAsync(playbook);

        Assert.Equal(OfficeBugfixRunStates.Completed, result.State);
    }

    // ---- Version 2 features ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalPromptIsAnsweredWhenShownAndSkippedOtherwise(bool pause)
    {
        var playbook = V2(
            Step("Main menu choice:", "2"),
            Step("Press any key", "x", "key", optional: true)) with
        {
            SuccessMarker = "Task completed successfully"
        };

        var result = await RunSampleAsync(playbook,
            ("SAMPLE_PAUSE", pause ? "1" : "0"));

        Assert.Equal(OfficeBugfixRunStates.Completed, result.State);
        Assert.Equal(pause ? 2 : 1, result.CompletedStepCount);
    }

    [Fact]
    public async Task LoopingMenuIsWalkedThroughSubMenuAndBackToMain()
    {
        var playbook = V2(
            Step("Main menu choice:", "1"),
            Step("Settings choice:", "A"),
            Step("Main menu choice:", "9")) with
        {
            SuccessMarker = "Logging enabled."
        };

        var result = await RunSampleAsync(playbook);

        Assert.Equal(OfficeBugfixRunStates.Completed, result.State);
        Assert.Equal(3, result.CompletedStepCount);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    public async Task AlternativeAnswersWhicheverPromptTheBatShows(string configured)
    {
        var playbook = V2(
            Step("Main menu choice:", "1"),
            Step("Settings choice:", "B",
                alternatives: new BatchPlaybookChoice("Reconfigure? [Y/N]", "N", "key")),
            Step("Main menu choice:", "9"));

        var result = await RunSampleAsync(playbook, ("SAMPLE_CONFIGURED", configured));

        Assert.Equal(OfficeBugfixRunStates.Completed, result.State);
        Assert.Equal(3, result.CompletedStepCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task RepeatingStepAnswersEveryPageUntilTheNextPrompt(int pages)
    {
        var playbook = V2(
            Step("Press N for the next page:", "N", optional: true, maxRepeats: 5),
            Step("Main menu choice:", "9")) with
        {
            FailureMarkers = ["Unexpected page answer"]
        };

        var result = await RunSampleAsync(playbook,
            ("SAMPLE_PAGES", pages.ToString()));

        Assert.Equal(OfficeBugfixRunStates.Completed, result.State);
        Assert.Equal(pages + 1, result.CompletedStepCount);
    }

    [Fact]
    public async Task RepeatingStepStopsAtItsLimit()
    {
        var playbook = V2(
            Step("Press N for the next page:", "N", maxRepeats: 2),
            Step("Main menu choice:", "9"));

        var result = await RunSampleAsync(playbook, ("SAMPLE_PAGES", "3"));

        Assert.Equal(OfficeBugfixRunStates.RepeatLimitExceeded, result.State);
        Assert.Equal(2, result.CompletedStepCount);
        Assert.Equal(0, result.FailedStepIndex);
    }

    [Fact]
    public async Task FailureMarkerTurnsExitCodeZeroIntoAFailure()
    {
        var playbook = V2(Step("Main menu choice:", "3")) with
        {
            FailureMarkers = ["Task FAILED"]
        };

        var result = await RunSampleAsync(playbook);

        Assert.Equal(OfficeBugfixRunStates.FailureMarkerDetected, result.State);
        Assert.Equal(0, result.ExitCode);
    }

    [Theory]
    [InlineData(new[] { 0 }, OfficeBugfixRunStates.ProcessFailed)]
    [InlineData(new[] { 0, 7 }, OfficeBugfixRunStates.Completed)]
    public async Task ExitCodeIsCheckedAgainstTheApprovedList(int[] codes,
        string expected)
    {
        var playbook = V2(Step("Main menu choice:", "4")) with
        {
            SuccessExitCodes = codes
        };

        var result = await RunSampleAsync(playbook);

        Assert.Equal(expected, result.State);
        Assert.Equal(7, result.ExitCode);
    }

    [Fact]
    public async Task MissingSuccessMarkerIsNotASuccess()
    {
        var playbook = V2(Step("Main menu choice:", "9")) with
        {
            SuccessMarker = "Task completed successfully"
        };

        var result = await RunSampleAsync(playbook);

        Assert.Equal(OfficeBugfixRunStates.SuccessMarkerMissing, result.State);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task KnownUnexpectedPromptAbortsWithoutWaitingForTheTimeout()
    {
        var playbook = V2(
            Step("Main menu choice:", "5"),
            Step("Settings choice:", "A")) with
        {
            FailureMarkers = ["Enter the server name"]
        };
        var clock = Stopwatch.StartNew();

        var result = await RunSampleAsync(playbook);

        Assert.Equal(OfficeBugfixRunStates.FailureMarkerDetected, result.State);
        Assert.Equal(1, result.FailedStepIndex);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(Wait - 5), clock.Elapsed.ToString());
    }

    [Fact]
    public async Task PromptThatNeverAppearsTimesOut()
    {
        var playbook = V2(
            Step("Main menu choice:", "5"),
            new BatchPlaybookStep("Settings choice:", "A", "line", 5));

        var result = await RunSampleAsync(playbook);

        Assert.Equal(OfficeBugfixRunStates.PromptTimedOut, result.State);
        Assert.Equal(1, result.FailedStepIndex);
    }

    [Fact]
    public async Task AnswersReachTheBatWithoutExtraBytes()
    {
        // The sample echoes "Invalid choice [x]" for anything other than an exact menu
        // number, so a stray byte-order mark or newline would show up here.
        var playbook = V2(Step("Main menu choice:", "1"), Step("Settings choice:", "B"),
            Step("Main menu choice:", "9")) with
        {
            FailureMarkers = ["Invalid choice"]
        };

        var result = await RunSampleAsync(playbook);

        Assert.Equal(OfficeBugfixRunStates.Completed, result.State);
    }

    [Fact]
    public async Task StoppedRunNeverFeedsAnEmptyAnswerToTheBat()
    {
        var root = Path.Combine(Path.GetTempPath(), $"EndpointAgents-stop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "default-branch.bat");
        var marker = Path.Combine(root, "default-ran.txt");
        await File.WriteAllTextAsync(path, string.Join("\r\n",
            "@echo off",
            "set \"x=\"",
            "set /p \"x=Press Enter for the default action: \"",
            "if \"%x%\"==\"\" echo ran> \"%~dp0default-ran.txt\"",
            "exit /b 0", ""), TestContext.Current.CancellationToken);
        try
        {
            var result = await OfficeBugfixRunner.RunProcessAsync(
                OfficeBugfixRunner.CreateStartInfo(path),
                V2(new BatchPlaybookStep("Never shown", "1", "line", 5)),
                TestContext.Current.CancellationToken);
            await Task.Delay(500, TestContext.Current.CancellationToken);

            Assert.Equal(OfficeBugfixRunStates.PromptTimedOut, result.State);
            Assert.False(File.Exists(marker));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- GUI dialogs ----

    [Fact]
    public async Task PersistentWindowStopsTheRunWithoutWaitingForTheTimeout()
    {
        var clock = Stopwatch.StartNew();

        var result = await OfficeBugfixRunner.RunProcessAsync(
            OfficeBugfixRunner.CreateStartInfo(SamplePath),
            V2(Step("Never shown", "1")) with { GuiDialogGraceSeconds = 5 },
            TestContext.Current.CancellationToken, guiWindowProbe: _ => true);

        Assert.Equal(OfficeBugfixRunStates.GuiDialogBlocked, result.State);
        Assert.Equal(0, result.FailedStepIndex);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(Wait), clock.Elapsed.ToString());
    }

    [Fact]
    public async Task ShortLivedWindowIsNotADialog()
    {
        var checks = 0;

        var result = await OfficeBugfixRunner.RunProcessAsync(
            OfficeBugfixRunner.CreateStartInfo(SamplePath),
            V2(Step("Main menu choice:", "9")), TestContext.Current.CancellationToken,
            guiWindowProbe: _ => Interlocked.Increment(ref checks) <= 2);

        Assert.Equal(OfficeBugfixRunStates.Completed, result.State);
    }

    // A dialog shown under the hidden, redirected runner returns at once on an
    // interactive desktop, so the real probe is checked against a normally started
    // message box instead. The session 0 behaviour of the service is not reproduced.
    [Fact]
    public async Task ProbeSeesAMessageBoxButNotAWindowlessProcess()
    {
        using var dialog = Process.Start(new ProcessStartInfo("powershell.exe",
            "-NoProfile -Command \"Add-Type -AssemblyName System.Windows.Forms;"
            + " [void][System.Windows.Forms.MessageBox]::Show('EndpointAgents probe test')\"")
        {
            UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized
        })!;
        using var windowless = Process.Start(new ProcessStartInfo("cmd.exe",
            "/d /c ping -n 20 127.0.0.1 >nul")
        {
            UseShellExecute = false, CreateNoWindow = true
        })!;
        try
        {
            var seen = false;
            for (var attempt = 0; attempt < 40 && !seen; attempt++)
            {
                await Task.Delay(250, TestContext.Current.CancellationToken);
                seen = GuiWindowProbe.HasVisibleWindow(dialog.Id);
            }

            Assert.True(seen);
            Assert.False(GuiWindowProbe.HasVisibleWindow(windowless.Id));
        }
        finally
        {
            dialog.Kill(entireProcessTree: true);
            windowless.Kill(entireProcessTree: true);
        }
    }

    // ---- Failure output tail ----

    [Fact]
    public async Task FailedRunKeepsARedactedCappedOutputTail()
    {
        var playbook = V2(Step("Main menu choice:", "5"),
            new BatchPlaybookStep("Never shown", "1", "line", 5));

        var failed = await RunSampleAsync(playbook);
        var completed = await RunSampleAsync(V2(Step("Main menu choice:", "9")));

        Assert.Equal(OfficeBugfixRunStates.PromptTimedOut, failed.State);
        Assert.Contains("Enter the server name:", failed.OutputTail);
        Assert.True(failed.OutputTail!.Length <= OfficeBugfixRunner.OutputTailCharacters);
        Assert.Equal(OfficeBugfixRunStates.Completed, completed.State);
        Assert.Null(completed.OutputTail);
    }

    [Fact]
    public async Task OutputTailNeverContainsKeysOrTokens()
    {
        var root = Path.Combine(Path.GetTempPath(), $"EndpointAgents-tail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "secrets.bat");
        await File.WriteAllTextAsync(path, string.Join("\r\n",
            "@echo off",
            "echo Using key ABCDE-12345-FGHIJ-67890-KLMNO",
            "echo password=Sup3rS3cret",
            "echo Authorization: Bearer abc.def.ghi",
            "echo token " + new string('a', 40),
            "exit /b 5", ""), TestContext.Current.CancellationToken);
        try
        {
            var result = await OfficeBugfixRunner.RunProcessAsync(
                OfficeBugfixRunner.CreateStartInfo(path),
                V2(Step("Prompt", "1", optional: true)),
                TestContext.Current.CancellationToken);

            Assert.Equal(OfficeBugfixRunStates.ProcessFailed, result.State);
            Assert.DoesNotContain("ABCDE-12345", result.OutputTail);
            Assert.DoesNotContain("Sup3rS3cret", result.OutputTail);
            Assert.DoesNotContain("abc.def.ghi", result.OutputTail);
            Assert.DoesNotContain(new string('a', 40), result.OutputTail);
            Assert.Contains("[key-redacted]", result.OutputTail);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- Hash pinning and tampering ----

    [Fact]
    public async Task RunnerRejectsTamperedBatAndTamperedPlaybook()
    {
        const string artifactId = "menu-scenarios-sample";
        var bytes = await File.ReadAllBytesAsync(SamplePath,
            TestContext.Current.CancellationToken);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        var root = Path.Combine(Path.GetTempPath(),
            $"EndpointAgents-v2-{Guid.NewGuid():N}");
        var directory = Path.Combine(root, artifactId, sha256);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{artifactId}.bat");
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        var playbook = V2(Step("Main menu choice:", "9"));
        var descriptor = new OfficeBugfixArtifactDescriptor(artifactId, "Sample",
            "Harmless test", $"{artifactId}.bat", sha256, "16.0", "17.0", playbook,
            BatchPlaybookPolicy.ComputeSha256(playbook),
            $"/api/files/office/bugfix/{artifactId}");
        var runner = new OfficeBugfixRunner(Options.Create(new OfficeRemediationOptions
        {
            StagingRoot = root, RequireProtectedAcl = false, RequireAdministrator = false
        }));
        try
        {
            var approved = await runner.RunAsync(path, descriptor,
                TestContext.Current.CancellationToken);
            Assert.Equal(OfficeBugfixRunStates.Completed, approved.State);

            var changedAnswer = descriptor with
            {
                Playbook = V2(Step("Main menu choice:", "3"))
            };
            await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(path,
                changedAnswer, TestContext.Current.CancellationToken));

            await File.AppendAllTextAsync(path, "echo tampered\r\n",
                TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(path,
                descriptor, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- Policy validation ----

    [Fact]
    public void Version1HashLayoutIsUnchanged()
    {
        var playbook = new BatchPlaybook(1,
        [
            new BatchPlaybookStep("Select an item", "1", "line", 30),
            new BatchPlaybookStep("Continue with harmless sample? [Y/N]", "Y", "key", 30)
        ]);

        // Reference copy of the original version 1 layout; already approved hashes
        // must not change when the policy gains version 2.
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, new System.Text.UTF8Encoding(false),
                   leaveOpen: true))
        {
            writer.Write(1);
            writer.Write(playbook.Steps.Count);
            foreach (var step in playbook.Steps)
            {
                writer.Write(step.Expect);
                writer.Write(step.Send);
                writer.Write(step.Submit);
                writer.Write(step.TimeoutSeconds);
            }
        }

        Assert.Equal(Convert.ToHexString(SHA256.HashData(stream.ToArray())),
            BatchPlaybookPolicy.ComputeSha256(playbook));
    }

    public static TheoryData<BatchPlaybook> InvalidPlaybooks() =>
    [
        new BatchPlaybook(1, [Step("Prompt", "1", optional: true)]),
        new BatchPlaybook(1, [Step("Prompt", "1", maxRepeats: 2)]),
        new BatchPlaybook(1, [new BatchPlaybookStep("Prompt", "1", "line", 30)],
            SuccessExitCodes: [0]),
        new BatchPlaybook(3, [new BatchPlaybookStep("Prompt", "1", "line", 30)]),
        V2(Step("Prompt", "1", maxRepeats: 0)),
        V2(Step("Prompt", "1", maxRepeats: BatchPlaybookPolicy.MaximumRepeats + 1)),
        V2(Step("Prompt", "1", alternatives: new BatchPlaybookChoice("Other",
            "my password", "line"))),
        V2(Step("Prompt", "1", alternatives: new BatchPlaybookChoice("Other",
            "AAAAA-BBBBB-CCCCC-DDDDD-EEEEE", "line"))),
        V2(Step("Prompt", "1", alternatives: new BatchPlaybookChoice(" prompt ",
            "2", "line"))),
        V2(Step("Prompt", "1", alternatives: new BatchPlaybookChoice("Other",
            "12", "key"))),
        V2(Step("Prompt", "1", alternatives: Enumerable.Range(0, 9)
            .Select(index => new BatchPlaybookChoice($"Other {index}", "1", "line"))
            .ToArray())),
        V2(Step("Prompt", "1")) with { SuccessExitCodes = [] },
        V2(Step("Prompt", "1")) with { SuccessExitCodes = [0, 0] },
        V2(Step("Prompt", "1")) with { SuccessMarker = "   " },
        V2(Step("Prompt", "1")) with { FailureMarkers = [new string('x', 129)] },
        V2(Step("Prompt", "1")) with { FailureMarkers = ["FAILED", "failed"] },
        V2(Step("Prompt", "1")) with { GuiDialogGraceSeconds = 4 },
        V2(Step("Prompt", "1")) with { GuiDialogGraceSeconds = 601 },
        new BatchPlaybook(1, [new BatchPlaybookStep("Prompt", "1", "line", 30)],
            GuiDialogGraceSeconds: 60)
    ];

    [Theory]
    [MemberData(nameof(InvalidPlaybooks))]
    public void PolicyRejectsInvalidPlaybooks(BatchPlaybook playbook)
    {
        Assert.False(BatchPlaybookPolicy.TryNormalize(playbook, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Version2HashCoversEveryBehaviouralField()
    {
        var baseline = V2(Step("Prompt", "1", optional: true, maxRepeats: 2,
            alternatives: new BatchPlaybookChoice("Other", "2", "line"))) with
        {
            SuccessExitCodes = [0, 3010], SuccessMarker = "Done",
            FailureMarkers = ["FAILED"]
        };
        var hash = BatchPlaybookPolicy.ComputeSha256(baseline);
        BatchPlaybook[] variants =
        [
            baseline with { Steps = [Step("Prompt", "1", optional: false, maxRepeats: 2,
                alternatives: new BatchPlaybookChoice("Other", "2", "line"))] },
            baseline with { Steps = [Step("Prompt", "1", optional: true, maxRepeats: 3,
                alternatives: new BatchPlaybookChoice("Other", "2", "line"))] },
            baseline with { Steps = [Step("Prompt", "1", optional: true, maxRepeats: 2,
                alternatives: new BatchPlaybookChoice("Other", "3", "line"))] },
            baseline with { SuccessExitCodes = [0] },
            baseline with { SuccessMarker = null },
            baseline with { FailureMarkers = ["ERROR"] },
            baseline with { GuiDialogGraceSeconds = 60 }
        ];

        Assert.All(variants, variant => Assert.NotEqual(hash,
            BatchPlaybookPolicy.ComputeSha256(variant)));
        Assert.Equal(hash, BatchPlaybookPolicy.ComputeSha256(baseline with
        {
            SuccessExitCodes = [3010, 0], SuccessMarker = "  Done "
        }));
    }

    private static string SamplePath => Path.Combine(AppContext.BaseDirectory,
        "menu-scenarios-sample.bat");

    private static Task<OfficeBugfixRunResult> RunSampleAsync(BatchPlaybook playbook,
        params (string Name, string Value)[] environment)
    {
        var startInfo = OfficeBugfixRunner.CreateStartInfo(SamplePath);
        foreach (var (name, value) in environment)
            startInfo.Environment[name] = value;
        return OfficeBugfixRunner.RunProcessAsync(startInfo, playbook,
            TestContext.Current.CancellationToken);
    }
}
