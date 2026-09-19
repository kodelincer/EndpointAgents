using ControlPlane.Api.Files;
using EndpointAgents.Shared.Office;
using EndpointAgents.Shared.Windows;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text.Json;

namespace ControlPlane.Api.Tests;

public sealed class FileArtifactRepositoryTests
{
    [Fact]
    public async Task SelectsExactApprovedDialerAndComputesSha256()
    {
        var root = Path.Combine(Path.GetTempPath(), $"EndpointAgents-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var decoy = Path.Combine(root, "IBSngDialer-newer.exe");
            var approved = Path.Combine(root, "IBSngDialer-pap(1).exe");
            await File.WriteAllBytesAsync(decoy, [0x4d, 0x5a, 0x01], TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(approved, [0x4d, 0x5a, 0x02], TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(decoy, DateTime.UtcNow);

            var repository = new FileArtifactRepository(
                Options.Create(new FileRepositoryOptions
                    { RootPath = root, RequireProtectedAcl = false }),
                new TestHostEnvironment(root));
            var artifact = await repository.GetIbsngDialerAsync(TestContext.Current.CancellationToken);

            Assert.NotNull(artifact);
            Assert.Equal("IBSngDialer-pap(1).exe", artifact.FileName);
            Assert.Equal(Convert.ToHexString(SHA256.HashData([0x4d, 0x5a, 0x02])), artifact.Sha256);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadsOnlyHashVerifiedOfficeArtifacts()
    {
        var root = CreateRoot();
        try
        {
            var officeRoot = Directory.CreateDirectory(Path.Combine(root, "Office")).FullName;
            var bytes = new byte[] { 1, 2, 3, 4 };
            var fileName = "m365-reset.ps1";
            await File.WriteAllBytesAsync(Path.Combine(officeRoot, fileName), bytes,
                TestContext.Current.CancellationToken);
            await WriteManifestAsync(officeRoot,
                Entry("m365-reset", fileName, Convert.ToHexString(SHA256.HashData(bytes))));
            var repository = CreateRepository(root);

            var catalog = await repository.GetOfficeCatalogAsync(
                TestContext.Current.CancellationToken);

            var artifact = Assert.Single(catalog.Artifacts);
            Assert.Equal("m365-reset", artifact.Descriptor.ArtifactId);
            Assert.Equal("/api/files/office/m365-reset", artifact.Descriptor.DownloadPath);
            Assert.Empty(catalog.Issues);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RejectsHashMismatchAndPathTraversal()
    {
        var root = CreateRoot();
        try
        {
            var officeRoot = Directory.CreateDirectory(Path.Combine(root, "Office")).FullName;
            await File.WriteAllTextAsync(Path.Combine(officeRoot, "wrong.ps1"), "content",
                TestContext.Current.CancellationToken);
            await WriteManifestAsync(officeRoot,
                Entry("wrong-hash", "wrong.ps1", new string('A', 64)),
                Entry("escape", "..\\escape.ps1", new string('B', 64)));

            var catalog = await CreateRepository(root).GetOfficeCatalogAsync(
                TestContext.Current.CancellationToken);

            Assert.Empty(catalog.Artifacts);
            Assert.Equal(2, catalog.Issues.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RejectsEveryEntryWithDuplicateArtifactId()
    {
        var root = CreateRoot();
        try
        {
            var officeRoot = Directory.CreateDirectory(Path.Combine(root, "Office")).FullName;
            await WriteManifestAsync(officeRoot,
                Entry("duplicate", "one.ps1", new string('A', 64)),
                Entry("duplicate", "two.ps1", new string('B', 64)));

            var catalog = await CreateRepository(root).GetOfficeCatalogAsync(
                TestContext.Current.CancellationToken);

            Assert.Empty(catalog.Artifacts);
            Assert.Single(catalog.Issues);
            Assert.Contains("Duplicate", catalog.Issues[0].Message);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("{\"version\":1,\"artifacts\":null}", "collection")]
    [InlineData("{\"version\":1,\"artifacts\":[null]}", "entry")]
    public async Task RejectsNullRegularOfficeManifestDataWithoutFailingTheCatalog(
        string json, string expectedMessage)
    {
        var root = CreateRoot();
        try
        {
            var officeRoot = Directory.CreateDirectory(Path.Combine(root, "Office"))
                .FullName;
            await File.WriteAllTextAsync(Path.Combine(officeRoot,
                "office-artifacts.json"), json, TestContext.Current.CancellationToken);

            var catalog = await CreateRepository(root).GetOfficeCatalogAsync(
                TestContext.Current.CancellationToken);

            Assert.Empty(catalog.Artifacts);
            var issue = Assert.Single(catalog.Issues);
            Assert.Contains(expectedMessage, issue.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CatalogRejectsBatchFilesAndAcceptsMicrosoftExecutableKind()
    {
        var root = CreateRoot();
        try
        {
            var officeRoot = Directory.CreateDirectory(Path.Combine(root, "Office")).FullName;
            var bytes = new byte[] { (byte)'M', (byte)'Z', 1, 2 };
            await File.WriteAllBytesAsync(Path.Combine(officeRoot, "repair.exe"), bytes,
                TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(officeRoot, "unsafe.bat"), bytes,
                TestContext.Current.CancellationToken);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var executable = Entry("m365-repair", "repair.exe", hash);
            executable.Kind = "MicrosoftSignedExecutable";
            var batch = Entry("unsafe-batch", "unsafe.bat", hash);
            batch.Kind = "MicrosoftSignedExecutable";
            await WriteManifestAsync(officeRoot, executable, batch);

            var catalog = await CreateRepository(root).GetOfficeCatalogAsync(
                TestContext.Current.CancellationToken);

            Assert.Equal("m365-repair", Assert.Single(catalog.Artifacts).Descriptor.ArtifactId);
            Assert.Contains(catalog.Issues, issue => issue.Source == "office.unsafe-batch");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LoadsMultipleHashPinnedOfficeBugfixBatches()
    {
        var root = CreateRoot();
        try
        {
            var firstBytes = "@echo off\r\nexit /b 0\r\n"u8.ToArray();
            var secondBytes = "@echo off\r\nexit /b 1\r\n"u8.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(root, "office-repair-15.bat"),
                firstBytes, TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(root, "office-repair-16.bat"),
                secondBytes, TestContext.Current.CancellationToken);
            await WriteBugfixManifestAsync(root,
                BugfixEntry("office-repair-15", "office-repair-15.bat",
                    Convert.ToHexString(SHA256.HashData(firstBytes)), "15.0", "16.0"),
                BugfixEntry("office-repair-16", "office-repair-16.bat",
                    Convert.ToHexString(SHA256.HashData(secondBytes)), "16.0", "17.0"));

            var catalog = await CreateRepository(root).GetOfficeBugfixCatalogAsync(
                TestContext.Current.CancellationToken);

            Assert.Equal(2, catalog.Artifacts.Count);
            Assert.Equal("office-repair-15", catalog.Artifacts[0].Descriptor.ArtifactId);
            Assert.Equal("office-repair-16", catalog.Artifacts[1].Descriptor.ArtifactId);
            Assert.Equal("/api/files/office/bugfix/office-repair-16",
                catalog.Artifacts[1].Descriptor.DownloadPath);
            Assert.Equal(2, catalog.Artifacts[1].Descriptor.Playbook.Steps.Count);
            Assert.Equal(BatchPlaybookPolicy.ComputeSha256(
                catalog.Artifacts[1].Descriptor.Playbook),
                catalog.Artifacts[1].Descriptor.PlaybookSha256);
            Assert.Empty(catalog.Issues);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LoadsSeededOffice16BatAndRejectsLegacyExeEntry()
    {
        var root = CreateRoot();
        try
        {
            var batBytes = "@echo off\r\nexit /b 0\r\n"u8.ToArray();
            var exeBytes = new byte[] { (byte)'M', (byte)'Z', 1, 2 };
            await File.WriteAllBytesAsync(Path.Combine(root,
                "office-activation-repair.bat"), batBytes,
                TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(root,
                "officebugfixmanager_16.0.exe"), exeBytes,
                TestContext.Current.CancellationToken);
            var legacy = BugfixEntry("sample-office-bugfix-16",
                "officebugfixmanager_16.0.exe",
                Convert.ToHexString(SHA256.HashData(exeBytes)), "16.0", "17.0");
            await WriteBugfixManifestAsync(root,
                BugfixEntry("office-activation-repair",
                    "office-activation-repair.bat",
                    Convert.ToHexString(SHA256.HashData(batBytes)), "16.0", "17.0"),
                legacy);

            var catalog = await CreateRepository(root).GetOfficeBugfixCatalogAsync(
                TestContext.Current.CancellationToken);

            Assert.Equal("office-activation-repair",
                Assert.Single(catalog.Artifacts).Descriptor.ArtifactId);
            Assert.Contains(catalog.Issues,
                issue => issue.Source == "office_bugfix.sample-office-bugfix-16");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task IgnoresUnmanifestedOfficeBatchFiles()
    {
        var root = CreateRoot();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "other.bat"), "@echo off",
                TestContext.Current.CancellationToken);

            var catalog = await CreateRepository(root).GetOfficeBugfixCatalogAsync(
                TestContext.Current.CancellationToken);

            Assert.Empty(catalog.Artifacts);
            Assert.Empty(catalog.Issues);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ProtectedRepositoryModeRejectsAUserOwnedDevelopmentDirectory()
    {
        var root = CreateRoot();
        try
        {
            var repository = new FileArtifactRepository(Options.Create(
                new FileRepositoryOptions
                {
                    RootPath = root,
                    RequireProtectedAcl = true
                }), new TestHostEnvironment(root));

            var catalog = await repository.GetOfficeBugfixCatalogAsync(
                TestContext.Current.CancellationToken);

            Assert.Empty(catalog.Artifacts);
            var issue = Assert.Single(catalog.Issues);
            Assert.Contains("protected", issue.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RejectsOfficeBugfixHashMismatchAndUnsafeFilename()
    {
        var root = CreateRoot();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "wrong.bat"), "@echo off",
                TestContext.Current.CancellationToken);
            await WriteBugfixManifestAsync(root,
                BugfixEntry("wrong", "wrong.bat", new string('A', 64), "16.0", "17.0"),
                BugfixEntry("unsafe", "different-name.bat", new string('B', 64),
                    "16.0", "17.0"));

            var catalog = await CreateRepository(root).GetOfficeBugfixCatalogAsync(
                TestContext.Current.CancellationToken);

            Assert.Empty(catalog.Artifacts);
            Assert.Equal(2, catalog.Issues.Count);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RejectsNullOfficeBugfixManifestEntryWithoutFailingTheCatalog()
    {
        var root = CreateRoot();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root,
                "office-bugfix-managers.json"),
                """{"version":1,"artifacts":[null]}""",
                TestContext.Current.CancellationToken);

            var catalog = await CreateRepository(root).GetOfficeBugfixCatalogAsync(
                TestContext.Current.CancellationToken);

            Assert.Empty(catalog.Artifacts);
            var issue = Assert.Single(catalog.Issues);
            Assert.Contains("null manifest entry", issue.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RejectsOfficeBugfixWithoutAPlaybook()
    {
        var root = CreateRoot();
        try
        {
            var bytes = "@echo off\r\nexit /b 0\r\n"u8.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(root, "missing-playbook.bat"),
                bytes, TestContext.Current.CancellationToken);
            var entry = BugfixEntry("missing-playbook", "missing-playbook.bat",
                Convert.ToHexString(SHA256.HashData(bytes)), "16.0", "17.0");
            entry.Playbook = null;
            await WriteBugfixManifestAsync(root, entry);

            var catalog = await CreateRepository(root).GetOfficeBugfixCatalogAsync(
                TestContext.Current.CancellationToken);

            Assert.Empty(catalog.Artifacts);
            Assert.Contains("playbook", Assert.Single(catalog.Issues).Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    public static TheoryData<BatchPlaybook> InvalidPlaybooks => new()
    {
        new BatchPlaybook(1,
            [new BatchPlaybookStep("Prompt", "1", "press", 30)]),
        new BatchPlaybook(1,
            [new BatchPlaybookStep("  \r\n", "1", "key", 30)]),
        new BatchPlaybook(1,
            [new BatchPlaybookStep("Prompt", "12", "key", 30)]),
        new BatchPlaybook(1,
            [new BatchPlaybookStep("Prompt", "Y\r", "line", 30)]),
        new BatchPlaybook(1,
            [new BatchPlaybookStep("Prompt", "1", "key", 4)]),
        new BatchPlaybook(1,
            [new BatchPlaybookStep("Prompt", "1", "key", 301)]),
        new BatchPlaybook(1,
            [new BatchPlaybookStep("Prompt", new string('X', 65), "line", 30)]),
        new BatchPlaybook(1,
            [new BatchPlaybookStep("Prompt", "AAAAA-BBBBB-CCCCC-DDDDD-EEEEE", "line", 30)]),
        new BatchPlaybook(1, []),
        new BatchPlaybook(1, Enumerable.Range(0, 33)
            .Select(index => new BatchPlaybookStep($"Prompt {index}", "1", "key", 30))
            .ToArray())
    };

    [Theory]
    [MemberData(nameof(InvalidPlaybooks))]
    public void RejectsInvalidOfficeBugfixPlaybooks(BatchPlaybook playbook)
    {
        Assert.False(BatchPlaybookPolicy.TryNormalize(playbook, out _, out _));
    }

    [Fact]
    public void PlaybookHashIsStableForNormalizedStepsAndChangesWithBehavior()
    {
        var first = new BatchPlaybook(1,
        [
            new BatchPlaybookStep(" Select   an option \r\n", "1", "LINE", 30),
            new BatchPlaybookStep("Continue", "Y", "key", 10)
        ]);
        var equivalent = new BatchPlaybook(1,
        [
            new BatchPlaybookStep("Select an option", "1", "line", 30),
            new BatchPlaybookStep("Continue", "Y", "key", 10)
        ]);
        var changed = equivalent with
        {
            Steps =
            [
                equivalent.Steps[0],
                equivalent.Steps[1] with { Send = "N" }
            ]
        };

        Assert.Equal(BatchPlaybookPolicy.ComputeSha256(first),
            BatchPlaybookPolicy.ComputeSha256(equivalent));
        Assert.NotEqual(BatchPlaybookPolicy.ComputeSha256(equivalent),
            BatchPlaybookPolicy.ComputeSha256(changed));
        Assert.True(BatchPlaybookPolicy.TryNormalize(new BatchPlaybook(1,
            [new BatchPlaybookStep("Select [1].*", "1", "key", 30)]),
            out _, out _));
    }

    [Fact]
    public async Task LoadsOnlyFixedWindowsBugfixAndComputesSha256()
    {
        var root = CreateRoot();
        try
        {
            var windows = Directory.CreateDirectory(Path.Combine(root, "Windows")).FullName;
            var bytes = "@echo off\r\nexit /b 0\r\n"u8.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(windows, "windowsbugfixer.bat"),
                bytes, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(windows, "other.bat"), "@echo off",
                TestContext.Current.CancellationToken);

            var catalog = await CreateRepository(root).GetWindowsBugfixCatalogAsync(
                TestContext.Current.CancellationToken);

            var artifact = Assert.Single(catalog.Artifacts);
            Assert.Equal("windows-activation-repair", artifact.Descriptor.ArtifactId);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)),
                artifact.Descriptor.Sha256);
            Assert.Equal("/api/files/windows/bugfix/windows-activation-repair",
                artifact.Descriptor.DownloadPath);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ExposesTheFixedHarmlessWindowsHelloSample()
    {
        var root = CreateRoot();
        try
        {
            var windows = Directory.CreateDirectory(Path.Combine(root, "Windows")).FullName;
            var bytes = "@echo off\r\necho Hello\r\npause\r\n"u8.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(windows,
                WindowsHelloSamplePaths.FileName), bytes,
                TestContext.Current.CancellationToken);

            var catalog = await CreateRepository(root).GetWindowsBugfixCatalogAsync(
                TestContext.Current.CancellationToken);

            var artifact = Assert.Single(catalog.Artifacts);
            Assert.Equal(WindowsHelloSamplePaths.ArtifactId,
                artifact.Descriptor.ArtifactId);
            Assert.Equal(WindowsHelloSamplePaths.FileName,
                artifact.Descriptor.FileName);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)),
                artifact.Descriptor.Sha256);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"EndpointAgents-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static FileArtifactRepository CreateRepository(string root) => new(
        Options.Create(new FileRepositoryOptions
            { RootPath = root, RequireProtectedAcl = false }),
        new TestHostEnvironment(root));

    private static OfficeArtifactManifestEntry Entry(string id, string fileName, string sha256) =>
        new()
        {
            ArtifactId = id,
            DisplayName = id,
            Description = "Approved test remediation",
            FileName = fileName,
            Sha256 = sha256,
            Kind = "PowerShellScript",
            SupportedProductFamilies = ["Microsoft365Apps"],
            ActivationStates = ["Unlicensed"]
        };

    private static async Task WriteManifestAsync(string officeRoot,
        params OfficeArtifactManifestEntry[] entries)
    {
        await using var stream = File.Create(Path.Combine(officeRoot, "office-artifacts.json"));
        await JsonSerializer.SerializeAsync(stream,
            new OfficeArtifactManifest { Version = 1, Artifacts = [.. entries] },
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static OfficeBugfixManifestEntry BugfixEntry(string id, string fileName,
        string sha256, string minimum, string maximumExclusive) => new()
        {
            ArtifactId = id,
            DisplayName = id,
            Description = "Approved test Office repair",
            FileName = fileName,
            Sha256 = sha256,
            MinOfficeVersion = minimum,
            MaxOfficeVersionExclusive = maximumExclusive,
            Playbook = TestPlaybook()
        };

    private static BatchPlaybook TestPlaybook() => new(1,
    [
        new BatchPlaybookStep("Select an option", "1", "line", 30),
        new BatchPlaybookStep("Continue", "Y", "key", 30)
    ]);

    private static async Task WriteBugfixManifestAsync(string root,
        params OfficeBugfixManifestEntry[] entries)
    {
        await using var stream = File.Create(Path.Combine(root,
            "office-bugfix-managers.json"));
        await JsonSerializer.SerializeAsync(stream,
            new OfficeBugfixManifest { Version = 1, Artifacts = [.. entries] },
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ControlPlane.Api.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
