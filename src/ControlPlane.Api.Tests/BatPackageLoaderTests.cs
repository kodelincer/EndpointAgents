using ControlPlane.Api.Files;
using EndpointAgents.Shared.Office;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ControlPlane.Api.Tests;

public sealed class BatPackageLoaderTests : IDisposable
{
    private const string Bat = "@echo off\r\nset /p \"x=Select an option: \"\r\nexit /b 0\r\n";
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        $"EndpointAgents-packages-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ValidOfficePackageIsReadyAndJoinsTheOfficeCatalog()
    {
        await WritePackageAsync("menu-fix");

        var repository = CreateRepository();
        var status = Assert.Single(await repository.GetPackageStatusesAsync(
            TestContext.Current.CancellationToken));
        var catalog = await repository.GetOfficeBugfixCatalogAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(BatPackageStatuses.Ready, status.Status);
        Assert.Empty(status.Reasons);
        var artifact = Assert.Single(catalog.Artifacts);
        Assert.Equal("menu-fix", artifact.Descriptor.ArtifactId);
        // Endpoints stage the BAT as <id>.bat, whatever its name in the package.
        Assert.Equal("menu-fix.bat", artifact.Descriptor.FileName);
        Assert.Equal("Vendor Tool.bat", artifact.File.FileName);
        Assert.Equal("/api/files/office/bugfix/menu-fix", artifact.Descriptor.DownloadPath);
        Assert.Equal(BatchPlaybookPolicy.ComputeSha256(Playbook()),
            artifact.Descriptor.PlaybookSha256);
        Assert.Empty((await repository.GetWindowsBugfixCatalogAsync(
            TestContext.Current.CancellationToken)).Artifacts);
    }

    [Fact]
    public async Task WindowsPackageWithSupportFilesJoinsTheWindowsCatalog()
    {
        var folder = await WritePackageAsync("win-fix", windows: true,
            supportFile: ("helper.txt", "data"));

        var repository = CreateRepository();
        var artifact = Assert.Single((await repository.GetWindowsBugfixCatalogAsync(
            TestContext.Current.CancellationToken)).Artifacts);
        var support = await repository.GetWindowsBugfixSupportFileAsync("win-fix",
            "helper.txt", TestContext.Current.CancellationToken);

        Assert.Equal("win-fix", artifact.Descriptor.ArtifactId);
        Assert.NotNull(artifact.Descriptor.BundleSha256);
        Assert.Equal(Path.Combine(folder, "files", "helper.txt"), support!.FullPath);
    }

    [Fact]
    public async Task ChangedBatIsAHashMismatchAndIsNotPublished()
    {
        var folder = await WritePackageAsync("menu-fix");
        await File.AppendAllTextAsync(Path.Combine(folder, "Vendor Tool.bat"), "rem changed\r\n",
            TestContext.Current.CancellationToken);

        var status = await SingleStatusAsync();

        Assert.Equal(BatPackageStatuses.HashMismatch, status.Status);
        Assert.Contains(status.Reasons, reason => reason.Contains("changed after review"));
        Assert.Empty((await CreateRepository().GetOfficeBugfixCatalogAsync(
            TestContext.Current.CancellationToken)).Artifacts);
    }

    [Fact]
    public async Task InvalidPlaybookIsReported()
    {
        await WritePackageAsync("menu-fix", edit: package =>
            package["playbook"]!["steps"]![0]!["timeoutSeconds"] = 1);

        var status = await SingleStatusAsync();

        Assert.Equal(BatPackageStatuses.InvalidPlaybook, status.Status);
        Assert.Contains(status.Reasons, reason => reason.StartsWith("Playbook:"));
    }

    [Fact]
    public async Task EditedPlaybookWithoutNewHashIsReported()
    {
        await WritePackageAsync("menu-fix", edit: package =>
            package["playbook"]!["steps"]![0]!["send"] = "2");

        var status = await SingleStatusAsync();

        Assert.Equal(BatPackageStatuses.InvalidPlaybook, status.Status);
        Assert.Contains(status.Reasons, reason => reason.Contains("update playbookSha256"));
    }

    [Theory]
    [InlineData("other-id", "must equal the folder name")]
    [InlineData("TODO", "TODO markers")]
    public async Task IdMustMatchTheFolderAndTodoMarkersBlockPublishing(string id,
        string expected)
    {
        await WritePackageAsync("menu-fix", edit: package => package["id"] = id);

        var status = await SingleStatusAsync();

        Assert.Equal(BatPackageStatuses.Invalid, status.Status);
        Assert.Contains(expected, Assert.Single(status.Reasons));
    }

    [Fact]
    public async Task PackageNeedsExactlyOneCompatibilitySection()
    {
        await WritePackageAsync("menu-fix", edit: package =>
            package["windows"] = new JsonObject());

        var status = await SingleStatusAsync();

        Assert.Equal(BatPackageStatuses.Invalid, status.Status);
    }

    [Fact]
    public async Task LintWarningsPublishAndLintErrorsReject()
    {
        await WritePackageAsync("warned", bat: Bat + "timeout /t 2\r\n");
        await WritePackageAsync("rejected", bat: Bat + "powershell -enc SQBFAFgA\r\n");

        var statuses = await CreateRepository().GetPackageStatusesAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(BatPackageStatuses.ReadyWithWarnings,
            statuses.Single(item => item.Id == "warned").Status);
        Assert.Equal(BatPackageStatuses.LintRejected,
            statuses.Single(item => item.Id == "rejected").Status);
    }

