using EndpointAgents.Platform.Windows.Ibsng;
using System.Security.Cryptography;

namespace EndpointAgents.Platform.Windows.Tests;

public sealed class IbsngArtifactTests
{
    [Fact]
    public void RasInspectorReturnsStructuredSnapshot()
    {
        var connections = new RasConnectionInspector().GetActiveConnections();

        Assert.NotNull(connections);
    }

    [Fact]
    public async Task VerifierRecognizesApprovedPortableExecutable()
    {
        var path = GetTemporaryPath();
        try
        {
            var bytes = new byte[] { (byte)'M', (byte)'Z', 1, 2, 3, 4 };
            await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
            var trustedHash = Convert.ToHexString(SHA256.HashData(bytes));

            var inspection = await new IbsngArtifactVerifier().InspectAsync(path, trustedHash,
                TestContext.Current.CancellationToken);

            Assert.True(inspection.Exists);
            Assert.True(inspection.IsPortableExecutable);
            Assert.True(inspection.IsTrusted);
            Assert.Equal("Approved", inspection.State);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task VerifierRejectsWrongHashAndInvalidExecutable()
    {
        var path = GetTemporaryPath();
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4],
                TestContext.Current.CancellationToken);
            var inspection = await new IbsngArtifactVerifier().InspectAsync(path,
                new string('A', 64), TestContext.Current.CancellationToken);

            Assert.False(inspection.IsPortableExecutable);
            Assert.False(inspection.IsTrusted);
            Assert.Equal("InvalidExecutable", inspection.State);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void QuarantineMovesArtifactOutOfDesktopLocation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"EndpointAgents-{Guid.NewGuid():N}");
        var desktop = Path.Combine(root, "Desktop");
        var quarantine = Path.Combine(root, "Quarantine");
        Directory.CreateDirectory(desktop);
        var source = Path.Combine(desktop, "IBSngDialer-pap(1).exe");
        File.WriteAllBytes(source, [1, 2, 3]);

        try
        {
            var destination = new IbsngArtifactQuarantine().Move(source, quarantine);

            Assert.False(File.Exists(source));
            Assert.True(File.Exists(destination));
            Assert.StartsWith(Path.GetFullPath(quarantine), Path.GetFullPath(destination),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AtomicInstallerMovesVerifiedStagingFileWithinDesktopDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"EndpointAgents-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, ".dialer.download");
        var destination = Path.Combine(root, "IBSngDialer-pap(1).exe");
        File.WriteAllBytes(staging, [(byte)'M', (byte)'Z']);

        try
        {
            new IbsngAtomicInstaller().Install(staging, destination);

            Assert.False(File.Exists(staging));
            Assert.True(File.Exists(destination));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReconcilerKeepsCanonicalAndQuarantinesDuplicateWithoutTouchingUnrelatedExe()
    {
        var root = Path.Combine(Path.GetTempPath(), $"EndpointAgents-{Guid.NewGuid():N}");
        var desktop = Path.Combine(root, "Desktop");
        var quarantine = Path.Combine(root, "Quarantine");
        Directory.CreateDirectory(desktop);
        var approvedBytes = new byte[] { (byte)'M', (byte)'Z', 1, 2, 3, 4 };
        var trustedHash = Convert.ToHexString(SHA256.HashData(approvedBytes));
        var canonical = Path.Combine(desktop, "IBSngDialer-pap(1).exe");
        var duplicate = Path.Combine(desktop, "IBSngDialer-copy.exe");
        var unrelated = Path.Combine(desktop, "Accounting.exe");
        await File.WriteAllBytesAsync(canonical, approvedBytes,
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(duplicate, approvedBytes,
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(unrelated, [(byte)'M', (byte)'Z', 9, 9],
            TestContext.Current.CancellationToken);

        try
        {
            var verifier = new IbsngArtifactVerifier();
            var reconciler = new IbsngDesktopReconciler(verifier,
                new IbsngArtifactQuarantine());
            var options = new IbsngOptions
            {
                DialerFileName = Path.GetFileName(canonical),
                DialerSearchPattern = "IBSngDialer*.exe",
                TrustedSha256 = trustedHash,
                QuarantineRoot = quarantine
            };

            var result = await reconciler.ReconcileAsync(desktop, options,
                TestContext.Current.CancellationToken);

            Assert.True(result.CanonicalArtifact.IsTrusted);
            Assert.True(File.Exists(canonical));
            Assert.False(File.Exists(duplicate));
            Assert.True(File.Exists(unrelated));
            Assert.Single(result.QuarantinedPaths);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReconcilerFindsApprovedCopyEvenWhenItWasRenamed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"EndpointAgents-{Guid.NewGuid():N}");
        var desktop = Path.Combine(root, "Desktop");
        var quarantine = Path.Combine(root, "Quarantine");
        Directory.CreateDirectory(desktop);
        var approvedBytes = new byte[] { (byte)'M', (byte)'Z', 5, 6, 7, 8 };
        var trustedHash = Convert.ToHexString(SHA256.HashData(approvedBytes));
        var renamed = Path.Combine(desktop, "renamed-tool.exe");
        await File.WriteAllBytesAsync(renamed, approvedBytes,
            TestContext.Current.CancellationToken);

        try
        {
            var verifier = new IbsngArtifactVerifier();
            var reconciler = new IbsngDesktopReconciler(verifier,
                new IbsngArtifactQuarantine());
            var options = new IbsngOptions
            {
                TrustedSha256 = trustedHash,
                QuarantineRoot = quarantine
            };

            var inventory = await reconciler.InspectAsync(desktop, options,
                TestContext.Current.CancellationToken);

            var duplicate = Assert.Single(inventory.Duplicates);
            Assert.Equal(renamed, duplicate.Path, ignoreCase: true);
            Assert.False(duplicate.NameMatches);
            Assert.True(duplicate.Artifact.IsTrusted);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static string GetTemporaryPath() =>
        Path.Combine(Path.GetTempPath(), $"EndpointAgents-{Guid.NewGuid():N}.exe");
}
