using EndpointAgents.Platform.Windows.Updates;
using EndpointAgents.Shared.Updates;
using EndpointAgents.Updater;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace EndpointAgents.Platform.Windows.Tests;

public sealed class AgentSelfUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "self-update-" + Guid.NewGuid().ToString("N"));

    public AgentSelfUpdateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private (string Zip, AgentUpdateManifest Manifest) MakeZip(Action<ZipArchive>? extra = null, bool lieAboutHash = false)
    {
        var zipPath = Path.Combine(_root, "endpoint-agent-0.3.0.zip");
        var files = new List<AgentUpdateFile>();
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var path in new[] { AgentUpdatePolicy.ServiceExe, AgentUpdatePolicy.TrayExe, AgentUpdatePolicy.UpdaterExe })
            {
                var bytes = Encoding.UTF8.GetBytes(path);
                using (var stream = archive.CreateEntry(path).Open()) stream.Write(bytes);
                var hash = Convert.ToHexString(SHA256.HashData(lieAboutHash ? [1, 2, 3] : bytes));
                files.Add(new AgentUpdateFile(path, hash));
            }
            extra?.Invoke(archive);
        }
        return (zipPath, new AgentUpdateManifest(1, "0.3.0", "endpoint-agent-0.3.0.zip", new string('A', 64),
            new FileInfo(zipPath).Length, 17763, "x64", "", files, null));
    }

    [Fact]
    public void ExtractWritesOnlyListedFilesWithTheirHashes()
    {
        var (zip, manifest) = MakeZip();
        var target = Path.Combine(_root, "versions", "0.3.0");
        AgentUpdateInstaller.Extract(zip, target, manifest);
        Assert.True(File.Exists(Path.Combine(target, "Service", "EndpointAgents.Service.exe")));
        Assert.True(File.Exists(Path.Combine(target, "Updater", "EndpointAgents.Updater.exe")));
    }

    [Fact]
    public void ExtractRefusesExtraFilesEscapesAndChangedFiles()
    {
        var (extraZip, extraManifest) = MakeZip(archive => archive.CreateEntry("Service/extra.dll").Open().Dispose());
        Assert.Throws<InvalidDataException>(() =>
            AgentUpdateInstaller.Extract(extraZip, Path.Combine(_root, "a"), extraManifest));
        File.Delete(extraZip);

        var (escapeZip, escapeManifest) = MakeZip(archive => archive.CreateEntry("../../evil.exe").Open().Dispose());
        Assert.Throws<InvalidDataException>(() =>
            AgentUpdateInstaller.Extract(escapeZip, Path.Combine(_root, "b"), escapeManifest));
        Assert.False(File.Exists(Path.Combine(_root, "..", "evil.exe")));
        File.Delete(escapeZip);

        var (changedZip, changedManifest) = MakeZip(lieAboutHash: true);
        Assert.Throws<InvalidDataException>(() =>
            AgentUpdateInstaller.Extract(changedZip, Path.Combine(_root, "c"), changedManifest));
    }

    [Fact]
    public void SiteSettingsKeepOnlyThisPcsValues()
    {
        var site = JsonNode.Parse(AgentSiteSettings.Extract("""
            {
              // comments are fine
              "Logging": { "LogLevel": { "Default": "Debug" } },
              "ControlPlane": { "HubUrl": "https://cp.example/agent-hub", "Version": "0.1.0",
                                "EnrollmentCode": "ONCE", "CommandTimeoutSeconds": 600 },
              "PackageSigning": { "TrustedPublicKeys": ["KEY"] }
            }
            """))!;
        Assert.Equal("https://cp.example/agent-hub", (string?)site["ControlPlane"]!["HubUrl"]);
        Assert.Null(site["ControlPlane"]!["Version"]);
        Assert.Null(site["ControlPlane"]!["EnrollmentCode"]);
        Assert.Null(site["ControlPlane"]!["CommandTimeoutSeconds"]);
        Assert.Null(site["Logging"]);
        Assert.Equal("KEY", (string?)site["PackageSigning"]!["TrustedPublicKeys"]![0]);
    }

    [Theory]
    [InlineData(@"C:\Program Files\EndpointAgents\Endpoint\Service\EndpointAgents.Service.exe", @"C:\Program Files\EndpointAgents\Endpoint")]
    [InlineData(@"C:\Program Files\EndpointAgents\Endpoint\versions\0.3.0\Service\EndpointAgents.Service.exe", @"C:\Program Files\EndpointAgents\Endpoint")]
    public void TheInstallFolderIsFoundFromTheRunningService(string exe, string root)
    {
        var installer = new AgentUpdateInstaller(null!, Microsoft.Extensions.Options.Options.Create(new AgentUpdateOptions()),
            Microsoft.Extensions.Options.Options.Create(new Packages.PackageSigningOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentUpdateInstaller>.Instance);
        Assert.Equal(root, installer.ResolveInstallRoot(exe), ignoreCase: true);
    }

    private const string ProgramFiles = @"C:\Program Files";
    private const string Root = @"C:\Program Files\EndpointAgents\Endpoint";

    private AgentUpdateState State(string? statePath = null) => new()
    {
        Version = "0.3.0",
        FromVersion = "0.2.0",
        Phase = AgentUpdatePhases.Installing,
        InstallRoot = Root,
        PreviousServiceExe = Root + @"\Service\EndpointAgents.Service.exe",
        PreviousTrayCommand = "\"" + Root + @"\Tray\EndpointAgents.TrayWeb.exe" + "\"",
        NewServiceExe = Root + @"\versions\0.3.0\Service\EndpointAgents.Service.exe",
        NewTrayExe = Root + @"\versions\0.3.0\Tray\EndpointAgents.TrayWeb.exe",
        HealthTimeoutSeconds = 30
    };

    [Fact]
    public void TheUpdaterOnlyActsOnOurOwnFiles()
    {
        Assert.Null(UpdaterPaths.Validate(State(), ProgramFiles));
        Assert.NotNull(UpdaterPaths.Validate(State() with { InstallRoot = @"C:\Users\Public\Endpoint" }, ProgramFiles));
        Assert.NotNull(UpdaterPaths.Validate(State() with { NewServiceExe = @"C:\Windows\System32\cmd.exe" }, ProgramFiles));
        Assert.NotNull(UpdaterPaths.Validate(State() with { NewServiceExe = Root + @"\versions\0.9.0\Service\EndpointAgents.Service.exe" }, ProgramFiles));
        Assert.NotNull(UpdaterPaths.Validate(State() with { PreviousServiceExe = Root + @"\..\..\evil\EndpointAgents.Service.exe" }, ProgramFiles));
        Assert.NotNull(UpdaterPaths.Validate(State() with { ServiceName = "Spooler" }, ProgramFiles));
        Assert.Equal(@"C:\a b\t.exe", UpdaterPaths.ExeFromCommand("\"C:\\a b\\t.exe\" --x"));
    }

    private sealed class FakeMachine(string statePath, bool newVersionConnects) : IMachine
    {
        public string? ImagePath = "\"" + Root + @"\Service\EndpointAgents.Service.exe" + "\"";
        public string? TrayCommand = "\"" + Root + @"\Tray\EndpointAgents.TrayWeb.exe" + "\"";
        public readonly List<string> Calls = [];
        public readonly List<string> Deleted = [];
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UtcNow;

        public bool StopService(string name, TimeSpan timeout) { Calls.Add("stop"); return true; }

        public bool StartService(string name)
        {
            Calls.Add("start " + ImagePath);
            // The new service writes Healthy once it connects to the control plane.
            if (newVersionConnects && ImagePath!.Contains(@"versions\0.3.0"))
                (AgentUpdateState.Read(statePath)! with { Phase = AgentUpdatePhases.Healthy }).Write(statePath);
            return true;
        }

        public string? GetServiceImagePath(string name) => ImagePath;
        public void SetServiceImagePath(string name, string imagePath) => ImagePath = imagePath;
        public string? GetTrayRunCommand() => TrayCommand;
        public void SetTrayRunCommand(string? command) => TrayCommand = command;
        public IReadOnlyCollection<int> StopTrays(string installRoot) { Calls.Add("close tray"); return [1]; }
        public void StartTray(string trayExe, IReadOnlyCollection<int> sessions) => Calls.Add("tray " + trayExe);
        public void DeleteDirectory(string path) => Deleted.Add(path);
        public IReadOnlyList<string> ListDirectories(string path) =>
            [Root + @"\versions\0.1.0", Root + @"\versions\0.3.0"];
        public void Sleep(TimeSpan delay) => UtcNow += delay;
    }

    [Fact]
    public void AHealthyUpdateSwitchesAndStartsTheProbation()
    {
        var statePath = Path.Combine(_root, "state.json");
        State().Write(statePath);
        var machine = new FakeMachine(statePath, newVersionConnects: true);
        using var log = new UpdaterLog(Path.Combine(_root, "updater.log"));

        Assert.Equal(0, new AgentSwitcher(State(), statePath, log, machine).Run());

        var state = AgentUpdateState.Read(statePath)!;
        Assert.Equal(AgentUpdatePhases.Succeeded, state.Phase);
        Assert.NotNull(state.ProbationUntilUtc);
        Assert.Contains(@"versions\0.3.0", machine.ImagePath);
        Assert.Contains(@"versions\0.3.0", machine.TrayCommand);
        Assert.Contains(machine.Calls, call => call.StartsWith("tray ") && call.Contains(@"versions\0.3.0"));
        // An older version goes; the previous (first installed) copy stays for a rollback.
        Assert.Contains(Root + @"\versions\0.1.0", machine.Deleted);
        Assert.DoesNotContain(Root + @"\Service", machine.Deleted);
    }

    [Fact]
    public void AnUpdateThatNeverConnectsIsRolledBackAndNeverRetried()
    {
        var statePath = Path.Combine(_root, "state.json");
        State().Write(statePath);
        var machine = new FakeMachine(statePath, newVersionConnects: false);
        using var log = new UpdaterLog(Path.Combine(_root, "updater.log"));

        Assert.Equal(1, new AgentSwitcher(State(), statePath, log, machine).Run());

        var state = AgentUpdateState.Read(statePath)!;
        Assert.Equal(AgentUpdatePhases.RolledBack, state.Phase);
        Assert.Contains("0.3.0", state.FailedVersions);
        Assert.Contains(@"\Service\EndpointAgents.Service.exe", machine.ImagePath);
        Assert.DoesNotContain("versions", machine.ImagePath);
        Assert.Contains(@"\Tray\EndpointAgents.TrayWeb.exe", machine.TrayCommand);
        Assert.Equal("start " + machine.ImagePath, machine.Calls.Last(call => call.StartsWith("start ")));
        Assert.Contains(Root + @"\versions\0.3.0", machine.Deleted);
    }

    [Fact]
    public void AVersionThatKeepsCrashingGoesBack()
    {
        var statePath = Path.Combine(_root, "state.json");
        var crashing = State() with { Phase = AgentUpdatePhases.RollingBack, Message = "It crashed 3 times." };
        crashing.Write(statePath);
        var machine = new FakeMachine(statePath, newVersionConnects: true)
        {
            ImagePath = "\"" + Root + @"\versions\0.3.0\Service\EndpointAgents.Service.exe" + "\""
        };
        using var log = new UpdaterLog(Path.Combine(_root, "updater.log"));

        new AgentSwitcher(crashing, statePath, log, machine).RollBackAfterCrashes();

        var state = AgentUpdateState.Read(statePath)!;
        Assert.Equal(AgentUpdatePhases.RolledBack, state.Phase);
        Assert.Contains("0.3.0", state.FailedVersions);
        Assert.DoesNotContain("versions", machine.ImagePath);
    }

    [Fact]
    public void OnlyUnexpectedStopsCountDuringTheProbation()
    {
        var statePath = Path.Combine(_root, "state.json");
        var now = DateTimeOffset.UtcNow;
        (State() with { Phase = AgentUpdatePhases.Succeeded, ProbationUntilUtc = now.AddHours(24), CleanStop = true })
            .Write(statePath);

        // A normal start, a normal stop (a reboot), another start: no crash.
        Assert.False(AgentUpdateProbation.OnServiceStart(statePath, "0.3.0", now));
        AgentUpdateProbation.OnServiceStop(statePath);
        Assert.False(AgentUpdateProbation.OnServiceStart(statePath, "0.3.0", now));
        Assert.Equal(0, AgentUpdateState.Read(statePath)!.CrashesDuringProbation);

        // Two starts without a stop in between: one crash.
        Assert.False(AgentUpdateProbation.OnServiceStart(statePath, "0.3.0", now));
        Assert.Equal(1, AgentUpdateState.Read(statePath)!.CrashesDuringProbation);

        // After the probation nothing counts any more.
        Assert.False(AgentUpdateProbation.OnServiceStart(statePath, "0.3.0", now.AddHours(25)));
        Assert.Equal(1, AgentUpdateState.Read(statePath)!.CrashesDuringProbation);
    }

    [Fact]
    public void APowerCutIsNotCountedAsACrash()
    {
        var statePath = Path.Combine(_root, "state.json");
        var now = DateTimeOffset.UtcNow;
        (State() with { Phase = AgentUpdatePhases.Succeeded, ProbationUntilUtc = now.AddHours(24), CleanStop = true })
            .Write(statePath);
        Assert.False(AgentUpdateProbation.OnServiceStart(statePath, "0.3.0", now, bootedAtUtc: now.AddHours(-5)));

        // No normal stop, but Windows itself started after the last service start: the PC went down.
        Assert.False(AgentUpdateProbation.OnServiceStart(statePath, "0.3.0", now.AddMinutes(30),
            bootedAtUtc: now.AddMinutes(29)));
        Assert.Equal(0, AgentUpdateState.Read(statePath)!.CrashesDuringProbation);

        // No normal stop and Windows kept running: the service crashed.
        Assert.False(AgentUpdateProbation.OnServiceStart(statePath, "0.3.0", now.AddMinutes(40),
            bootedAtUtc: now.AddMinutes(29)));
        Assert.Equal(1, AgentUpdateState.Read(statePath)!.CrashesDuringProbation);
    }

    [Fact]
    public void AnUpdateTheUpdaterNeverFinishedIsFinishedByTheService()
    {
        var statePath = Path.Combine(_root, "state.json");
        var started = DateTimeOffset.UtcNow;
        (State() with { Phase = AgentUpdatePhases.AwaitingHealth, StartedAtUtc = started }).Write(statePath);

        // While the updater can still be working, the service leaves the update alone.
        Assert.Equal(AgentUpdateProbation.OrphanOutcome.None,
            AgentUpdateProbation.RecoverOrphaned(statePath, "0.3.0", started.AddMinutes(5), connected: true));
        // Another version is running: not this service's update to finish.
        Assert.Equal(AgentUpdateProbation.OrphanOutcome.None,
            AgentUpdateProbation.RecoverOrphaned(statePath, "0.2.0", started.AddHours(2), connected: true));
        // Never connected and no updater on disk to roll back with: nothing is changed.
        Assert.Equal(AgentUpdateProbation.OrphanOutcome.None,
            AgentUpdateProbation.RecoverOrphaned(statePath, "0.3.0", started.AddHours(2), connected: false));
        Assert.Equal(AgentUpdatePhases.AwaitingHealth, AgentUpdateState.Read(statePath)!.Phase);

        // Long past the updater's deadline and connected: kept, reported, and watched for a day.
        var later = started.AddHours(2);
        Assert.Equal(AgentUpdateProbation.OrphanOutcome.Adopted,
            AgentUpdateProbation.RecoverOrphaned(statePath, "0.3.0", later, connected: true));
        var state = AgentUpdateState.Read(statePath)!;
        Assert.Equal(AgentUpdatePhases.Succeeded, state.Phase);
        Assert.Equal(later + AgentUpdateProbation.Probation, state.ProbationUntilUtc);
        Assert.False(state.Reported);
        Assert.Equal(AgentUpdateProbation.OrphanOutcome.None,
            AgentUpdateProbation.RecoverOrphaned(statePath, "0.3.0", later.AddMinutes(1), connected: true));
    }

    [Fact]
    public void CrashesOfAnUnfinishedUpdateAreCountedToo()
    {
        var statePath = Path.Combine(_root, "state.json");
        var started = DateTimeOffset.UtcNow;
        (State() with { Phase = AgentUpdatePhases.AwaitingHealth, StartedAtUtc = started, CleanStop = false })
            .Write(statePath);

        Assert.False(AgentUpdateProbation.OnServiceStart(statePath, "0.3.0", started.AddMinutes(1)));
        Assert.Equal(0, AgentUpdateState.Read(statePath)!.CrashesDuringProbation); // the updater still watches
        Assert.False(AgentUpdateProbation.OnServiceStart(statePath, "0.3.0", started.AddHours(1)));
        Assert.Equal(1, AgentUpdateState.Read(statePath)!.CrashesDuringProbation);
    }
}
