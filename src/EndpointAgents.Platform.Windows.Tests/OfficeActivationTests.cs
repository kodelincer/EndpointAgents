using EndpointAgents.Platform.Windows.Office;
using EndpointAgents.Shared.Office;
using Microsoft.Win32;
using System.Text.Json;

namespace EndpointAgents.Platform.Windows.Tests;

public sealed class OfficeActivationTests
{
    [Theory]
    [InlineData("Licensed", "Licensed")]
    [InlineData("---UNLICENSED---", "Unlicensed")]
    [InlineData("OOB_GRACE", "Grace")]
    [InlineData("NOTIFICATIONS", "Notification")]
    [InlineData("unexpected", "Unknown")]
    public void NormalizesActivationStates(string input, string expected) =>
        Assert.Equal(expected, OfficeActivationOutputParser.NormalizeState(input));

    [Fact]
    public void MalformedActivationOutputProducesNoProducts()
    {
        Assert.Empty(OfficeActivationOutputParser.ParseVNext("not structured output"));
        Assert.Empty(OfficeActivationOutputParser.ParseOspp("not structured output"));
    }

    [Fact]
    public void OsppParserReturnsKmsStateWithoutProductKeyFragment()
    {
        const string output = """
            LICENSE NAME: Office 24, Office24ProPlus2024VL_KMS_Client_AE edition
            LICENSE DESCRIPTION: Office 24, VOLUME_KMSCLIENT channel
            LICENSE STATUS: ---NOTIFICATIONS---
            ERROR CODE: 0xC004F074
            Last 5 characters of installed product key: ABCDE
            """;

        var product = Assert.Single(OfficeActivationOutputParser.ParseOspp(output));
        Assert.Equal("Notification", product.State);
        Assert.Equal(OfficeLicenseTypes.VolumeKms, product.LicenseType);
        Assert.Equal("0XC004F074", product.ErrorCode);
        Assert.DoesNotContain("ABCDE", JsonSerializer.Serialize(product));
    }

    [Fact]
    public void OsppParserIgnoresInformationalCodeForLicensedKms()
    {
        const string output = """
            LICENSE NAME: Office 21, Office21ProPlus2021VL_KMS_Client_AE edition
            LICENSE DESCRIPTION: Office 21, VOLUME_KMSCLIENT channel
            LICENSE STATUS: ---LICENSED---
            ERROR CODE: 0x4004F040 (for information purposes only as the status is licensed)
            """;

        var product = Assert.Single(OfficeActivationOutputParser.ParseOspp(output));

        Assert.Equal(OfficeActivationStates.Licensed, product.State);
        Assert.Null(product.ErrorCode);
        Assert.True(OfficeActivationRecommendation.IsHealthyKms(Probe(product)));
    }

    [Fact]
    public void KmsHealthRequiresSuccessfulErrorFreeLicensedKmsProducts()
    {
        Assert.True(OfficeActivationRecommendation.IsHealthyKms(Probe(
            Product(OfficeLicenseTypes.VolumeKms, OfficeActivationStates.Licensed))));
        Assert.False(OfficeActivationRecommendation.IsHealthyKms(Probe(
            Product(OfficeLicenseTypes.VolumeMak, OfficeActivationStates.Licensed))));
        Assert.False(OfficeActivationRecommendation.IsHealthyKms(Probe(
            Product(OfficeLicenseTypes.Retail, OfficeActivationStates.Licensed))));
        Assert.False(OfficeActivationRecommendation.IsHealthyKms(Probe(
            Product(OfficeLicenseTypes.VolumeKms, OfficeActivationStates.Grace))));
        Assert.False(OfficeActivationRecommendation.IsHealthyKms(Probe(
            Product(OfficeLicenseTypes.VolumeKms, OfficeActivationStates.Licensed,
                "0XC004F074"))));
        Assert.False(OfficeActivationRecommendation.IsHealthyKms(
            new OfficeActivationProbeResult(OfficeActivationMechanisms.Ospp,
                false, false, [], "ToolUnavailable")));
    }