    [Fact]
    public async Task PackageCannotReuseALegacyManifestId()
    {
        await WritePackageAsync("menu-fix");
        var bytes = Encoding.ASCII.GetBytes(Bat);
        await File.WriteAllBytesAsync(Path.Combine(_root, "menu-fix.bat"), bytes,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_root, "office-bugfix-managers.json"),
            JsonSerializer.Serialize(new OfficeBugfixManifest
            {
                Artifacts = [new OfficeBugfixManifestEntry
                {
                    ArtifactId = "menu-fix", DisplayName = "Legacy", Description = "Legacy",
                    FileName = "menu-fix.bat",
                    Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                    MinOfficeVersion = "16.0", MaxOfficeVersionExclusive = "17.0",
                    Playbook = Playbook()
                }]
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            TestContext.Current.CancellationToken);

        var repository = CreateRepository();
        var catalog = await repository.GetOfficeBugfixCatalogAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal("Legacy", Assert.Single(catalog.Artifacts).Descriptor.DisplayName);
        Assert.Contains(catalog.Issues, issue => issue.Source == "package.menu-fix");
        Assert.Equal(BatPackageStatuses.Invalid, (await SingleStatusAsync()).Status);
    }

    [Fact]
    public async Task CheckedInDevelopmentPackageIsReady()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "EndpointAgents.slnx")))
            root = Path.GetDirectoryName(root)!;
        // The checked-in packages are signed with the development key that
        // appsettings.Development.json trusts.
        using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root,
            "src", "ControlPlane.Api", "appsettings.Development.json"),
            TestContext.Current.CancellationToken));
        var devKeys = settings.RootElement.GetProperty("FileRepository")
            .GetProperty("TrustedPackageSigningKeys").EnumerateArray()
            .Select(key => key.GetString()!).ToList();
        var repository = new FileArtifactRepository(Options.Create(new FileRepositoryOptions
        {
            RootPath = Path.Combine(root, "deploy", "control-plane", "FileRepository"),
            RequireProtectedAcl = false,
            TrustedPackageSigningKeys = devKeys
        }), new HostingEnvironment { ContentRootPath = root });

        var statuses = await repository.GetPackageStatusesAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(["adobe-connect-install", "chrome-install", "foxit-editor-install",
                "foxit-reader-install", "no-input-repair-sample", "office-activation-repair",
                "persian-fonts-install", "photos-install",
                "windows-activation-retry"],
            statuses.Select(status => status.Id).Order(StringComparer.Ordinal));
        // Install packages need vendor installers that are not in git and an administrator's
        // signature (docs/software-install.md); RepairPackageLoaderTests covers them.
        foreach (var status in statuses.Where(status =>
                     !status.Id.EndsWith("-install", StringComparison.Ordinal)))
            Assert.True(status.Status == BatPackageStatuses.Ready,
                $"{status.Id}: {string.Join(" ", status.Reasons)}");
        Assert.Contains("no-input-repair-sample", (await repository.GetRepairPackagesAsync(
                TestContext.Current.CancellationToken))
            .Select(package => package.Descriptor.PackageId));
    }

    private static BatchPlaybook Playbook() => new(2,
        [new BatchPlaybookStep("Select an option", "1", "line", 30)],
        SuccessMarker: "Done");

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

    private async Task<string> WritePackageAsync(string id, bool windows = false,
        (string Name, string Content)? supportFile = null, string bat = Bat,
        Action<JsonObject>? edit = null, bool sign = true)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "packages", id)).FullName;
        var batBytes = Encoding.ASCII.GetBytes(bat);
        await File.WriteAllBytesAsync(Path.Combine(folder, "Vendor Tool.bat"), batBytes,
            TestContext.Current.CancellationToken);
        var supportFiles = new JsonArray();
        if (supportFile is { } file)
        {
            Directory.CreateDirectory(Path.Combine(folder, "files"));
            var bytes = Encoding.ASCII.GetBytes(file.Content);
            await File.WriteAllBytesAsync(Path.Combine(folder, "files", file.Name), bytes,
                TestContext.Current.CancellationToken);
            supportFiles.Add(new JsonObject
            {
                ["fileName"] = file.Name,
                ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes))
            });
        }
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var package = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = id,
            ["displayName"] = "Menu fix",
            ["description"] = "Test package",
            ["bat"] = new JsonObject
            {
                ["fileName"] = "Vendor Tool.bat",
                ["sha256"] = Convert.ToHexString(SHA256.HashData(batBytes))
            },
            ["supportFiles"] = supportFiles,
            ["playbook"] = JsonSerializer.SerializeToNode(Playbook(), web),
            ["playbookSha256"] = BatchPlaybookPolicy.ComputeSha256(Playbook())
        };
        package[windows ? "windows" : "office"] = windows
            ? new JsonObject
            {
                ["minWindowsVersion"] = "10.0.19041",
                ["maxWindowsVersionExclusive"] = "10.0.30000",
                ["editionIds"] = new JsonArray()
            }
            : new JsonObject
            {
                ["minOfficeVersion"] = "16.0",
                ["maxOfficeVersionExclusive"] = "17.0",
                ["officeProductReleaseIds"] = new JsonArray()
            };
        edit?.Invoke(package);
        if (sign) TestPackageSigner.Sign(package, folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "package.json"),
            package.ToJsonString(), TestContext.Current.CancellationToken);
        return folder;
    }
}
