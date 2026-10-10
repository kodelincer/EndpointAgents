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
    private static readonly string ConsoleExe =
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
        Assert.Equal("Tool.bat", descriptor.FileName);
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
        await WritePackageAsync("console-tool", exe: ConsoleExe,
            playbook: Playbook(new BatchPlaybookStep("Continue?", "y", "key", 30)));

        var status = await SingleStatusAsync();

        Assert.True(BatPackageStatuses.IsPublished(status.Status),
            string.Join(" ", status.Reasons));
        Assert.Equal(PackageFileTypes.Exe, status.FileType);
        Assert.Equal(ExecutableInspector.Console, status.ExeSubsystem);
    }

    [Fact]
    public async Task GuiExeCanOnlyRunWithoutInput()
    {
        await WritePackageAsync("gui-prompts", exe: GuiExe,
            playbook: Playbook(new BatchPlaybookStep("Continue?", "y", "key", 30)));
        await WritePackageAsync("gui-silent", exe: GuiExe);

        var statuses = await CreateRepository().GetPackageStatusesAsync(
            TestContext.Current.CancellationToken);

        var prompts = statuses.Single(item => item.Id == "gui-prompts");
        Assert.Equal(BatPackageStatuses.InvalidPlaybook, prompts.Status);
        Assert.Contains(prompts.Reasons, reason => reason.Contains("GUI"));
        Assert.True(BatPackageStatuses.IsPublished(
            statuses.Single(item => item.Id == "gui-silent").Status));
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
        await WritePackageAsync("shadow", supportFile: ("Tool.bat", Bat));

        var status = await SingleStatusAsync();

        Assert.Equal(BatPackageStatuses.Invalid, status.Status);
    }

    [Fact]
    public async Task GeneralPackageMayShareAnIdWithAnActivationRepair()
    {
        await WritePackageAsync("windows-activation-repair");

        Assert.True(BatPackageStatuses.IsPublished((await SingleStatusAsync()).Status));
    }

    private static BatchPlaybook Playbook(params BatchPlaybookStep[] steps) =>
        new(3, steps, Arguments: steps.Length == 0 ? ["/quiet"] : [],
            TimeoutSeconds: 120, RestartExitCodes: [3010]);

    private async Task<BatPackageStatus> SingleStatusAsync() =>
        Assert.Single(await CreateRepository().GetPackageStatusesAsync(
            TestContext.Current.CancellationToken));

    private FileArtifactRepository CreateRepository() => new(
        Options.Create(new FileRepositoryOptions { RootPath = _root, RequireProtectedAcl = false }),
        new HostingEnvironment { ContentRootPath = _root });

    private async Task<string> WritePackageAsync(string id, string? exe = null,
        BatchPlaybook? playbook = null, (string Name, string Content)? supportFile = null,
        string? fileName = null, Action<JsonObject>? edit = null)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "packages", id)).FullName;
        fileName ??= exe is null ? "Tool.bat" : "Tool.exe";
        var bytes = exe is null ? Encoding.ASCII.GetBytes(Bat)
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
        await File.WriteAllTextAsync(Path.Combine(folder, "package.json"),
            package.ToJsonString(), TestContext.Current.CancellationToken);
        return folder;
    }
}