    [Fact]
    public void RecommendationUsesOnlyKmsHealth()
    {
        var healthy = OfficeActivationRecommendation.Evaluate(Probe(
            Product(OfficeLicenseTypes.VolumeKms, OfficeActivationStates.Licensed)));
        var wrongChannel = OfficeActivationRecommendation.Evaluate(Probe(
            Product(OfficeLicenseTypes.VolumeMak, OfficeActivationStates.Licensed)));
        var unavailable = OfficeActivationRecommendation.Evaluate(
            new OfficeActivationProbeResult(OfficeActivationMechanisms.Ospp,
                false, false, [], "ToolUnavailable"));

        Assert.Equal(OfficeActivationActions.NoAction, healthy.Action);
        Assert.Equal(OfficeActivationActions.CheckVolumeActivationInfrastructure,
            wrongChannel.Action);
        Assert.Equal(OfficeActivationActions.ManualReview, unavailable.Action);
    }

    [Fact]
    public async Task HealthyKmsSuppressesCompatibleBatRepairs()
    {
        var probe = new FakeVolumeProbe(Probe(
            Product(OfficeLicenseTypes.VolumeKms, OfficeActivationStates.Licensed)));
        var service = CreateService(probe, bugfixes: [Bugfix(
            "office-activation-repair", "16.0", "17.0")]);

        var snapshot = await service.GetStatusAsync("test",
            TestContext.Current.CancellationToken);

        Assert.Equal(OfficeActivationStates.Licensed, snapshot.Report.OverallState);
        Assert.Equal(OfficeActivationActions.NoAction,
            snapshot.Report.RecommendedAction);
        Assert.Empty(snapshot.Report.CompatibleArtifactIds);
        Assert.Empty(snapshot.Report.CompatibleBugfixArtifactIds);
        Assert.Equal([OfficeActivationMechanisms.Ospp],
            snapshot.Report.ActivationMechanisms);
        Assert.Equal([OfficeProductFamilies.VolumeLicensed],
            snapshot.Report.ProductFamilies);
        Assert.Equal(1, probe.Calls);
    }

    [Theory]
    [InlineData(OfficeLicenseTypes.VolumeKms, OfficeActivationStates.Unlicensed)]
    [InlineData(OfficeLicenseTypes.VolumeKms, OfficeActivationStates.Grace)]
    [InlineData(OfficeLicenseTypes.VolumeKms, OfficeActivationStates.Notification)]
    [InlineData(OfficeLicenseTypes.VolumeKms, OfficeActivationStates.Error)]
    [InlineData(OfficeLicenseTypes.VolumeMak, OfficeActivationStates.Licensed)]
    [InlineData(OfficeLicenseTypes.Retail, OfficeActivationStates.Licensed)]
    public async Task UnhealthyOrUnexpectedLicensesReturnCompatibleBat(
        string licenseType, string state)
    {
        var service = CreateService(new FakeVolumeProbe(Probe(
                Product(licenseType, state))),
            bugfixes: [Bugfix("office-activation-repair", "16.0", "17.0")]);

        var snapshot = await service.GetStatusAsync("test",
            TestContext.Current.CancellationToken);

        Assert.Equal(OfficeActivationActions.CheckVolumeActivationInfrastructure,
            snapshot.Report.RecommendedAction);
        Assert.Equal(["office-activation-repair"],
            snapshot.Report.CompatibleBugfixArtifactIds);
    }

    [Fact]
    public async Task ToolUnavailableRemainsInconclusiveAndAllowsApprovedBat()
    {
        var result = new OfficeActivationProbeResult(OfficeActivationMechanisms.Ospp,
            false, false, [], "ToolUnavailable");
        var service = CreateService(new FakeVolumeProbe(result),
            bugfixes: [Bugfix("office-activation-repair", "16.0", "17.0")]);

        var snapshot = await service.GetStatusAsync("test",
            TestContext.Current.CancellationToken);

        Assert.Equal(OfficeActivationStates.ToolUnavailable,
            snapshot.Report.OverallState);
        Assert.Equal(OfficeActivationActions.ManualReview,
            snapshot.Report.RecommendedAction);
        Assert.Equal(["office-activation-repair"],
            snapshot.Report.CompatibleBugfixArtifactIds);
    }

