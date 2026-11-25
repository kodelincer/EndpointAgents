using ControlPlane.Api.Files;
using ControlPlane.Api.Files.Builder;
using EndpointAgents.Shared.Office;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;
using EndpointAgents.Shared.Packages;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ControlPlane.Api.Tests;

public sealed class BatMenuScannerTests
{
    private const string MenuBat = """
        @echo off
        :main
        cls
        echo ============== MAIN MENU ==============
        echo [1] Office repair
        echo [2] Windows repair
        echo [6] Exit
        echo =======================================
        set /p choice=Select an option [1-6]:
        if "%choice%"=="1" goto office
        if /i "%choice%" EQU "2" goto windows
        if %choice%==6 goto end
        goto main
        :office
        echo 1. Activate Office
        echo 2. Back
        set /p "sub=Choose: "
        choice /c YN /m "Continue with activation"
        if errorlevel 2 goto main
        if errorlevel 1 goto activate
        :activate
        echo Done.
        pause
        echo Please wait for %computername%
        set /p ok=Ready on %computername% to restart?
        echo Restarting now
        pause >nul
        set /p =Working... <nul
        :end
        """;

    [Fact]
    public void FindsMenusQuestionsChoicesAndPauses()
    {
        var screens = BatMenuScanner.Scan(MenuBat);

        Assert.Equal(["menu", "menu", "choice", "pause", "question", "pause"],
            screens.Select(screen => screen.Kind));
        var main = screens[0];
        Assert.Equal("Select an option [1-6]:", main.Expect);
        Assert.Equal(BatchPlaybookSubmitKinds.Line, main.Submit);
        Assert.Equal("main", main.Section);
        Assert.Equal(9, main.Line);
        Assert.Equal(["1", "2", "6"], main.Options.Select(option => option.Value));
        Assert.Equal("Office repair", main.Options[0].Label);
        Assert.Equal("office", main.Options[0].GoesTo);
        Assert.Equal("windows", main.Options[1].GoesTo);
        Assert.Equal("end", main.Options[2].GoesTo);
        Assert.Contains("[1] Office repair", main.Shown);
    }

    [Fact]
    public void ReadsQuotedPromptsChoiceListsAndErrorLevels()
    {
        var screens = BatMenuScanner.Scan(MenuBat);

        Assert.Equal("Choose:", screens[1].Expect);
        Assert.Equal(["1", "2"], screens[1].Options.Select(option => option.Value));
        var choice = screens[2];
        Assert.Equal("Continue with activation [Y,N]?", choice.Expect);
        Assert.Equal(BatchPlaybookSubmitKinds.Key, choice.Submit);
        Assert.Equal(["Y", "N"], choice.Options.Select(option => option.Value));
        Assert.Equal("activate", choice.Options[0].GoesTo);
        Assert.Equal("main", choice.Options[1].GoesTo);
        Assert.Equal("Press any key to continue", screens[3].Expect);
    }

    [Fact]
    public void SuggestsOnlyTheFixedPartOfAPromptWithAVariable()
    {
        var screens = BatMenuScanner.Scan(MenuBat);

        var question = screens[4];
        Assert.Equal("to restart?", question.Expect);
        Assert.Contains(question.Notes, note => note.Contains("variable"));
        // pause >nul shows nothing itself; the last printed line is suggested instead.
        Assert.Equal("Restarting now", screens[5].Expect);
    }

    [Fact]
    public void SkipsCommentsAndEchoesSentToNul()
    {
        var screens = BatMenuScanner.Scan("""
            rem set /p x=Not a prompt:
            :: choice /c AB
            echo hidden > nul
            echo Visible line
            set /p x=
            """);

        var screen = Assert.Single(screens);
        Assert.Equal("Visible line", screen.Expect);
        Assert.Equal(["Visible line"], screen.Shown);
    }
}

