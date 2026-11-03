using ControlPlane.Api.Files;
using ControlPlane.Api.Updates;
using EndpointAgents.Shared.Packages;
using EndpointAgents.Shared.Updates;
using Microsoft.Extensions.Options;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace ControlPlane.Api.Tests;

public sealed class AgentUpdateTests : IDisposable
{
    private static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly string PublicKey = Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent-update-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static readonly AgentUpdateFile[] RequiredFiles =
    [
        new(AgentUpdatePolicy.ServiceExe, new string('A', 64)),
        new(AgentUpdatePolicy.TrayExe, new string('B', 64)),
        new(AgentUpdatePolicy.UpdaterExe, new string('C', 64))
    ];

    private static AgentUpdateManifest Manifest(string version = "0.3.0", IReadOnlyList<AgentUpdateFile>? files = null,
        string sha = "", long size = 10) =>
        new(1, version, AgentUpdatePolicy.ExpectedFileName(version), sha.Length == 0 ? new string('D', 64) : sha,
            size, 17763, "x64", "notes", files ?? RequiredFiles, null);

    private static AgentUpdateManifest Signed(AgentUpdateManifest manifest) => manifest with
    {
        Signature = PackageSignaturePolicy.Sign(AgentUpdatePolicy.BuildStatement(manifest), Key, PublicKey)
    };

    [Fact]
    public void AValidManifestPassesAndUnsafeOnesFail()
    {
        Assert.True(AgentUpdatePolicy.TryValidate(Manifest(), out var error), error);
        Assert.False(AgentUpdatePolicy.TryValidate(Manifest("1.2"), out _));
        Assert.False(AgentUpdatePolicy.TryValidate(Manifest(files: [.. RequiredFiles, new("Service/../evil.exe", new string('E', 64))]), out _));
        Assert.False(AgentUpdatePolicy.TryValidate(Manifest(files: [.. RequiredFiles, new("Windows/evil.exe", new string('E', 64))]), out _));
        Assert.False(AgentUpdatePolicy.TryValidate(Manifest(files: [.. RequiredFiles, new("Service/appsettings.Development.json", new string('E', 64))]), out _));
        Assert.False(AgentUpdatePolicy.TryValidate(Manifest(files: RequiredFiles[..2]), out _));
        Assert.False(AgentUpdatePolicy.TryValidate(Manifest() with { FileName = "other.zip" }, out _));
        Assert.False(AgentUpdatePolicy.TryValidate(Manifest() with { Architecture = "arm64" }, out _));
    }

    [Fact]
    public void TheSignatureCoversEveryFile()
    {
        var signed = Signed(Manifest());
        Assert.True(AgentUpdatePolicy.Verify(signed, [PublicKey]));
        var changedFile = signed with { Files = [RequiredFiles[0] with { Sha256 = new string('F', 64) }, .. RequiredFiles[1..]] };
        Assert.False(AgentUpdatePolicy.Verify(changedFile, [PublicKey]));
        Assert.False(AgentUpdatePolicy.Verify(signed with { MinWindowsBuild = 10240 }, [PublicKey]));
        Assert.False(AgentUpdatePolicy.Verify(signed, []));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(AgentUpdatePolicy.Verify(signed, [Convert.ToBase64String(other.ExportSubjectPublicKeyInfo())]));
    }

    [Theory]
    [InlineData("0.10.0", "0.9.0", true)]
    [InlineData("0.2.0", "0.2.0", false)]
    [InlineData("0.2.0", "0.3.0", false)]
    [InlineData("1.0.0", "0.9.9+abcdef", true)]
    [InlineData("1.0.0", "1.0", false)]
    public void OnlyNewerVersionsCount(string candidate, string current, bool newer) =>
        Assert.Equal(newer, AgentUpdatePolicy.IsNewer(candidate, current));

    private static AgentUpdatePackage Ready(string version, int minBuild = 17763) =>
        new(version, Manifest(version) with { MinWindowsBuild = minBuild }, AgentUpdatePackageStatus.Ready, null, "x.zip", null);

    private static AgentUpdateCheck Check(string version = "0.2.0", int build = 19045, bool busy = false,
        params string[] failed) => new(version, build, "x64", failed, busy);