    [Fact]
    public async Task GenericMsiInstallationStillUsesOnlyOspp()
    {
        var probe = new FakeVolumeProbe(Probe(
            Product(OfficeLicenseTypes.VolumeKms, OfficeActivationStates.Licensed)));
        var service = CreateService(probe, clickToRun: false);

        var snapshot = await service.GetStatusAsync("test",
            TestContext.Current.CancellationToken);

        Assert.True(snapshot.Report.OfficeInstalled);
        Assert.Equal([OfficeActivationMechanisms.Ospp],
            snapshot.Report.ActivationMechanisms);
        Assert.Equal([OfficeProductFamilies.VolumeLicensed],
            snapshot.Report.ProductFamilies);
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public void OsppDiscoveryUsesVerifiedOfficeApplicationDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"EndpointAgents-OSPP-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var script = Path.Combine(root, "OSPP.VBS");
            File.WriteAllText(script, "' test");
            var inventory = new OfficeInventory(true, "16.0", "x64",
                ["ApplicationPaths"], [], [],
                [new OfficeInstalledApplication("Word", "WINWORD.EXE",
                    Path.Combine(root, "WINWORD.EXE"), "16.0", "16.0", "Registry64")]);

            Assert.Equal(script, OfficeVolumeActivationProbe.FindOsppScript(inventory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ActivationCollectionHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = CreateService(new FakeVolumeProbe(Probe(
            Product(OfficeLicenseTypes.VolumeKms, OfficeActivationStates.Licensed))));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetStatusAsync("test", cancellation.Token));
    }

    private static OfficeActivationService CreateService(FakeVolumeProbe volumeProbe,
        bool clickToRun = true,
        IReadOnlyList<OfficeBugfixArtifactDescriptor>? bugfixes = null) =>
        new(new OfficeInventoryCollector(new ActivationInventorySource(clickToRun)),
            volumeProbe, new FakeBugfixCatalogClient(bugfixes ?? []));

    private static OfficeActivationProbeResult Probe(
        params OfficeActivationProduct[] products) => new(
        OfficeActivationMechanisms.Ospp, true, true, products);

    private static OfficeActivationProduct Product(string licenseType, string state,
        string? errorCode = null) => new("Office LTSC", licenseType, state,
        errorCode, OfficeActivationMechanisms.Ospp);

    private static OfficeBugfixArtifactDescriptor Bugfix(string id, string minimum,
        string maximumExclusive) => new(id, id, "Approved Office BAT", $"{id}.bat",
        new string('A', 64), minimum, maximumExclusive,
        $"/api/files/office/bugfix/{id}");

    private sealed class ActivationInventorySource(bool clickToRun)
        : IOfficeInstallationSource
    {
        public OfficeClickToRunInstallation? ReadClickToRun(RegistryView view) =>
            clickToRun && view == RegistryView.Registry64
                ? new OfficeClickToRunInstallation("Registry64", "16.0.14332", "x64",
                    "en-US", "Current", "ProPlus2024Volume", @"C:\Office")
                : null;

        public IReadOnlyList<OfficeInstalledProduct> ReadInstalledProducts(
            RegistryView view) => !clickToRun && view == RegistryView.Registry64
                ? [new OfficeInstalledProduct("Microsoft Office Professional Plus 2016",
                    "16.0.14332", "Microsoft Corporation", null, "Registry64")]
                : [];

        public IReadOnlyList<OfficeInstalledApplication> ReadInstalledApplications(
            RegistryView view, IReadOnlyCollection<string> installationPaths) => [];
    }

    private sealed class FakeVolumeProbe(OfficeActivationProbeResult result)
        : IOfficeVolumeActivationProbe
    {
        public int Calls { get; private set; }

        public Task<OfficeActivationProbeResult> ProbeAsync(OfficeInventory inventory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeBugfixCatalogClient(
        IReadOnlyList<OfficeBugfixArtifactDescriptor> artifacts)
        : IOfficeBugfixCatalogClient
    {
        public Task<IReadOnlyList<OfficeBugfixArtifactDescriptor>> GetArtifactsAsync(
            CancellationToken cancellationToken) => Task.FromResult(artifacts);
    }
}
