using ControlPlane.Api.Chat;
using ControlPlane.Api.Files;
using ControlPlane.Api.Software;
using EndpointAgents.Shared.Office;
using EndpointAgents.Shared.Packages;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ControlPlane.Api.Tests;

public sealed class SoftwareInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "software-install-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static readonly DetectionRule Chrome = new(DetectionKinds.UninstallEntry, DisplayName: "Google Chrome");

    [Fact]
    public void AnMsiSpecIsNormalizedWithDefaultReturnCodes()
    {
        var spec = new SoftwareSpec(" 7-Zip ", "24.08", Install: new SoftwareInstall("MSI",
                MsiProperties: new Dictionary<string, string> { ["ALLUSERS"] = "1", ["INSTALLDIR"] = @"C:\Program Files\7-Zip" }),
            Detection: [new DetectionRule("msi-product", ProductCode: "{23170f69-40c1-2702-2408-000001000000}")]);

        Assert.True(SoftwareSpecPolicy.TryNormalize(spec, PackageFileTypes.Msi, out var normalized, out var error), error);
        Assert.Equal("7-Zip", normalized!.Name);
        Assert.Equal(InstallerKinds.Msi, normalized.Install!.Kind);
        Assert.Equal([0], normalized.Install.ReturnCodes!.Success);
        Assert.Equal([3010, 1641], normalized.Install.ReturnCodes.Restart);
        Assert.Equal([1618], normalized.Install.ReturnCodes.Retry);
        Assert.Equal("{23170F69-40C1-2702-2408-000001000000}", normalized.Detection![0].ProductCode);
        // The same section always hashes the same, so its signature is stable.
        Assert.True(SoftwareSpecPolicy.TryNormalize(normalized, PackageFileTypes.Msi, out var again, out _));
        Assert.Equal(SoftwareSpecPolicy.ComputeSha256(normalized), SoftwareSpecPolicy.ComputeSha256(again!));
    }

    [Theory]
    [InlineData("msi", "exe", "installs from a .msi file")]
    [InlineData("exe", "exe", "silent-install switches")]
    [InlineData("copy-files", "zip", "copyTarget")]
    [InlineData("manual", "exe", "manualReason")]
    [InlineData("wizard", "exe", "must be one of")]
    public void BadInstallSectionsAreRefused(string kind, string fileType, string expected)
    {
        var spec = new SoftwareSpec(Install: new SoftwareInstall(kind), Detection: [Chrome]);
        Assert.False(SoftwareSpecPolicy.TryNormalize(spec, fileType, out _, out var error));
        Assert.Contains(expected, error);
    }

    [Theory]
    [InlineData("/S \"&calc")]
    [InlineData("/D=C:\\x\" & del")]
    public void QuotesInSwitchesAreRefused(string argument)
    {
        var spec = new SoftwareSpec(Install: new SoftwareInstall(InstallerKinds.Nsis, Arguments: [argument]), Detection: [Chrome]);
        Assert.False(SoftwareSpecPolicy.TryNormalize(spec, PackageFileTypes.Exe, out _, out var error));
        Assert.Contains("quotes", error);
    }

    [Fact]
    public void TypedInstallsNeedADetectionRule()
    {
        var spec = new SoftwareSpec(Install: new SoftwareInstall(InstallerKinds.Inno));
        Assert.False(SoftwareSpecPolicy.TryNormalize(spec, PackageFileTypes.Exe, out _, out var error));
        Assert.Contains("detection", error);
    }

    [Theory]
    [InlineData(@"..\Windows\System32\cmd.exe")]
    [InlineData(@"C:\Windows\notepad.exe")]
    [InlineData(@"Foo\*.exe")]
    public void FileDetectionStaysUnderItsRoot(string path)
    {
        var spec = new SoftwareSpec(Detection: [new DetectionRule(DetectionKinds.File, Root: PathRoots.ProgramFiles, Path: path)]);
        Assert.False(SoftwareSpecPolicy.TryNormalize(spec, PackageFileTypes.Bat, out _, out _));
    }

    [Fact]
    public void RequirementsMustUseKnownValues()
    {
        Assert.False(SoftwareSpecPolicy.TryNormalize(new SoftwareSpec(Requirements: new SoftwareRequirements(Runtimes: ["python-3"])),
            PackageFileTypes.Bat, out _, out var runtimeError));
        Assert.Contains("runtimes", runtimeError);
        Assert.False(SoftwareSpecPolicy.TryNormalize(new SoftwareSpec(Requirements: new SoftwareRequirements(MinMemoryMb: 10)),
            PackageFileTypes.Bat, out _, out var memoryError));
        Assert.Contains("minMemoryMb", memoryError);
        Assert.True(SoftwareSpecPolicy.TryNormalize(new SoftwareSpec(Requirements: new SoftwareRequirements(
                MinWindowsBuild: 19045, Architectures: ["X64"], MinMemoryMb: 8192, Runtimes: ["vcredist-2015-x64"])),
            PackageFileTypes.Bat, out var ok, out _));
        Assert.Equal(["x64"], ok!.Requirements!.Architectures);
    }

    [Fact]
    public void TheSoftwareHashIsPartOfTheSignature()
    {
        var plain = PackageSignaturePolicy.BuildStatement("app", "app.msi", new string('A', 64), new string('B', 64), null, "CN=Vendor");
        var withSoftware = PackageSignaturePolicy.BuildStatement("app", "app.msi", new string('A', 64), new string('B', 64), null,
            "CN=Vendor", softwareSha256: new string('c', 64));
        Assert.DoesNotContain("software:", plain); // older packages keep their signatures
        Assert.EndsWith("software:" + new string('C', 64), withSoftware);
    }

    [Fact]
    public async Task ABatInstallPackageCarriesItsSignedRequirements()
    {
        var requirements = new JsonObject { ["minMemoryMb"] = 4096, ["minFreeDiskMb"] = 2048 };
        var folder = await WriteBatPackageAsync("chrome-install", "install-chrome", new JsonObject
        {
            ["name"] = "Google Chrome",
            ["requirements"] = requirements,
            ["detection"] = new JsonArray(new JsonObject { ["kind"] = "uninstall-entry", ["displayName"] = "Google Chrome" })
        });
        var repository = Repository();
        var package = Assert.Single(await repository.GetRepairPackagesAsync(TestContext.Current.CancellationToken));

        Assert.True(package.IsInstall);
        Assert.Equal(4096, package.Descriptor.Software!.Requirements!.MinMemoryMb);
        Assert.NotNull(package.Descriptor.SoftwareSha256);
        Assert.True(PackageDescriptorPolicy.TryValidate(package.Descriptor, out _));
        Assert.True(PackageSignaturePolicy.Verify(package.Descriptor, [TestPackageSigner.PublicKey]));

        // Changing a requirement after signing unpublishes the package.
        var path = Path.Combine(folder, "package.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!.AsObject();
        json["software"]!["requirements"]!["minMemoryMb"] = 512;
        await File.WriteAllTextAsync(path, json.ToJsonString(), TestContext.Current.CancellationToken);
        Assert.Empty(await Repository().GetRepairPackagesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AManualInstallIsPublishedButNeverRunByThePc()
    {
        await WriteBatPackageAsync("tool-install", "install-tool", new JsonObject
        {
            ["install"] = new JsonObject { ["kind"] = "manual", ["manualReason"] = "The vendor wizard has no silent mode." }
        });
        var status = Assert.Single(await Repository().GetPackageStatusesAsync(TestContext.Current.CancellationToken));
        // Manual installs keep the BAT and playbook; they are never run by the PC.
        Assert.True(BatPackageStatuses.IsPublished(status.Status), string.Join(" ", status.Reasons));
        Assert.Equal(InstallerKinds.Manual, status.Software!.Install!.Kind);
    }

    [Fact]
    public async Task LicensedSoftwareAlwaysNeedsApproval()
    {
        await WriteBatPackageAsync("editor-install", "install-editor", new JsonObject { ["licensed"] = true });
        var package = Assert.Single(await Repository().GetRepairPackagesAsync(TestContext.Current.CancellationToken));
        var store = await StoreAsync([]);

        Assert.True(package.RequiresApproval);
        Assert.Equal("Licensed software always needs approval.", SoftwareApprovalStore.CannotSkipApproval(package));
        Assert.True(await store.NeedsApprovalAsync(package, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InstallsNeedApprovalUnlessOnTheList()
    {
        await WriteBatPackageAsync("chrome-install", "install-chrome", null);
        await WriteBatPackageAsync("spooler-reset", "printing-stuck", null);
        var packages = await Repository().GetRepairPackagesAsync(TestContext.Current.CancellationToken);
        var chrome = packages.Single(item => item.Descriptor.PackageId == "chrome-install");
        var repair = packages.Single(item => item.Descriptor.PackageId == "spooler-reset");
        var store = await StoreAsync(["chrome-install"]);
        var ct = TestContext.Current.CancellationToken;

        // Seeded once from the defaults: what installed without approval before still does.
        Assert.False(await store.NeedsApprovalAsync(chrome, ct));
        Assert.False(await store.NeedsApprovalAsync(repair, ct)); // repairs are not installs
        await store.SetAsync("chrome-install", false, "admin", ct);
        Assert.True(await store.NeedsApprovalAsync(chrome, ct));

        // Removing a default stays removed after a restart (the seed runs only once).
        await store.InitializeAsync(ct);
        Assert.True(await store.NeedsApprovalAsync(chrome, ct));
        Assert.Equal("Only install packages can be on the list.", SoftwareApprovalStore.CannotSkipApproval(repair));
    }

    private async Task<SoftwareApprovalStore> StoreAsync(List<string> defaults)
    {
        var store = new SoftwareApprovalStore(
            Options.Create(new ChatOptions { DatabasePath = Path.Combine(_root, "cp.db") }),
            Options.Create(new SoftwareInstallOptions { DefaultNoApproval = defaults }));
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        return store;
    }

    private FileArtifactRepository Repository() => new(
        Options.Create(new FileRepositoryOptions
        {
            RootPath = _root, RequireProtectedAcl = false,
            TrustedPackageSigningKeys = [TestPackageSigner.PublicKey]
        }),
        new HostingEnvironment { ContentRootPath = _root });

    private async Task<string> WriteBatPackageAsync(string id, string problem, JsonObject? software)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "packages", id)).FullName;
        var bytes = Encoding.ASCII.GetBytes("@echo off\r\necho done\r\nexit /b 0\r\n");
        await File.WriteAllBytesAsync(Path.Combine(folder, "install.bat"), bytes, TestContext.Current.CancellationToken);
        var playbook = new BatchPlaybook(3, [], TimeoutSeconds: 120);
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var package = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["id"] = id,
            ["displayName"] = "Install an app",
            ["description"] = "Installs an app.",
            ["problem"] = problem,
            ["file"] = new JsonObject { ["type"] = "bat", ["fileName"] = "install.bat", ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)) },
            ["symptoms"] = new JsonArray("I need the app"),
            ["playbook"] = JsonSerializer.SerializeToNode(playbook, web),
            ["playbookSha256"] = BatchPlaybookPolicy.ComputeSha256(playbook)
        };
        if (software is not null) package["software"] = software;
        TestPackageSigner.Sign(package, folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "package.json"), package.ToJsonString(), TestContext.Current.CancellationToken);
        return folder;
    }
}
