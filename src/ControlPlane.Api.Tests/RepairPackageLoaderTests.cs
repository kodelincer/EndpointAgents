using ControlPlane.Api.Files;
using EndpointAgents.Shared.Office;
using EndpointAgents.Shared.Packages;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ControlPlane.Api.Tests;

/// <summary>Schema 2 general repair packages (BAT or EXE).</summary>
public sealed class RepairPackageLoaderTests : IDisposable
{
    private const string Bat = "@echo off\r\necho Spooler reset\r\nexit /b 0\r\n";
    // dotnet.exe carries an embedded Authenticode signature; the Windows tools below are
    // catalog-signed only, so they count as unsigned.
    private static readonly string ConsoleExe = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
    private static readonly string UnsignedExe =
        Path.Combine(Environment.SystemDirectory, "hostname.exe");
    private static readonly string GuiExe =
        Path.Combine(Environment.SystemDirectory, "mmc.exe");
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        $"EndpointAgents-repair-packages-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task NoInputBatPackageJoinsTheRepairCatalog()
    {
        await WritePackageAsync("spooler-reset", supportFile: ("helper.txt", "data"));

        var repository = CreateRepository();
        var status = Assert.Single(await repository.GetPackageStatusesAsync(
            TestContext.Current.CancellationToken));
        var package = Assert.Single(await repository.GetRepairPackagesAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(BatPackageStatuses.Ready, status.Status);
        Assert.Equal(FileArtifactRepository.GeneralPackageKind, status.Kind);
        var descriptor = package.Descriptor;
        Assert.Equal("spooler-reset", descriptor.PackageId);
        Assert.Equal(PackageFileTypes.Bat, descriptor.FileType);
        // Endpoints stage the file as <id>.<type>, whatever its name in the package.
        Assert.Equal("spooler-reset.bat", descriptor.FileName);
        Assert.Equal("Vendor Tool.bat", package.File.FileName);
        Assert.Equal("/api/files/repair-packages/spooler-reset", descriptor.DownloadPath);
        Assert.Equal(["Printing is stuck"], descriptor.Symptoms);
        Assert.Equal("10.0.17763", descriptor.MinWindowsVersion);
        Assert.Empty(descriptor.Playbook.Steps);
        Assert.Equal(["/quiet"], descriptor.Playbook.Arguments);
        Assert.True(PackageDescriptorPolicy.TryValidate(descriptor, out _));
        Assert.NotNull(await repository.GetRepairPackageSupportFileAsync("spooler-reset",
            "helper.txt", TestContext.Current.CancellationToken));
        // General packages never join the activation catalogs.
        Assert.Empty((await repository.GetOfficeBugfixCatalogAsync(
            TestContext.Current.CancellationToken)).Artifacts);
        Assert.Empty((await repository.GetWindowsBugfixCatalogAsync(
            TestContext.Current.CancellationToken)).Artifacts);
    }

    [Fact]
    public async Task ConsoleExeMayAnswerPrompts()
    {
        var signer = ExecutableInspector.ReadSignerSubject(ConsoleExe);
        Assert.NotNull(signer);
        await WritePackageAsync("console-tool", exe: ConsoleExe,
            playbook: Playbook(new BatchPlaybookStep("Continue?", "y", "key", 30)),
            edit: package => package["file"]!["publisher"] = signer);

        var status = await SingleStatusAsync();
        var package = Assert.Single(await CreateRepository().GetRepairPackagesAsync(
            TestContext.Current.CancellationToken));

        Assert.True(BatPackageStatuses.IsPublished(status.Status),
            string.Join(" ", status.Reasons));
        Assert.Equal(PackageFileTypes.Exe, status.FileType);
        Assert.Equal(ExecutableInspector.Console, status.ExeSubsystem);
        Assert.Equal(signer, package.Descriptor.Publisher);
        Assert.True(PackageSignaturePolicy.Verify(package.Descriptor,
            [TestPackageSigner.PublicKey]));
    }

    [Fact]
    public async Task SignedExeNeedsItsPublisherPinned()
    {
        await WritePackageAsync("no-pin", exe: ConsoleExe);

        var status = await SingleStatusAsync();

        Assert.Equal(BatPackageStatuses.Invalid, status.Status);
        Assert.Contains(status.Reasons, reason => reason.Contains("Set file.publisher"));
    }

    [Fact]
    public async Task UnsignedExeIsRejected()
    {
        await WritePackageAsync("unsigned-exe", exe: UnsignedExe);

        var status = await SingleStatusAsync();

        Assert.Equal(BatPackageStatuses.Invalid, status.Status);
        Assert.Contains(status.Reasons, reason => reason.Contains("no embedded Authenticode"));
    }

    [Fact]
    public async Task UnsignedOrForeignSignedPackagesAreNotPublished()
    {
        await WritePackageAsync("unsigned", sign: false);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await WritePackageAsync("foreign", sign: false, edit: package =>
            TestPackageSigner.Sign(package, Path.Combine(_root, "packages", "foreign"),
                otherKey, Convert.ToBase64String(otherKey.ExportSubjectPublicKeyInfo())));
        await WritePackageAsync("signed");

        var repository = CreateRepository();
        var statuses = await repository.GetPackageStatusesAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(BatPackageStatuses.Unsigned,
            statuses.Single(item => item.Id == "unsigned").Status);
        Assert.Equal(BatPackageStatuses.Unsigned,
            statuses.Single(item => item.Id == "foreign").Status);
        Assert.Equal(["signed"], (await repository.GetRepairPackagesAsync(
            TestContext.Current.CancellationToken)).Select(item => item.Descriptor.PackageId));
    }

    [Fact]
    public async Task ChangingASignedPackageNeedsANewSignature()
    {
        // A different but valid playbook pinned by hash: the signature no longer matches.
        var changed = Playbook() with { TimeoutSeconds = 60 };
        await WritePackageAsync("changed", edit: package => { });
        var path = Path.Combine(_root, "packages", "changed", "package.json");
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path,
            TestContext.Current.CancellationToken))!.AsObject();
        node["playbook"] = JsonSerializer.SerializeToNode(changed,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        node["playbookSha256"] = BatchPlaybookPolicy.ComputeSha256(changed);
        await File.WriteAllTextAsync(path, node.ToJsonString(),
            TestContext.Current.CancellationToken);

        var status = await SingleStatusAsync();

        Assert.Equal(BatPackageStatuses.Unsigned, status.Status);
        Assert.Contains(status.Reasons, reason => reason.Contains("sign it again"));
    }