    private static readonly DateTimeOffset Noon = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ThePlannerOffersOnlyWhenEveryRuleAllows()
    {
        var packages = new[] { Ready("0.3.0"), Ready("0.4.0", minBuild: 22000) };
        var everyone = new AgentUpdateSettings("0.3.0", "0.4.0", ["pilot"], false, null, null);

        Assert.Equal("0.3.0", AgentUpdatePlanner.Decide("pc", Check(), everyone, packages, false, Noon).Offer?.Manifest.Version);
        Assert.Equal(AgentUpdateRoutes.Download("0.3.0"),
            AgentUpdatePlanner.Decide("pc", Check(), everyone, packages, false, Noon).Offer?.DownloadPath);
        // The pilot PC gets the pilot version, if its Windows is new enough.
        Assert.Equal("0.4.0", AgentUpdatePlanner.Decide("PILOT", Check(build: 22631), everyone, packages, false, Noon).Offer?.Manifest.Version);
        Assert.Null(AgentUpdatePlanner.Decide("pilot", Check(build: 19045), everyone, packages, false, Noon).Offer);

        Assert.Null(AgentUpdatePlanner.Decide("pc", Check(), everyone with { Paused = true }, packages, false, Noon).Offer);
        Assert.Null(AgentUpdatePlanner.Decide("pc", Check("0.3.0"), everyone, packages, false, Noon).Offer);
        Assert.Null(AgentUpdatePlanner.Decide("pc", Check("0.5.0"), everyone, packages, false, Noon).Offer); // never down
        Assert.Null(AgentUpdatePlanner.Decide("pc", Check(failed: "0.3.0"), everyone, packages, false, Noon).Offer);
        Assert.Null(AgentUpdatePlanner.Decide("pc", Check(busy: true), everyone, packages, false, Noon).Offer);
        Assert.Null(AgentUpdatePlanner.Decide("pc", Check(), everyone, packages, inConversation: true, Noon).Offer);
        Assert.Null(AgentUpdatePlanner.Decide("pc", Check(), everyone with { TargetVersion = "0.9.0" }, packages, false, Noon).Offer);
        var notSigned = new[] { Ready("0.3.0") with { Status = AgentUpdatePackageStatus.NotSigned } };
        Assert.Null(AgentUpdatePlanner.Decide("pc", Check(), everyone, notSigned, false, Noon).Offer);
        Assert.Null(AgentUpdatePlanner.Decide("pc", Check(), AgentUpdateSettings.Default, packages, false, Noon).Offer);
    }

    [Theory]
    [InlineData(null, null, 3, true)]
    [InlineData(18, 6, 23, true)]   // across midnight
    [InlineData(18, 6, 3, true)]
    [InlineData(18, 6, 12, false)]
    [InlineData(9, 17, 12, true)]
    [InlineData(9, 17, 17, false)]
    public void TheUpdateWindowIsRespected(int? start, int? end, int hour, bool allowed) =>
        Assert.Equal(allowed, AgentUpdatePlanner.InWindow(
            new AgentUpdateSettings("1.0.0", null, [], false, start, end),
            new DateTimeOffset(2026, 11, 3, hour, 30, 0, TimeSpan.Zero)));

    private AgentUpdateRepository Repository(bool trustKey = true) => new(Options.Create(new FileRepositoryOptions
    {
        RootPath = _root,
        RequireProtectedAcl = false,
        TrustedPackageSigningKeys = trustKey ? [PublicKey] : []
    }));

