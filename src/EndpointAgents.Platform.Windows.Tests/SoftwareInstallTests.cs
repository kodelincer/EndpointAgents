using EndpointAgents.Platform.Windows.Software;
using EndpointAgents.Shared.Office;
using EndpointAgents.Shared.Packages;
using System.IO.Compression;

namespace EndpointAgents.Platform.Windows.Tests;

public sealed class SoftwareInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "software-endpoint-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeFacts : IMachineFacts
    {
        public int WindowsBuild { get; init; } = 19045;
        public string EditionId { get; init; } = "Professional";
        public string Architecture { get; init; } = "x64";
        public long MemoryMb { get; init; } = 8000;
        public long FreeSystemDiskMb { get; init; } = 50_000;
        public int CpuCores { get; init; } = 4;
        public (int Width, int Height)? Screen { get; init; } = (1920, 1080);
        public bool DomainJoined { get; init; } = true;
        public HashSet<string> Runtimes { get; init; } = [];
        public HashSet<string> Installed { get; init; } = [];
        public bool HasRuntime(string runtimeId) => Runtimes.Contains(runtimeId);
        public bool IsDetected(DetectionRule rule) => Installed.Contains(rule.DisplayName ?? rule.ProductCode ?? "");
    }

    private static PackageDescriptor Package(string id, SoftwareSpec spec) => new(id, id, "test",
        PackageFileTypes.Exe, id + ".exe", new string('A', 64), PackageRoutes.Download(id),
        new BatchPlaybook(3, []), new string('B', 64), [], null, null, null, "CN=Vendor", [],
        Software: spec);

    [Fact]
    public void APcThatMeetsEveryRulePasses()
    {
        var app = Package("app-install", new SoftwareSpec(Requirements: new SoftwareRequirements(
            MinWindowsBuild: 19041, Editions: ["Professional", "Enterprise"], Architectures: ["x64"],
            MinMemoryMb: 8192, MinFreeDiskMb: 4096, MinCpuCores: 2, MinScreenWidth: 1366, MinScreenHeight: 768,
            DomainJoined: true)));
        // 8000 MB reported for 8 GB installed still counts as 8 GB.
        var report = RequirementChecker.Check(app, new FakeFacts(), [app]);
        Assert.True(report.Met, string.Join(" ", report.Problems));
        Assert.False(report.AlreadyInstalled);
    }

    [Fact]
    public void EachFailedRuleSaysWhyInPlainWords()
    {
        var app = Package("app-install", new SoftwareSpec(Requirements: new SoftwareRequirements(
            MinMemoryMb: 16384, MinFreeDiskMb: 100_000, Editions: ["Enterprise"])));
        var report = RequirementChecker.Check(app, new FakeFacts { MemoryMb = 4096 }, [app]);

        Assert.False(report.Met);
        Assert.Contains("This PC has 4 GB of memory; the software needs 16 GB.", report.Problems);
        Assert.Contains(report.Problems, problem => problem.StartsWith("Drive C: has 48.8 GB free"));
        Assert.Contains("This PC has Windows Professional; the software needs Enterprise.", report.Problems);
    }

    [Fact]
    public void AMissingRuntimeIsInstalledFirstWhenTheCatalogHasIt()
    {
        var runtime = Package("vcredist-install", new SoftwareSpec(Provides: "vcredist-2015-x64",
            Install: new SoftwareInstall(InstallerKinds.WixBurn), Detection: [new DetectionRule(DetectionKinds.UninstallEntry, DisplayName: "VC")]));
        var app = Package("app-install", new SoftwareSpec(Requirements: new SoftwareRequirements(
            Runtimes: ["vcredist-2015-x64", "webview2"])));

        var withRuntime = RequirementChecker.Check(app, new FakeFacts { Runtimes = ["webview2"] }, [app, runtime]);
        Assert.True(withRuntime.Met);
        Assert.Equal(["vcredist-install"], withRuntime.InstallFirst);
        Assert.Equal(["vcredist-2015-x64"], withRuntime.MissingRuntimes);

        var without = RequirementChecker.Check(app, new FakeFacts(), [app]);
        Assert.False(without.Met);
        Assert.Contains(without.Problems, problem => problem.Contains("the catalog has no package for it"));
    }

    [Fact]
    public void ConflictsAndDetectionAreReported()
    {
        var app = Package("app-install", new SoftwareSpec(
            Requirements: new SoftwareRequirements(MustNotBeInstalled: [new DetectionRule(DetectionKinds.UninstallEntry, DisplayName: "Old App")]),
            Detection: [new DetectionRule(DetectionKinds.UninstallEntry, DisplayName: "New App")]));
        var facts = new FakeFacts { Installed = ["Old App", "New App"] };
        var report = RequirementChecker.Check(app, facts, [app]);
        Assert.False(report.Met);
        Assert.True(report.AlreadyInstalled);
        Assert.Contains("Old App is installed and must be removed first.", report.Problems);
    }

    [Fact]
    public void AnMsiCommandLineComesOnlyFromTheSignedSection()
    {
        var install = new SoftwareInstall(InstallerKinds.Msi, MsiProperties: new SortedDictionary<string, string>
            { ["ALLUSERS"] = "1", ["INSTALLDIR"] = @"C:\Program Files\App" });
        var start = SoftwareInstaller.BuildStartInfo(@"C:\Staging\app-install.msi", install, @"C:\Windows\System32")!;
        Assert.Equal(Path.Combine(@"C:\Windows\System32", "msiexec.exe"), start.FileName);
        Assert.Equal("/i \"C:\\Staging\\app-install.msi\" /qn /norestart ALLUSERS=\"1\" INSTALLDIR=\"C:\\Program Files\\App\"", start.Arguments);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
    }

    [Theory]
    [InlineData(InstallerKinds.Inno, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-")]
    [InlineData(InstallerKinds.Nsis, "/S")]
    [InlineData(InstallerKinds.WixBurn, "/quiet /norestart")]
    [InlineData(InstallerKinds.InstallShield, "/s /v\"/qn /norestart\"")]
    [InlineData(InstallerKinds.Squirrel, "--silent")]
    public void KnownInstallerKindsGetTheirSilentSwitches(string kind, string expected)
    {
        var start = SoftwareInstaller.BuildStartInfo(@"C:\Staging\app.exe", new SoftwareInstall(kind), @"C:\Windows\System32")!;
        Assert.Equal(@"C:\Staging\app.exe", start.FileName);
        Assert.Equal(expected, start.Arguments);
    }

    [Fact]
    public void ReturnCodesMapToResults()
    {
        var install = new SoftwareInstall(InstallerKinds.Msi, ReturnCodes: new InstallReturnCodes([0], [3010], [1618]));
        Assert.Equal((SoftwareRunStates.Installed, false), SoftwareInstaller.MapExitCode(install, 0));
        Assert.Equal((SoftwareRunStates.Installed, true), SoftwareInstaller.MapExitCode(install, 3010));
        Assert.Equal((SoftwareRunStates.RetryLater, false), SoftwareInstaller.MapExitCode(install, 1618));
        Assert.Equal((SoftwareRunStates.InstallerFailed, false), SoftwareInstaller.MapExitCode(install, 1603));
        Assert.Equal((SoftwareRunStates.InstallerFailed, false), SoftwareInstaller.MapExitCode(install, null));
    }

    [Fact]
    public void CopyFilesKeepsFontsAndRefusesPathsOutsideTheTarget()
    {
        Directory.CreateDirectory(_root);
        var zip = Path.Combine(_root, "fonts.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            Write(archive, "Vazirmatn/Vazirmatn-Regular.ttf");
            Write(archive, "readme.txt");
        }
        var target = Path.Combine(_root, "Fonts");
        var result = SoftwareInstaller.CopyFiles(zip, new SoftwareInstall(InstallerKinds.CopyFiles, CopyTarget: CopyTargets.Fonts),
            target, registerFonts: false);
        Assert.Equal(SoftwareRunStates.Installed, result.State);
        Assert.True(result.RestartRequired); // fonts show after signing out and in
        Assert.True(File.Exists(Path.Combine(target, "Vazirmatn-Regular.ttf")));
        Assert.False(File.Exists(Path.Combine(target, "readme.txt")));

        var evil = Path.Combine(_root, "evil.zip");
        using (var archive = ZipFile.Open(evil, ZipArchiveMode.Create))
            Write(archive, "../outside.txt");
        Assert.Throws<InvalidDataException>(() => SoftwareInstaller.CopyFiles(evil,
            new SoftwareInstall(InstallerKinds.CopyFiles, CopyTarget: CopyTargets.ProgramFiles, CopyFolder: "App"),
            Path.Combine(_root, "App"), registerFonts: false));
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
    }

    [Theory]
    [InlineData("120.0.6099.71", "120.0", true)]
    [InlineData("119.9", "120.0", false)]
    [InlineData(null, "1.0", false)]
    [InlineData("anything", null, true)]
    public void VersionsCompareWithMissingParts(string? actual, string? minimum, bool expected) =>
        Assert.Equal(expected, WindowsMachineFacts.AtLeast(actual, minimum));

    private static void Write(ZipArchive archive, string name)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write("data");
    }
}