    private const string SelfElevatingBat =
        "@echo off\r\nnet session >nul 2>&1 || (powershell -Command \"Start-Process '%~f0' -Verb RunAs\" & exit /b)\r\nexit /b 0\r\n";

    private static void AcceptRisks(JsonObject package, params string[] risks) =>
        package["review"] = new JsonObject
        {
            ["reviewedBy"] = "Tester",
            ["acceptedRisks"] = new JsonArray(risks.Select(risk => (JsonNode)risk).ToArray())
        };

    [Fact]
    public async Task SelfElevatingBatIsBlockedUntilTheReviewerAcceptsTheRisk()
    {
        await WritePackageAsync("elevates", bat: SelfElevatingBat);
        await WritePackageAsync("elevates-accepted", bat: SelfElevatingBat,
            edit: package => AcceptRisks(package, BatRisks.SelfElevation));

        var statuses = await CreateRepository().GetPackageStatusesAsync(
            TestContext.Current.CancellationToken);
        var published = await CreateRepository().GetRepairPackagesAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(BatPackageStatuses.LintRejected,
            statuses.Single(item => item.Id == "elevates").Status);
        var accepted = statuses.Single(item => item.Id == "elevates-accepted");
        Assert.Equal(BatPackageStatuses.ReadyWithWarnings, accepted.Status);
        Assert.Contains(accepted.Warnings, warning => warning.StartsWith("Accepted risk (self-elevation)"));
        var package = Assert.Single(published);
        Assert.Equal("elevates-accepted", package.Descriptor.PackageId);
        Assert.Equal([BatRisks.SelfElevation], package.Descriptor.AcceptedRisks);
        Assert.True(PackageSignaturePolicy.Verify(package.Descriptor, [TestPackageSigner.PublicKey]));
    }