    /// <summary>Writes a real zip and its signed manifest the way new-agent-update.ps1 does.</summary>
    private string Publish(string version, bool sign = true, Action<string>? tamper = null)
    {
        var folder = Path.Combine(_root, AgentUpdateRepository.DirectoryName, version);
        Directory.CreateDirectory(folder);
        var zipPath = Path.Combine(folder, AgentUpdatePolicy.ExpectedFileName(version));
        var files = new List<AgentUpdateFile>();
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            foreach (var path in new[] { AgentUpdatePolicy.ServiceExe, AgentUpdatePolicy.TrayExe, AgentUpdatePolicy.UpdaterExe })
            {
                var bytes = global::System.Text.Encoding.UTF8.GetBytes(path + version);
                using (var entry = archive.CreateEntry(path).Open()) entry.Write(bytes);
                files.Add(new AgentUpdateFile(path, Convert.ToHexString(SHA256.HashData(bytes))));
            }
        var manifest = Manifest(version, files, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zipPath))),
            new FileInfo(zipPath).Length);
        if (sign) manifest = Signed(manifest);
        File.WriteAllText(Path.Combine(folder, AgentUpdateRepository.ManifestFileName),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        tamper?.Invoke(zipPath);
        return folder;
    }

    [Fact]
    public async Task TheRepositoryOffersOnlySignedMatchingVersions()
    {
        Publish("0.3.0");
        Publish("0.4.0", sign: false);
        Publish("0.5.0", tamper: zip => File.AppendAllText(zip, "x"));
        var all = await Repository().GetAllAsync(CancellationToken.None);
        Assert.Equal(["0.5.0", "0.4.0", "0.3.0"], all.Select(item => item.Version));
        Assert.Equal(AgentUpdatePackageStatus.Ready, all.Single(item => item.Version == "0.3.0").Status);
        Assert.Equal(AgentUpdatePackageStatus.NotSigned, all.Single(item => item.Version == "0.4.0").Status);
        Assert.Equal(AgentUpdatePackageStatus.HashMismatch, all.Single(item => item.Version == "0.5.0").Status);
        Assert.Equal(AgentUpdatePackageStatus.NotSigned,
            (await Repository(trustKey: false).GetAsync("0.3.0", CancellationToken.None))!.Status);
        Assert.Null(await Repository().GetAsync("../0.3.0", CancellationToken.None));
    }

    [Fact]
    public async Task SettingsNeedReadyVersionsAndPilotPcs()
    {
        Publish("0.3.0");
        var repository = Repository();
        Assert.Null(await AgentUpdateEndpoints.ValidateAsync(
            new AgentUpdateSettingsRequest("0.3.0", "0.3.0", ["pc"], false, 9, 17), repository, CancellationToken.None));
        Assert.NotNull(await AgentUpdateEndpoints.ValidateAsync(
            new AgentUpdateSettingsRequest("0.9.0", null, null, false, null, null), repository, CancellationToken.None));
        Assert.NotNull(await AgentUpdateEndpoints.ValidateAsync(
            new AgentUpdateSettingsRequest(null, "0.3.0", [], false, null, null), repository, CancellationToken.None));
        Assert.NotNull(await AgentUpdateEndpoints.ValidateAsync(
            new AgentUpdateSettingsRequest(null, null, null, false, 9, null), repository, CancellationToken.None));
        Assert.NotNull(await AgentUpdateEndpoints.ValidateAsync(
            new AgentUpdateSettingsRequest(null, null, null, false, 24, 3), repository, CancellationToken.None));
    }

    [Fact]
    public async Task TheStoreKeepsSettingsAndDeviceReports()
    {
        var database = Path.Combine(_root, "cp.db");
        Directory.CreateDirectory(_root);
        var chat = new ControlPlane.Api.Chat.ChatStore(Options.Create(new ControlPlane.Api.Chat.ChatOptions { DatabasePath = database }));
        await chat.InitializeAsync();
        var store = new AgentUpdateStore(Options.Create(new ControlPlane.Api.Chat.ChatOptions { DatabasePath = database }));
        await store.InitializeAsync();
        Assert.Equal(AgentUpdateSettings.Default, await store.GetSettingsAsync(CancellationToken.None) with { PilotAgentIds = [] });
        await store.SaveSettingsAsync(new AgentUpdateSettings("0.3.0", null, ["a"], false, null, null, "admin"), CancellationToken.None);
        Assert.Equal("0.3.0", (await store.GetSettingsAsync(CancellationToken.None)).TargetVersion);
        await store.RecordVersionAsync("pc-1", "0.2.0", isCheck: true, CancellationToken.None);
        await store.RecordReportAsync("pc-1", AgentUpdateStatuses.RolledBack, "0.2.0", "0.3.0", "did not connect", CancellationToken.None);
        var device = Assert.Single(await store.GetDevicesAsync(CancellationToken.None));
        Assert.Equal(("0.2.0", AgentUpdateStatuses.RolledBack, "0.3.0"), (device.CurrentVersion, device.LastStatus, device.LastToVersion));
        Assert.False(await store.IsInConversationAsync("pc-1", TimeSpan.FromMinutes(10), CancellationToken.None));
    }
}
