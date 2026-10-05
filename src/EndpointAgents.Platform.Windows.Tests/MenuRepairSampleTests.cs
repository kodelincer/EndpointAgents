using EndpointAgents.Platform.Windows.Office;
using EndpointAgents.Shared.Office;
using System.Text.Json;

namespace EndpointAgents.Platform.Windows.Tests;

/// <summary>
/// Runs deploy/samples/menu-repair-sample.bat, a print-only mock repair tool with
/// nested menus, through the real runner using its checked-in playbook.
/// </summary>
public sealed class MenuRepairSampleTests
{
    private static string SamplePath => Path.Combine(AppContext.BaseDirectory,
        "menu-repair-sample.bat");

    [Fact]
    public async Task PlaybookWalksTheWholeMenuFlow()
    {
        var result = await RunAsync(LoadPlaybook());

        Assert.True(result.State == OfficeBugfixRunStates.Completed, result.ToString());
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(6, result.CompletedStepCount);
    }

    [Fact]
    public async Task SimulatedErrorStopsAtTheFailureMarker()
    {
        var result = await RunAsync(LoadPlaybook(), [("SAMPLE_FAIL", "1")]);

        Assert.Equal(OfficeBugfixRunStates.FailureMarkerDetected, result.State);
        Assert.Contains("Repair failed", result.OutputTail);
    }

    [Fact]
    public async Task SelfClosingDialogDoesNotBlockTheRun()
    {
        // The real message box closes itself after 3 s; the probe reports a window
        // for 3 s too, well inside the default 15 s grace period.
        var started = DateTime.UtcNow;

        var result = await RunAsync(LoadPlaybook(), [("SAMPLE_DIALOG", "1")],
            _ => DateTime.UtcNow - started < TimeSpan.FromSeconds(3));

        Assert.True(result.State == OfficeBugfixRunStates.Completed, result.ToString());
    }

    [Fact]
    public async Task DialogStayingPastTheGraceTimeBlocksTheRun()
    {
        // The playbook stops answering at the sub-menu, as a BAT waiting behind a
        // dialog would, so the window outlives the 5 s grace time.
        var playbook = LoadPlaybook() with
        {
            Steps = [LoadPlaybook().Steps[0]],
            GuiDialogGraceSeconds = 5
        };

        var result = await RunAsync(playbook with
        {
            Steps = [.. playbook.Steps, new BatchPlaybookStep(
                "Never shown", "1", "line", 60)]
        }, [], _ => true);

        Assert.Equal(OfficeBugfixRunStates.GuiDialogBlocked, result.State);
        Assert.Equal(1, result.FailedStepIndex);
    }

    [Fact]
    public void SampleIsLintCleanAndNotInAnyCatalog()
    {
        var lint = BatScriptLint.Inspect(File.ReadAllText(SamplePath));
        Assert.Empty(lint.Errors);
        Assert.Empty(lint.Warnings);

        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "EndpointAgents.slnx")))
            root = Path.GetDirectoryName(root)!;
        foreach (var manifest in Directory.EnumerateFiles(
                     Path.Combine(root, "deploy", "control-plane"), "*.json",
                     SearchOption.AllDirectories))
            Assert.DoesNotContain("menu-repair", File.ReadAllText(manifest),
                StringComparison.OrdinalIgnoreCase);
    }

    private static BatchPlaybook LoadPlaybook() =>
        JsonSerializer.Deserialize<BatchPlaybook>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                "menu-repair-sample.playbook.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private static Task<OfficeBugfixRunResult> RunAsync(BatchPlaybook playbook,
        (string Name, string Value)[]? environment = null,
        Func<int, bool>? probe = null)
    {
        var startInfo = OfficeBugfixRunner.CreateStartInfo(SamplePath);
        foreach (var (name, value) in environment ?? [])
            startInfo.Environment[name] = value;
        return OfficeBugfixRunner.RunProcessAsync(startInfo, playbook,
            TestContext.Current.CancellationToken, guiWindowProbe: probe);
    }
}
