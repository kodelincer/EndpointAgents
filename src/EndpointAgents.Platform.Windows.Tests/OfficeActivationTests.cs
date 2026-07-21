using EndpointAgents.Platform.Windows.Ibsng;
using EndpointAgents.Platform.Windows.Office;
using EndpointAgents.Platform.Windows.UserSessions;
using EndpointAgents.Shared.LocalIpc;
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
    public void VNextParserReturnsStatusWithoutIdentityOrLicenseIdentifiers()
    {
        const string output = """
            LicenseId: EWW_very-sensitive-license-id
            Type: User|Subscription
            Product: O365ProPlusRetail
            State: Licensed
            Email: person@example.test
            Tenant ID: tenant-sensitive-value
            """;

        var product = Assert.Single(OfficeActivationOutputParser.ParseVNext(output));
        Assert.Equal("O365ProPlusRetail", product.ProductName);
        Assert.Equal("Licensed", product.State);
        var json = JsonSerializer.Serialize(product);
        Assert.DoesNotContain("person@example", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant-sensitive", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EWW_", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MalformedActivationOutputProducesNoProducts()
    {
        Assert.Empty(OfficeActivationOutputParser.ParseVNext("not structured output"));
        Assert.Empty(OfficeActivationOutputParser.ParseOspp("not structured output"));
    }

    [Fact]
    public void OsppParserReturnsVolumeStateWithoutProductKeyFragment()
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
        Assert.Equal("Volume|KMS", product.LicenseType);
        Assert.Equal("0XC004F074", product.ErrorCode);
        Assert.DoesNotContain("ABCDE", JsonSerializer.Serialize(product));
    }

    [Fact]
    public void RecommendationSelectsOneExactCompatibleArtifact()
    {
        var artifact = Artifact("m365-reset", ["Microsoft365Apps"], ["Unlicensed"]);
        var products = new[] { new OfficeActivationProduct("O365ProPlusRetail",
            "User|Subscription", "Unlicensed", null, "VNext") };

        var result = OfficeActivationRecommendation.Evaluate("Unlicensed", "16.0.1",
            ["Microsoft365Apps"], products, [artifact]);

        Assert.Equal("ApprovedArtifactAvailable", result.Action);
        Assert.Equal(["m365-reset"], result.ArtifactIds);
        Assert.True(result.RequiresUserInteraction);
    }

    [Fact]
    public void RecommendationRequiresOperatorWhenSeveralArtifactsMatch()
    {
        var result = OfficeActivationRecommendation.Evaluate("Unlicensed", "16.0",
            ["VolumeLicensed"],
            [new OfficeActivationProduct("Office LTSC", "Volume|KMS", "Unlicensed",
                "0XC004F074", "OSPP")],
            [Artifact("kms-a", ["VolumeLicensed"], ["Unlicensed"]),
             Artifact("kms-b", ["VolumeLicensed"], ["Unlicensed"])]);

        Assert.Equal("OperatorSelectionRequired", result.Action);
        Assert.Equal(2, result.ArtifactIds.Count);
    }

    [Fact]
    public async Task ServiceKeepsReportWhenInteractiveTrayIsUnavailable()
    {
        var service = CreateService(new TrayIpcResponse("test", false, "TrayUnavailable"),
            new OfficeActivationProbeResult("OSPP", false, false, [], "NotCalled"));

        var snapshot = await service.GetStatusAsync("test",
            TestContext.Current.CancellationToken);

        Assert.True(snapshot.Report.OfficeInstalled);
        Assert.Equal("ToolUnavailable", snapshot.Report.OverallState);
        Assert.Contains(snapshot.Issues,
            issue => issue.Exception == "TrayUnavailable");
    }

    [Fact]
    public async Task MixedProductsUseWorstActivationState()
    {
        var vnext = new OfficeActivationProbeResult("VNext", true, true,
            [new OfficeActivationProduct("O365ProPlusRetail", "User|Subscription",
                "Licensed", null, "VNext")]);
        var volume = new OfficeActivationProbeResult("OSPP", true, true,
            [new OfficeActivationProduct("Office LTSC", "Volume|KMS",
                "Unlicensed", "0XC004F074", "OSPP")]);
        var service = CreateService(new TrayIpcResponse("test", true,
            "OfficeActivationInspected", OfficeActivation: vnext), volume,
            "O365ProPlusRetail,ProPlus2024Volume");

        var snapshot = await service.GetStatusAsync("test",
            TestContext.Current.CancellationToken);

        Assert.Equal("Unlicensed", snapshot.Report.OverallState);
        Assert.Equal(2, snapshot.Report.Products.Count);
        Assert.Contains("Microsoft365Apps", snapshot.Report.ProductFamilies);
        Assert.Contains("VolumeLicensed", snapshot.Report.ProductFamilies);
    }

    [Fact]
    public async Task ActivationCollectionHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = CreateService(new TrayIpcResponse("test", false, "unused"),
            new OfficeActivationProbeResult("OSPP", false, false, []));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetStatusAsync("test", cancellation.Token));
    }

    private static OfficeActivationService CreateService(TrayIpcResponse trayResponse,
        OfficeActivationProbeResult volumeResponse,
        string productReleaseIds = "O365ProPlusRetail")
    {
        var inventorySource = new ActivationInventorySource(productReleaseIds);
        return new OfficeActivationService(new OfficeInventoryCollector(inventorySource),
            new FakeSessionSource(), new FakeTrayClient(trayResponse),
            new FakeVolumeProbe(volumeResponse), new FakeCatalogClient());
    }

    private static OfficeRemediationArtifactDescriptor Artifact(string id,
        IReadOnlyList<string> families, IReadOnlyList<string> states) => new(
        id, id, "Approved remediation", $"{id}.ps1", new string('A', 64),
        "PowerShellScript", families, "16.0", null, states, [], true, true,
        false, $"/api/files/office/{id}");

    private sealed class ActivationInventorySource(string productReleaseIds)
        : IOfficeInstallationSource
    {
        public OfficeClickToRunInstallation? ReadClickToRun(RegistryView view) =>
            view == RegistryView.Registry64
                ? new OfficeClickToRunInstallation("Registry64", "16.0", "x64", "en-US",
                    "Current", productReleaseIds, @"C:\Office") : null;
        public IReadOnlyList<OfficeInstalledProduct> ReadInstalledProducts(RegistryView view) => [];
        public IReadOnlyList<OfficeInstalledApplication> ReadInstalledApplications(
            RegistryView view, IReadOnlyCollection<string> installationPaths) => [];
    }

    private sealed class FakeSessionSource : IOfficeUserSessionSource
    {
        public InteractiveUserSession GetActiveSession() =>
            new(1, "test-user", "TEST", @"C:\Users\test-user\Desktop");
    }

    private sealed class FakeTrayClient(TrayIpcResponse response) : ITrayCommandClient
    {
        public Task<TrayIpcResponse> SendAsync(int sessionId, TrayIpcRequest request,
            CancellationToken cancellationToken) => Task.FromResult(response);
    }

    private sealed class FakeVolumeProbe(OfficeActivationProbeResult result)
        : IOfficeVolumeActivationProbe
    {
        public Task<OfficeActivationProbeResult> ProbeAsync(OfficeInventory inventory,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class FakeCatalogClient : IOfficeActivationCatalogClient
    {
        public Task<IReadOnlyList<OfficeRemediationArtifactDescriptor>> GetArtifactsAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OfficeRemediationArtifactDescriptor>>([]);
    }
}
