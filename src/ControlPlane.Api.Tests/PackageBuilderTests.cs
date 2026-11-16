using ControlPlane.Api.Files;
using ControlPlane.Api.Files.Builder;
using EndpointAgents.Shared.Office;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;
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

    private FileArtifactRepository CreateRepository() => new(
        Options.Create(new FileRepositoryOptions
        {
            RootPath = _root, RequireProtectedAcl = false,
            TrustedPackageSigningKeys = [TestPackageSigner.PublicKey]
        }),
        new HostingEnvironment { ContentRootPath = _root });

    private PackageBuilder CreateBuilder() => new(CreateRepository(), TimeProvider.System);

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
}