public sealed class PackageBuilderTests : IDisposable
{
    private const string Bat = "@echo off\r\necho [1] Activate\r\nset /p \"x=Select an option: \"\r\necho Product activation successful\r\nexit /b 0\r\n";
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        $"EndpointAgents-builder-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private FileArtifactRepository CreateRepository(FakeApprovalKey? approvalKey = null) => new(
        Options.Create(new FileRepositoryOptions
        {
            RootPath = _root, RequireProtectedAcl = false,
            TrustedPackageSigningKeys = [TestPackageSigner.PublicKey],
            PackageOnlySigningKeys = approvalKey is null ? [] : [approvalKey.PublicKey!]
        }),
        new HostingEnvironment { ContentRootPath = _root });

    private PackageBuilder CreateBuilder(FakeApprovalKey? key = null) => new(
        CreateRepository(key), TimeProvider.System, key, CreateUploads());

    private InstallerUploads CreateUploads(long maximumBytes = 500L * 1024 * 1024) => new(CreateRepository(),
        Options.Create(new FileRepositoryOptions { RootPath = _root, MaxFileSizeBytes = maximumBytes }),
        TimeProvider.System);

    // dotnet.exe carries an embedded vendor (Authenticode) signature, like a real installer;
    // hostname.exe is catalog-signed only, so it counts as unsigned.
    private static readonly string SignedExe = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
    private static readonly string UnsignedExe = Path.Combine(Environment.SystemDirectory, "hostname.exe");

    private async Task<InstallerInfo> UploadInstallerAsync(string path = "", string name = "dotnet setup.exe")
    {
        await using var stream = File.OpenRead(path.Length == 0 ? SignedExe : path);
        var (info, error) = await CreateUploads().SaveAsync(stream, name, TestContext.Current.CancellationToken);
        Assert.True(info is not null, error);
        return info!;
    }

    private static PackageBuildRequest InstallRequest(string? uploadId, bool replace = false,
        string id = "dotnet-install", string kind = "exe") => new(
        id, "install", "Install .NET", "Installs the .NET host for all users.",
        new BatchPlaybook(3, []), Replace: replace, UploadId: uploadId,
        Symptoms: ["دات‌نت نصب نیست"],
        Software: new SoftwareSpec("Microsoft .NET", Install: new SoftwareInstall(kind, Arguments: kind == "exe" ? ["/quiet"] : null, TimeoutSeconds: 600),
            Detection: [new DetectionRule("uninstall-entry", DisplayName: "Microsoft .NET")],
            Requirements: new SoftwareRequirements(MinMemoryMb: 4096, Architectures: ["x64"])),
        Review: new BatPackageReview { Source = "Microsoft" });

    /// <summary>Stands in for the control plane's Windows (CNG) key.</summary>
    private sealed class FakeApprovalKey : IPackageApprovalKey
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public string? PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());
        public string? Problem => null;
        public PackageSignature Sign(string statement) =>
            PackageSignaturePolicy.Sign(statement, _key, PublicKey!);
    }

    private static PackageFileUpload Upload(string text = Bat, string name = "Vendor Fix.bat") =>
        new(name, Convert.ToBase64String(Encoding.UTF8.GetBytes(text)));

    private static PackageBuildRequest OfficeRequest(PackageFileUpload? file = null, bool replace = false) => new(
        "office-reseller-fix", "office", "Office activation", "Activates Office through the reseller BAT.",
        new BatchPlaybook(2, [new BatchPlaybookStep("Select an option:", "1", "line", 60)],
            SuccessMarker: "Product activation successful", FailureMarkers: ["FAILED"]),
        file ?? Upload(), replace,
        Office: new BatPackageOffice
        {
            MinOfficeVersion = "16.0", MaxOfficeVersionExclusive = "17.0",
            OfficeProductReleaseIds = ["ProPlus2021Volume"]
        },
        Review: new BatPackageReview { Source = "Reseller" });

    [Fact]
    public async Task SavesAnOfficePackageWithPinnedHashesThatOnlyNeedsASignature()
    {
        var result = await CreateBuilder().SaveAsync(OfficeRequest(), "reza", mayReplaceSigned: false,
            TestContext.Current.CancellationToken);

        Assert.True(result.Saved, result.Error);
        var status = result.Status!;
        Assert.Equal(BatPackageStatuses.Unsigned, status.Status);
        Assert.Single(status.Reasons);
        Assert.Equal(status.ActualBatSha256, status.PinnedBatSha256);
        Assert.Equal(status.ComputedPlaybookSha256, status.PinnedPlaybookSha256);
        Assert.Equal("office", status.Kind);

        var folder = Path.Combine(_root, "packages", "office-reseller-fix");
        Assert.Equal(Bat, await File.ReadAllTextAsync(Path.Combine(folder, "Vendor Fix.bat"),
            TestContext.Current.CancellationToken));
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "package.json"),
            TestContext.Current.CancellationToken))!;
        Assert.Equal(1, manifest["schemaVersion"]!.GetValue<int>());
        Assert.Equal("reza", manifest["review"]!["reviewedBy"]!.GetValue<string>());
        Assert.Null(manifest["signature"]);
        // The staging folder never shows up as a package.
        Assert.Single(Directory.GetDirectories(Path.Combine(_root, "packages")));
    }

    [Fact]
    public async Task SignedBuilderPackageBecomesReady()
    {
        await CreateBuilder().SaveAsync(OfficeRequest(), "reza", false, TestContext.Current.CancellationToken);
        var folder = Path.Combine(_root, "packages", "office-reseller-fix");
        var path = Path.Combine(folder, "package.json");
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!.AsObject();
        TestPackageSigner.Sign(node, folder);
        await File.WriteAllTextAsync(path, node.ToJsonString(), TestContext.Current.CancellationToken);

        var status = Assert.Single(await CreateRepository().GetPackageStatusesAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(BatPackageStatuses.Ready, status.Status);
    }

    [Fact]
    public async Task GeneralPackageGetsSymptomsAndAVersion3Playbook()
    {
        var request = OfficeRequest() with
        {
            Id = "printer-fix", Kind = "general", Office = null,
            Symptoms = ["printer is stuck", "چاپگر کار نمی‌کند", "printer is stuck"],
            Playbook = new BatchPlaybook(2, [], Arguments: ["/quiet"])
        };

        var result = await CreateBuilder().SaveAsync(request, "reza", false, TestContext.Current.CancellationToken);

        Assert.True(result.Saved, result.Error);
        Assert.Equal(["printer is stuck", "چاپگر کار نمی‌کند"], result.Status!.Symptoms);
        Assert.Equal(3, result.Status.Playbook!.Version);
        Assert.Equal(["/quiet"], result.Status.Playbook.Arguments);
        var text = await File.ReadAllTextAsync(Path.Combine(_root, "packages", "printer-fix", "package.json"),
            TestContext.Current.CancellationToken);
        Assert.Contains("چاپگر", text);
    }

    [Theory]
    [InlineData("Bad Id", "office", "id must be")]
    [InlineData("good-id", "printer", "Choose what the package is for")]
    public async Task RejectsBadIdsAndKinds(string id, string kind, string message)
    {
        var result = await CreateBuilder().SaveAsync(OfficeRequest() with { Id = id, Kind = kind }, "reza", false,
            TestContext.Current.CancellationToken);

        Assert.False(result.Saved);
        Assert.Contains(message, result.Error);
        Assert.False(Directory.Exists(Path.Combine(_root, "packages")) && Directory.EnumerateDirectories(Path.Combine(_root, "packages")).Any());
    }

    [Fact]
    public async Task RejectsInvalidPlaybooksTodoAndUnsafeFileNames()
    {
        var builder = CreateBuilder();
        var token = TestContext.Current.CancellationToken;

        var noPrompt = await builder.SaveAsync(OfficeRequest() with
        {
            Playbook = new BatchPlaybook(2, [new BatchPlaybookStep("", "1", "line", 60)])
        }, "reza", false, token);
        var todo = await builder.SaveAsync(OfficeRequest() with { Description = "TODO: describe" }, "reza", false, token);
        var path = await builder.SaveAsync(OfficeRequest(Upload(name: "..\\evil.bat")), "reza", false, token);
        var exe = await builder.SaveAsync(OfficeRequest(Upload(name: "tool.exe")), "reza", false, token);

        Assert.StartsWith("Playbook:", noPrompt.Error);
        Assert.Contains("TODO", todo.Error);
        Assert.Contains(".bat file", path.Error);
        Assert.Contains(".bat file", exe.Error);
    }

    [Fact]
    public async Task ReplacingNeedsReplaceAndKeepsTheBatWhenNoneIsUploaded()
    {
        var builder = CreateBuilder();
        var token = TestContext.Current.CancellationToken;
        await builder.SaveAsync(OfficeRequest(), "reza", false, token);

        var duplicate = await builder.SaveAsync(OfficeRequest(), "reza", false, token);
        Assert.Contains("already exists", duplicate.Error);

        var changed = OfficeRequest(replace: true) with { File = null, DisplayName = "Office activation v2" };
        var result = await builder.SaveAsync(changed, "reza", false, token);

        Assert.True(result.Saved, result.Error);
        Assert.Equal("Office activation v2", result.Status!.DisplayName);
        Assert.Equal(result.Status.ActualBatSha256, result.Status.PinnedBatSha256);
    }

    [Fact]
    public async Task OnlyAdminsReplaceASignedPackage()
    {
        var builder = CreateBuilder();
        var token = TestContext.Current.CancellationToken;
        await builder.SaveAsync(OfficeRequest(), "reza", false, token);
        var folder = Path.Combine(_root, "packages", "office-reseller-fix");
        var path = Path.Combine(folder, "package.json");
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path, token))!.AsObject();
        TestPackageSigner.Sign(node, folder);
        await File.WriteAllTextAsync(path, node.ToJsonString(), token);

        var member = await builder.SaveAsync(OfficeRequest(replace: true), "member", false, token);
        var admin = await builder.SaveAsync(OfficeRequest(replace: true), "admin", true, token);

        Assert.Contains("Only an admin", member.Error);
        Assert.True(admin.Saved, admin.Error);
        Assert.Equal(BatPackageStatuses.Unsigned, admin.Status!.Status);
    }

    [Fact]
    public async Task LoadsAPackageBackIntoTheFormWithAScanOfItsBat()
    {
        var builder = CreateBuilder();
        var token = TestContext.Current.CancellationToken;
        await builder.SaveAsync(OfficeRequest(), "reza", false, token);

        var draft = await builder.LoadAsync("office-reseller-fix", token);

        Assert.NotNull(draft);
        Assert.Equal("office", draft.Kind);
        Assert.Equal("Select an option:", draft.Manifest.Playbook!.Steps[0].Expect);
        Assert.Equal("Select an option:", Assert.Single(draft.Scan!.Screens).Expect);
        Assert.Null(await builder.LoadAsync("..", token));
        Assert.Null(await builder.LoadAsync("missing", token));
    }

    [Fact]
    public void ScanReportsLintAndNonUtf8Files()
    {
        var bytes = Encoding.Latin1.GetBytes("@echo off\r\necho café\r\npowershell -verb runas foo\r\nset /p x=Pick: \r\n");
        var (result, error) = PackageBuilder.Scan(new PackageFileUpload("x.bat", Convert.ToBase64String(bytes)));

        Assert.Null(error);
        Assert.NotEmpty(result!.LintErrors);
        Assert.Contains(result.Notes, note => note.Contains("not UTF-8"));
        Assert.Equal("Pick:", Assert.Single(result.Screens).Expect);
        Assert.NotNull(PackageBuilder.Scan(new PackageFileUpload("x.bat", "not base64!")).Error);
    }

    private static PackageApproveRequest Reviewed(BatPackageStatus status) =>
        new(status.ActualBatSha256, status.ComputedPlaybookSha256);

    [Fact]
    public async Task ASecondAdminApprovesAndTheControlPlaneSignsIt()
    {
        var key = new FakeApprovalKey();
        var builder = CreateBuilder(key);
        var token = TestContext.Current.CancellationToken;
        var saved = await builder.SaveAsync(OfficeRequest(), "member1", false, token);
        Assert.Equal("member1", (await builder.LoadAsync("office-reseller-fix", token))!.Manifest.Review!.BuiltBy);

        var result = await builder.ApproveAsync("office-reseller-fix", Reviewed(saved.Status!), "admin1", token);

        Assert.True(result.Saved, result.Error);
        Assert.Equal(BatPackageStatuses.Ready, result.Status!.Status);
        Assert.Equal("admin1", result.Status.Review!.ApprovedBy);
        // Joins the Office catalog for endpoints.
        Assert.Single((await CreateRepository(key).GetOfficeBugfixCatalogAsync(token)).Artifacts);
        // Without trusting the approval key, the same package is not published.
        Assert.Equal(BatPackageStatuses.Unsigned,
            Assert.Single(await CreateRepository().GetPackageStatusesAsync(token)).Status);
    }

    [Fact]
    public async Task AnAdminMayApproveAPackageTheySavedThemselves()
    {
        var key = new FakeApprovalKey();
        var token = TestContext.Current.CancellationToken;
        var saved = await CreateBuilder(key).SaveAsync(OfficeRequest(), "Reza", false, token);

        var own = await CreateBuilder(key).ApproveAsync("office-reseller-fix", Reviewed(saved.Status!), "reza", token);

        Assert.True(own.Saved, own.Error);
        Assert.Equal("reza", own.Status!.Review!.ApprovedBy);
    }

    [Fact]
    public async Task ApprovalRefusesAChangedRevisionAndBrokenPackages()
    {
        var key = new FakeApprovalKey();
        var builder = CreateBuilder(key);
        var token = TestContext.Current.CancellationToken;
        var first = await builder.SaveAsync(OfficeRequest(), "member1", false, token);
        await builder.SaveAsync(OfficeRequest(replace: true) with { File = null,
            Playbook = new BatchPlaybook(2, [new BatchPlaybookStep("Select an option:", "2", "line", 60)]) },
            "member1", false, token);

        var stale = await builder.ApproveAsync("office-reseller-fix", Reviewed(first.Status!), "admin1", token);
        Assert.Contains("changed after you opened it", stale.Error);

        var folder = Path.Combine(_root, "packages", "office-reseller-fix");
        await File.AppendAllTextAsync(Path.Combine(folder, "Vendor Fix.bat"), "rem changed\r\n", token);
        var current = Assert.Single(await CreateRepository(key).GetPackageStatusesAsync(token));
        var broken = await builder.ApproveAsync("office-reseller-fix", Reviewed(current), "admin1", token);
        Assert.Contains("Fix the package first", broken.Error);
        Assert.Null((await builder.LoadAsync("office-reseller-fix", token))!.Manifest.Review!.ApprovedBy);
    }

    [Fact]
    public async Task SavingAgainRemovesTheApproval()
    {
        var key = new FakeApprovalKey();
        var builder = CreateBuilder(key);
        var token = TestContext.Current.CancellationToken;
        var saved = await builder.SaveAsync(OfficeRequest(), "member1", false, token);
        await builder.ApproveAsync("office-reseller-fix", Reviewed(saved.Status!), "admin1", token);

        var changed = await builder.SaveAsync(OfficeRequest(replace: true) with { File = null }, "admin1", true, token);

        Assert.Equal(BatPackageStatuses.Unsigned, changed.Status!.Status);
        Assert.Null(changed.Status.Review!.ApprovedBy);
        Assert.Equal("admin1", changed.Status.Review.BuiltBy);
    }

    [Fact]
    public async Task ApprovalNeedsTheKey()
    {
        var token = TestContext.Current.CancellationToken;
        var builder = CreateBuilder();
        var saved = await builder.SaveAsync(OfficeRequest(), "member1", false, token);

        var result = await builder.ApproveAsync("office-reseller-fix", Reviewed(saved.Status!), "admin1", token);

        Assert.False(result.Saved);
        Assert.False(builder.GetApprovalInfo().Available);
        Assert.True(CreateBuilder(new FakeApprovalKey()).GetApprovalInfo().Available);
    }

    [Fact]
    public async Task UploadStoresTheInstallerAndReadsItsSigner()
    {
        var info = await UploadInstallerAsync();

        Assert.Equal("dotnet-setup.exe", info.FileName);
        Assert.Equal("exe", info.FileType);
        Assert.NotNull(info.Signer);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(SignedExe,
            TestContext.Current.CancellationToken))), info.Sha256);
        Assert.NotNull(CreateUploads().Get(info.UploadId));
        Assert.Null(CreateUploads().Get("../" + info.UploadId));
        Assert.Null(InstallerUploads.SafeFileName("fix.bat"));
        Assert.Null(InstallerUploads.SafeFileName(".exe"));
        Assert.Equal("Setup-x64-.msi", InstallerUploads.SafeFileName(@"C:\dir\Setup x64!.msi"));
    }

    [Fact]
    public async Task UploadRefusesLargeFilesAndLeavesNothingBehind()
    {
        await using var stream = new MemoryStream(new byte[100]);
        var (info, error) = await CreateUploads(maximumBytes: 10).SaveAsync(stream, "big.exe",
            TestContext.Current.CancellationToken);

        Assert.Null(info);
        Assert.Contains("larger than", error);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(PackageBuilder.StagingRoot(CreateRepository()), "uploads")));
    }

    [Fact]
    public void InstallerTypeAndProductNameAreSuggested()
    {
        var path = Path.Combine(_root, "nsis.exe");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes("MZ....Nullsoft.NSIS.exehead...."));

        Assert.Equal(InstallerKinds.Nsis, InstallerUploads.DetectTechnology(path, null));
        Assert.Equal(InstallerKinds.Inno, InstallerUploads.DetectTechnology(path, "Inno Setup version 6"));
        Assert.Equal("7-Zip", InstallerUploads.CleanProductName("7-Zip 24.08 (x64)"));
        Assert.Null(InstallerUploads.CleanProductName("  "));
    }

    [Fact]
    public async Task InstallPackageIsSavedApprovedAndLoadedBack()
    {
        var key = new FakeApprovalKey();
        var builder = CreateBuilder(key);
        var token = TestContext.Current.CancellationToken;
        var info = await UploadInstallerAsync();

        var saved = await builder.SaveAsync(InstallRequest(info.UploadId), "member1", false, token);

        Assert.True(saved.Saved, saved.Error);
        Assert.Equal(BatPackageStatuses.Unsigned, saved.Status!.Status);
        Assert.Equal("exe", saved.Status.FileType);
        Assert.Null(CreateUploads().Get(info.UploadId));
        var folder = Path.Combine(_root, "packages", "dotnet-install");
        Assert.True(File.Exists(Path.Combine(folder, "dotnet-setup.exe")));
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "package.json"), token))!;
        Assert.Equal(info.Signer, (string?)manifest["file"]!["publisher"]);
        Assert.Equal("install-dotnet", (string?)manifest["problem"]);
        Assert.Equal(600, (int?)manifest["playbook"]!["timeoutSeconds"]);
        Assert.Equal("exe", (string?)manifest["software"]!["install"]!["kind"]);

        var approved = await builder.ApproveAsync("dotnet-install", Reviewed(saved.Status), "admin1", token);
        Assert.True(approved.Saved, approved.Error);
        Assert.True(BatPackageStatuses.IsPublished(approved.Status!.Status));

        var draft = await builder.LoadAsync("dotnet-install", token);
        Assert.Equal(PackageBuilder.Install, draft!.Kind);
        Assert.True(draft.Signed);

        // Saving again without a new upload keeps the installer and needs a new approval.
        var changed = await builder.SaveAsync(InstallRequest(null, replace: true), "admin1", true, token);
        Assert.True(changed.Saved, changed.Error);
        Assert.Equal(BatPackageStatuses.Unsigned, changed.Status!.Status);
        Assert.True(File.Exists(Path.Combine(folder, "dotnet-setup.exe")));
    }

    [Fact]
    public async Task InstallPackageNeedsItsRules()
    {
        var builder = CreateBuilder();
        var token = TestContext.Current.CancellationToken;
        var info = await UploadInstallerAsync();

        Assert.Contains("ends with", (await builder.SaveAsync(InstallRequest(info.UploadId, id: "dotnet"), "m", false, token)).Error);
        Assert.Contains("kind msi", (await builder.SaveAsync(InstallRequest(info.UploadId, kind: "msi"), "m", false, token)).Error);
        Assert.Contains("Apps & features", (await builder.SaveAsync(InstallRequest(info.UploadId) with
            { Software = InstallRequest(null).Software! with { Detection = [] } }, "m", false, token)).Error);
        Assert.Contains("expired", (await builder.SaveAsync(InstallRequest(new string('b', 32)), "m", false, token)).Error);
        Assert.Contains("Upload the installer", (await builder.SaveAsync(InstallRequest(null), "m", false, token)).Error);

        var unsigned = await UploadInstallerAsync(UnsignedExe, "hostname-setup.exe");
        Assert.Contains("no vendor", (await builder.SaveAsync(InstallRequest(unsigned.UploadId), "m", false, token)).Error);
        Assert.False(Directory.Exists(Path.Combine(_root, "packages", "dotnet-install")));
    }

    private async Task<SupportFileInfo> UploadSupportAsync(string name, string text)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var (info, error) = await CreateUploads().SaveSupportAsync(stream, name, TestContext.Current.CancellationToken);
        Assert.True(info is not null, error);
        return info!;
    }

    [Fact]
    public void MenusInPlainTextFilesAreSuggested()
    {
        var screens = BatMenuScanner.ScanPlainText("Main menu\r\n\r\n[1] Activate\r\n[2] Exit\r\nSelect an option:\r\n", "menu.txt");
        var screen = Assert.Single(screens);
        Assert.Equal("Select an option:", screen.Expect);
        Assert.Equal(new[] { "1", "2" }, screen.Options.Select(option => option.Value));
        Assert.Contains("menu.txt", screen.Notes[0]);

        var noPrompt = Assert.Single(BatMenuScanner.ScanPlainText("1. Repair\n2. Reset\nThanks", "m.txt"));
        Assert.Null(noPrompt.Expect);
        Assert.Empty(BatMenuScanner.ScanPlainText("[1] only one option\nhello", "m.txt"));
    }

    [Fact]
    public async Task SupportingFilesAreHashedKeptAndRemoved()
    {
        var key = new FakeApprovalKey();
        var builder = CreateBuilder(key);
        var token = TestContext.Current.CancellationToken;
        var menu = await UploadSupportAsync("menu list.txt", "[1] Activate\r\n[2] Exit\r\nChoose:\r\n");
        Assert.Equal("menu-list.txt", menu.FileName);
        Assert.Single(menu.Screens);

        var saved = await builder.SaveAsync(OfficeRequest() with { SupportUploadIds = [menu.UploadId!] }, "member1", false, token);
        Assert.True(saved.Saved, saved.Error);
        Assert.Equal(BatPackageStatuses.Unsigned, saved.Status!.Status);
        var entry = Assert.Single(saved.Status.SupportFiles);
        Assert.Equal(("menu-list.txt", menu.Sha256), (entry.FileName, entry.Sha256));
        var folder = Path.Combine(_root, "packages", "office-reseller-fix");
        Assert.True(File.Exists(Path.Combine(folder, "files", "menu-list.txt")));
        Assert.Null(CreateUploads().GetSupport(menu.UploadId));

        // The approval covers the supporting files too.
        var approved = await builder.ApproveAsync("office-reseller-fix", Reviewed(saved.Status), "admin1", token);
        Assert.True(approved.Saved, approved.Error);

        var draft = await builder.LoadAsync("office-reseller-fix", token);
        var loaded = Assert.Single(draft!.SupportFiles!);
        Assert.Equal(menu.Sha256, loaded.Sha256);
        Assert.Single(loaded.Screens);

        // Not saying which files to keep keeps them all; an empty list removes them.
        var kept = await builder.SaveAsync(OfficeRequest(replace: true) with { File = null }, "admin1", true, token);
        Assert.Single(kept.Status!.SupportFiles);
        var removed = await builder.SaveAsync(OfficeRequest(replace: true) with { File = null, KeepSupportFiles = [] }, "admin1", true, token);
        Assert.Empty(removed.Status!.SupportFiles);
        Assert.False(Directory.Exists(Path.Combine(folder, "files")));
    }

    [Fact]
    public async Task SupportingFilesNeedSafeUniqueNames()
    {
        var builder = CreateBuilder();
        var token = TestContext.Current.CancellationToken;
        var clash = await UploadSupportAsync("office-reseller-fix.bat", "echo hi");

        var result = await builder.SaveAsync(OfficeRequest() with { SupportUploadIds = [clash.UploadId!] }, "m", false, token);

        Assert.Contains("cannot be named", result.Error);
        Assert.Contains("expired", (await builder.SaveAsync(OfficeRequest() with { SupportUploadIds = [new string('c', 32)] }, "m", false, token)).Error);
        Assert.Contains("no supporting file", (await builder.SaveAsync(OfficeRequest() with { KeepSupportFiles = ["x.txt"] }, "m", false, token)).Error);
        Assert.Null(InstallerUploads.SafeSupportFileName(".hidden"));
        Assert.Null(InstallerUploads.SafeSupportFileName(new string('a', 129)));
    }
}
