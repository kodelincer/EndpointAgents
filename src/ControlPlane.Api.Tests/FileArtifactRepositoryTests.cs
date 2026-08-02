using ControlPlane.Api.Files;
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
                Options.Create(new FileRepositoryOptions { RootPath = root }),
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
    public async Task LoadsSingleOfficeBugfixBatchAndComputesSha256()
    {
        var root = CreateRoot();
        try
        {
            var bytes = "@echo off\r\nexit /b 0\r\n"u8.ToArray();
            var fileName = "office-activation-repair.bat";
            await File.WriteAllBytesAsync(Path.Combine(root, fileName), bytes,
                TestContext.Current.CancellationToken);

            var catalog = await CreateRepository(root).GetOfficeBugfixCatalogAsync(
                TestContext.Current.CancellationToken);

            var artifact = Assert.Single(catalog.Artifacts);
            Assert.Equal("office-activation-repair", artifact.Descriptor.ArtifactId);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)),
                artifact.Descriptor.Sha256);
            Assert.Equal("/api/files/office/bugfix/office-activation-repair",
                artifact.Descriptor.DownloadPath);
            Assert.Empty(catalog.Issues);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task IgnoresOtherBatchFileNames()
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

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"EndpointAgents-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static FileArtifactRepository CreateRepository(string root) => new(
        Options.Create(new FileRepositoryOptions { RootPath = root }),
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

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "ControlPlane.Api.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