    [Fact]
    public async Task EncodedPayloadCanBeAcceptedButADownloadCannot()
    {
        await WritePackageAsync("payload", edit: package => AcceptRisks(package,
                BatRisks.EmbeddedPayload),
            bat: "@echo off\r\ncertutil -decode data.b64 run.ps1\r\nexit /b 0\r\n");
        await WritePackageAsync("download", edit: package => AcceptRisks(package,
                BatRisks.SelfElevation, BatRisks.EmbeddedPayload),
            bat: "@echo off\r\ncertutil -urlcache -f http://example.test/x x\r\nexit /b 0\r\n");

        var statuses = await CreateRepository().GetPackageStatusesAsync(
            TestContext.Current.CancellationToken);

        Assert.True(BatPackageStatuses.IsPublished(
            statuses.Single(item => item.Id == "payload").Status));
        var download = statuses.Single(item => item.Id == "download");
        Assert.Equal(BatPackageStatuses.LintRejected, download.Status);
        Assert.Contains(download.Reasons, reason => reason.Contains("cannot be accepted"));
    }

    [Fact]
    public async Task OnlyKnownRisksMayBeAccepted()
    {
        await WritePackageAsync("bad-risk", edit: package => AcceptRisks(package, "network-download"));

        var status = await SingleStatusAsync();

        Assert.Equal(BatPackageStatuses.Invalid, status.Status);
        Assert.Contains(status.Reasons, reason => reason.Contains("review.acceptedRisks may only contain"));
    }

