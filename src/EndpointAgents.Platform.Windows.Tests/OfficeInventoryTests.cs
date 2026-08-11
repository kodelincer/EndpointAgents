using EndpointAgents.Platform.Windows.Office;
using Microsoft.Win32;

namespace EndpointAgents.Platform.Windows.Tests;

public sealed class OfficeInventoryTests
{
    [Fact]
    public void CollectorCombinesAndDeduplicatesBothRegistryViews()
    {
        var source = new FakeOfficeInstallationSource
        {
            ClickToRun = new OfficeClickToRunInstallation(
                "Registry64", "16.0.18827.20128", "x64", "en-US", "Current",
                "O365ProPlusRetail", @"C:\Program Files\Microsoft Office"),
            Products =
            [
                new OfficeInstalledProduct("Microsoft 365 Apps for enterprise", "16.0.18827.20128",
                    "Microsoft Corporation", "20260721", "Registry64"),
                new OfficeInstalledProduct("microsoft 365 apps for enterprise", "16.0.18827.20128",
                    "Microsoft Corporation", "20260721", "Registry32")
            ],
            Applications =
            [
                new OfficeInstalledApplication("Word", "WINWORD.EXE", @"C:\Office\WINWORD.EXE",
                    "16.0.18827.20128", "16.0.18827.20128", "Registry64")
            ]
        };

        var snapshot = new OfficeInventoryCollector(source)
            .Collect(TestContext.Current.CancellationToken);

        Assert.True(snapshot.Inventory.Installed);
        Assert.Equal(["ClickToRun", "UninstallRegistry", "ApplicationPaths"],
            snapshot.Inventory.DiscoverySources);
        Assert.Single(snapshot.Inventory.ClickToRun);
        Assert.Single(snapshot.Inventory.Products);
        var word = Assert.Single(snapshot.Inventory.Applications);
        Assert.Equal("Word", word.Name);
        Assert.Equal("16.0.18827.20128", snapshot.Inventory.Version);
        Assert.Equal("x64", snapshot.Inventory.Architecture);
        Assert.Empty(snapshot.Issues);
    }

    [Fact]
    public void CollectorReportsOfficeAsNotInstalledWhenSourcesAreEmpty()
    {
        var snapshot = new OfficeInventoryCollector(new FakeOfficeInstallationSource())
            .Collect(TestContext.Current.CancellationToken);

        Assert.False(snapshot.Inventory.Installed);
        Assert.Empty(snapshot.Inventory.DiscoverySources);
        Assert.Empty(snapshot.Inventory.ClickToRun);
        Assert.Empty(snapshot.Inventory.Products);
        Assert.Empty(snapshot.Inventory.Applications);
    }

    [Fact]
    public void CollectorKeepsPartialResultsWhenOneRegistryViewFails()
    {
        var source = new FakeOfficeInstallationSource
        {
            ThrowFor64BitView = true,
            Products =
            [
                new OfficeInstalledProduct("Microsoft Office Professional Plus 2021", "16.0",
                    "Microsoft Corporation", null, "Registry32")
            ]
        };

        var snapshot = new OfficeInventoryCollector(source)
            .Collect(TestContext.Current.CancellationToken);

        Assert.True(snapshot.Inventory.Installed);
        Assert.Single(snapshot.Inventory.Products);
        Assert.Equal(3, snapshot.Issues.Count);
    }

    [Fact]
    public void CollectorDisablesRepairSelectionForMultipleOfficeVersionLines()
    {
        var source = new FakeOfficeInstallationSource
        {
            Products =
            [
                new OfficeInstalledProduct("Microsoft Office 2013", "15.0",
                    "Microsoft Corporation", null, "Registry64"),
                new OfficeInstalledProduct("Microsoft 365 Apps", "16.0",
                    "Microsoft Corporation", null, "Registry64")
            ]
        };

        var snapshot = new OfficeInventoryCollector(source)
            .Collect(TestContext.Current.CancellationToken);

        Assert.Null(snapshot.Inventory.Version);
        Assert.Contains(snapshot.Issues,
            issue => issue.Exception == "AmbiguousOfficeVersion");
    }

    [Theory]
    [InlineData("Microsoft 365 Apps for enterprise", "Microsoft Corporation", true)]
    [InlineData("Microsoft Office Professional Plus 2021", "Microsoft Corporation", true)]
    [InlineData("Microsoft Visio Professional 2024", "Microsoft Corporation", true)]
    [InlineData("Microsoft Teams", "Microsoft Corporation", false)]
    [InlineData("LibreOffice", "The Document Foundation", false)]
    public void ProductFilterOnlyAcceptsMicrosoftOfficeFamilyProducts(
        string name, string publisher, bool expected)
    {
        Assert.Equal(expected, WindowsOfficeInstallationSource.IsOfficeProduct(name, publisher));
    }

    private sealed class FakeOfficeInstallationSource : IOfficeInstallationSource
    {
        public OfficeClickToRunInstallation? ClickToRun { get; init; }
        public IReadOnlyList<OfficeInstalledProduct> Products { get; init; } = [];
        public IReadOnlyList<OfficeInstalledApplication> Applications { get; init; } = [];
        public bool ThrowFor64BitView { get; init; }

        public OfficeClickToRunInstallation? ReadClickToRun(RegistryView view)
        {
            ThrowIfConfigured(view);
            return view == RegistryView.Registry64 ? ClickToRun : null;
        }

        public IReadOnlyList<OfficeInstalledProduct> ReadInstalledProducts(RegistryView view)
        {
            ThrowIfConfigured(view);
            return Products.Where(product =>
                product.RegistryView.Equals(view.ToString(), StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        public IReadOnlyList<OfficeInstalledApplication> ReadInstalledApplications(
            RegistryView view, IReadOnlyCollection<string> installationPaths)
        {
            ThrowIfConfigured(view);
            return Applications.Where(application =>
                application.RegistryView.Equals(view.ToString(), StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        private void ThrowIfConfigured(RegistryView view)
        {
            if (ThrowFor64BitView && view == RegistryView.Registry64)
                throw new UnauthorizedAccessException("Test registry failure.");
        }
    }
}