    [Fact]
    public async Task AddingAnAcceptedRiskAfterSigningInvalidatesTheSignature()
    {
        // Signed with no accepted risks, then a risk is added to package.json by hand.
        await WritePackageAsync("tampered", bat: SelfElevatingBat);
        var path = Path.Combine(_root, "packages", "tampered", "package.json");
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path,
            TestContext.Current.CancellationToken))!.AsObject();
        AcceptRisks(node, BatRisks.SelfElevation);
        await File.WriteAllTextAsync(path, node.ToJsonString(),
            TestContext.Current.CancellationToken);

        var status = await SingleStatusAsync();

        Assert.Equal(BatPackageStatuses.Unsigned, status.Status);
    }

    [Fact]
    public async Task GuiExeCanOnlyRunWithoutInput()
    {
        await WritePackageAsync("gui-prompts", exe: GuiExe,
            playbook: Playbook(new BatchPlaybookStep("Continue?", "y", "key", 30)));
        await WritePackageAsync("gui-silent", exe: GuiExe);

        var statuses = await CreateRepository().GetPackageStatusesAsync(
            TestContext.Current.CancellationToken);

        // mmc.exe has no embedded signature, so both stay unpublished; only the one
        // that tries to answer prompts is also reported as a GUI program.
        var prompts = statuses.Single(item => item.Id == "gui-prompts");
        Assert.Equal(ExecutableInspector.Gui, prompts.ExeSubsystem);
        Assert.Contains(prompts.Reasons, reason => reason.Contains("GUI"));
        Assert.DoesNotContain(statuses.Single(item => item.Id == "gui-silent").Reasons,
            reason => reason.Contains("GUI"));
    }

    [Fact]
    public async Task NonExecutableExeIsRejected()
    {
        await WritePackageAsync("fake-exe", edit: package =>
            package["file"]!["type"] = "exe", fileName: "Tool.exe");

        var status = await SingleStatusAsync();

        Assert.Equal(BatPackageStatuses.Invalid, status.Status);
        Assert.Contains(status.Reasons, reason => reason.Contains("not a Windows"));
    }

    [Fact]
    public async Task PinnedPublisherMustMatchTheSigner()
    {
        await WritePackageAsync("pinned", exe: ConsoleExe, edit: package =>
            package["file"]!["publisher"] = "CN=Contoso");

        var status = await SingleStatusAsync();

        Assert.Equal(BatPackageStatuses.Invalid, status.Status);
        Assert.Contains(status.Reasons, reason => reason.Contains("file.publisher"));
    }

    [Theory]
    [InlineData("symptoms", "symptoms must list")]
    [InlineData("office", "remove \"bat\", \"office\" and \"windows\"")]
    [InlineData("compatibility", "minWindowsVersion")]
    [InlineData("type", "file.type must be")]
    public async Task InvalidSchema2FieldsAreReported(string field, string expected)
    {
        await WritePackageAsync("broken", edit: package =>
        {
            switch (field)
            {
                case "symptoms": package["symptoms"] = new JsonArray(); break;
                case "office": package["office"] = new JsonObject(); break;
                case "compatibility":
                    package["compatibility"]!["maxWindowsVersionExclusive"] = "10.0.1";
                    break;
                case "type": package["file"]!["type"] = "msi"; break;
            }
        });

        var status = await SingleStatusAsync();

        Assert.Equal(BatPackageStatuses.Invalid, status.Status);
        Assert.Contains(status.Reasons, reason => reason.Contains(expected));
    }

    [Theory]
    [InlineData("a & b")]
    [InlineData("\"quoted\"")]
    [InlineData("%PATH%")]
    public async Task ShellMetacharactersInArgumentsAreRejected(string argument)
    {
        var playbook = Playbook() with { Arguments = [argument] };
        await WritePackageAsync("args", edit: package =>
        {
            package["playbook"]!["arguments"] = new JsonArray(argument);
            // Pin what the playbook would hash to if it were accepted.
            package["playbookSha256"] = new string('A', 64);
        });

        var status = await SingleStatusAsync();

        Assert.Equal(BatPackageStatuses.InvalidPlaybook, status.Status);
        Assert.Contains(status.Reasons, reason => reason.Contains("Argument 0"));
        Assert.False(BatchPlaybookPolicy.TryNormalize(playbook, out _, out _));
    }

    [Fact]
    public async Task SupportFileCannotShadowThePackageFile()
    {
        await WritePackageAsync("shadow", supportFile: ("shadow.bat", Bat));

        var status = await SingleStatusAsync();

        Assert.Equal(BatPackageStatuses.Invalid, status.Status);
    }

    [Fact]
    public async Task PackagesForTheSameProblemAreListedInTryOrder()
    {
        await WritePackageAsync("spooler-b", edit: package =>
        {
            package["problem"] = "printing-stuck";
            package["order"] = 10;
        });
        await WritePackageAsync("spooler-a", edit: package =>
        {
            package["problem"] = "printing-stuck";
            package["order"] = 20;
        });
        await WritePackageAsync("bad-order", edit: package => package["order"] = 0);

        var repository = CreateRepository();
        var packages = await repository.GetRepairPackagesAsync(
            TestContext.Current.CancellationToken);
        var statuses = await repository.GetPackageStatusesAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(["spooler-b", "spooler-a"],
            packages.Select(item => item.Descriptor.PackageId));
        Assert.All(packages, item => Assert.Equal("printing-stuck", item.Problem));
        Assert.Equal(BatPackageStatuses.Invalid,
            statuses.Single(item => item.Id == "bad-order").Status);
    }

    [Fact]
    public async Task GeneralPackageMayShareAnIdWithAnActivationRepair()
    {
        await WritePackageAsync("windows-activation-repair");

        Assert.True(BatPackageStatuses.IsPublished((await SingleStatusAsync()).Status));
    }

    public static TheoryData<string> InstallPackages() =>
    [
        "chrome-install", "adobe-connect-install", "photos-install", "persian-fonts-install",
        "foxit-reader-install", "foxit-editor-install"
    ];

    [Theory]
    [MemberData(nameof(InstallPackages))]
    public async Task CheckedInInstallPackageIsReadyWithItsFiles(string id)
    {
        var folder = await CopyCheckedInPackageAsync(id);

        var repository = CreateRepository();
        var status = await SingleStatusAsync();
        var published = Assert.Single(await repository.GetRepairPackagesAsync(
            TestContext.Current.CancellationToken));
        var descriptor = published.Descriptor;

        // Ready, not "Ready with lint warnings": the BAT has nothing the lint flags.
        Assert.True(status.Status == BatPackageStatuses.Ready,
            string.Join(" ", status.Reasons));
        Assert.Empty(BatScriptLint.Inspect(await File.ReadAllTextAsync(
            Path.Combine(folder, $"{id}.bat"),
            TestContext.Current.CancellationToken)).Warnings);
        Assert.StartsWith("install-", published.Problem);
        Assert.NotEmpty(descriptor.SupportFiles!);
        // 10 already installed, 3010 installed and the user signs out or restarts.
        Assert.Contains(10, descriptor.Playbook.SuccessExitCodes!);
        Assert.Equal([3010], descriptor.Playbook.RestartExitCodes);
        Assert.Equal(["ERROR:"], descriptor.Playbook.FailureMarkers);
        Assert.Empty(descriptor.Playbook.Steps);
        Assert.Empty(descriptor.Playbook.Arguments ?? []);
    }

    [Fact]
    public async Task CheckedInChromeResetPackageIsReady()
    {
        await CopyCheckedInPackageAsync("chrome-reset");

        var status = await SingleStatusAsync();
        var published = Assert.Single(await CreateRepository().GetRepairPackagesAsync(
            TestContext.Current.CancellationToken));

        Assert.True(status.Status == BatPackageStatuses.Ready,
            string.Join(" ", status.Reasons));
        Assert.Equal("chrome-reset-settings", published.Problem);
        // 0 reset, 20 Chrome not installed, 21 no Chrome settings for this user.
        Assert.Equal([0, 20, 21], published.Descriptor.Playbook.SuccessExitCodes);
        Assert.Empty(published.Descriptor.SupportFiles ?? []);
    }

    /// <summary>
    /// Copies a checked-in package into the test repository, with stand-ins for vendor
    /// installers (not in git), and signs it with the test key.
    /// </summary>
    private async Task<string> CopyCheckedInPackageAsync(string id)
    {
        var source = Path.Combine(RepositoryRoot(), "deploy", "control-plane",
            "FileRepository", "packages", id);
        var folder = Directory.CreateDirectory(Path.Combine(_root, "packages", id)).FullName;
        File.Copy(Path.Combine(source, $"{id}.bat"), Path.Combine(folder, $"{id}.bat"));
        var package = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(source, "package.json"),
            TestContext.Current.CancellationToken))!.AsObject();
        Directory.CreateDirectory(Path.Combine(folder, "files"));
        foreach (var entry in package["supportFiles"]!.AsArray())
        {
            // Vendor installers are not in git; a stand-in takes their place.
            var name = entry!["fileName"]!.GetValue<string>();
            var checkedIn = Path.Combine(source, "files", name);
            var bytes = File.Exists(checkedIn) && !IsVendorInstaller(name)
                ? await File.ReadAllBytesAsync(checkedIn, TestContext.Current.CancellationToken)
                : Encoding.ASCII.GetBytes($"stand-in for {name}");
            await File.WriteAllBytesAsync(Path.Combine(folder, "files", name), bytes,
                TestContext.Current.CancellationToken);
            entry["sha256"] = Convert.ToHexString(SHA256.HashData(bytes));
        }
        package.Remove("signature");
        TestPackageSigner.Sign(package, folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "package.json"),
            package.ToJsonString(), TestContext.Current.CancellationToken);
        return folder;
    }

    [Fact]
    public async Task SoftwareInstallSkillListsEveryInstallPackage()
    {
        var root = RepositoryRoot();
        var skill = await File.ReadAllTextAsync(Path.Combine(root, "deploy", "hermes", "skills",
            "software-install", "SKILL.md"), TestContext.Current.CancellationToken);
        var packages = Directory.GetDirectories(Path.Combine(root, "deploy", "control-plane",
                "FileRepository", "packages"))
            .Select(directory => JsonNode.Parse(File.ReadAllText(
                Path.Combine(directory, "package.json")))!)
            .Where(package => package["problem"]?.GetValue<string>()
                .StartsWith("install-", StringComparison.Ordinal) == true)
            .ToList();

        Assert.Equal(InstallPackages().Count, packages.Count);
        foreach (var package in packages)
        {
            Assert.Contains($"`{package["problem"]!.GetValue<string>()}`", skill);
            Assert.Contains($"`{package["id"]!.GetValue<string>()}`", skill);
        }
    }

    private static bool IsVendorInstaller(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".msi" or ".exe" or ".msix"
            or ".msixbundle" or ".appx" or ".appxbundle";

    private static string RepositoryRoot()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "EndpointAgents.slnx")))
            root = Path.GetDirectoryName(root)!;
        return root;
    }

    private static BatchPlaybook Playbook(params BatchPlaybookStep[] steps) =>
        new(3, steps, Arguments: steps.Length == 0 ? ["/quiet"] : [],
            TimeoutSeconds: 120, RestartExitCodes: [3010]);

    private async Task<BatPackageStatus> SingleStatusAsync() =>
        Assert.Single(await CreateRepository().GetPackageStatusesAsync(
            TestContext.Current.CancellationToken));

    private FileArtifactRepository CreateRepository() => new(
        Options.Create(new FileRepositoryOptions
        {
            RootPath = _root, RequireProtectedAcl = false,
            TrustedPackageSigningKeys = [TestPackageSigner.PublicKey]
        }),
        new HostingEnvironment { ContentRootPath = _root });

    private async Task<string> WritePackageAsync(string id, string? exe = null,
        BatchPlaybook? playbook = null, (string Name, string Content)? supportFile = null,
        string? fileName = null, Action<JsonObject>? edit = null, bool sign = true,
        string? bat = null)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "packages", id)).FullName;
        fileName ??= exe is null ? "Vendor Tool.bat" : "Vendor Tool.exe";
        var bytes = exe is null ? Encoding.ASCII.GetBytes(bat ?? Bat)
            : await File.ReadAllBytesAsync(exe, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(folder, fileName), bytes,
            TestContext.Current.CancellationToken);
        var supportFiles = new JsonArray();
        if (supportFile is { } file)
        {
            Directory.CreateDirectory(Path.Combine(folder, "files"));
            var content = Encoding.ASCII.GetBytes(file.Content);
            await File.WriteAllBytesAsync(Path.Combine(folder, "files", file.Name), content,
                TestContext.Current.CancellationToken);
            supportFiles.Add(new JsonObject
            {
                ["fileName"] = file.Name,
                ["sha256"] = Convert.ToHexString(SHA256.HashData(content))
            });
        }
        playbook ??= Playbook();
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var package = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["id"] = id,
            ["displayName"] = "Reset the print spooler",
            ["description"] = "Clears stuck print jobs.",
            ["file"] = new JsonObject
            {
                ["type"] = exe is null ? "bat" : "exe",
                ["fileName"] = fileName,
                ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes))
            },
            ["symptoms"] = new JsonArray("Printing is stuck"),
            ["compatibility"] = new JsonObject
            {
                ["minWindowsVersion"] = "10.0.17763"
            },
            ["supportFiles"] = supportFiles,
            ["playbook"] = JsonSerializer.SerializeToNode(playbook, web),
            ["playbookSha256"] = BatchPlaybookPolicy.ComputeSha256(playbook)
        };
        edit?.Invoke(package);
        if (sign) TestPackageSigner.Sign(package, folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "package.json"),
            package.ToJsonString(), TestContext.Current.CancellationToken);
        return folder;
    }
}
