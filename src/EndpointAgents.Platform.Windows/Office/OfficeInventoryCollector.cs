using Microsoft.Win32;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Office;

/// <summary>Combines both registry views into one de-duplicated Office inventory.</summary>
[SupportedOSPlatform("windows")]
public sealed class OfficeInventoryCollector(IOfficeInstallationSource source)
{
    public OfficeInventorySnapshot Collect(CancellationToken cancellationToken)
    {
        var clickToRun = new List<OfficeClickToRunInstallation>();
        var products = new List<OfficeInstalledProduct>();
        var applications = new List<OfficeInstalledApplication>();
        var issues = new List<OfficeInventoryIssue>();

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            CollectSource($"office.click_to_run.{view}",
                () => source.ReadClickToRun(view), value =>
                {
                    if (value is not null)
                        clickToRun.Add(value);
                }, issues);

            cancellationToken.ThrowIfCancellationRequested();
            CollectSource($"office.installed_products.{view}",
                () => source.ReadInstalledProducts(view), values => products.AddRange(values), issues);
        }

        var installationPaths = clickToRun
            .Select(item => item.InstallationPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            CollectSource($"office.installed_applications.{view}",
                () => source.ReadInstalledApplications(view, installationPaths),
                values => applications.AddRange(values), issues);
        }

        var uniqueClickToRun = clickToRun
            .GroupBy(item => $"{item.Version}\0{item.Platform}\0{item.ProductReleaseIds}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var uniqueProducts = products
            .GroupBy(item => $"{item.DisplayName}\0{item.DisplayVersion}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var uniqueApplications = applications
            .GroupBy(item => item.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var discoverySources = new List<string>();
        if (uniqueClickToRun.Length > 0)
            discoverySources.Add("ClickToRun");
        if (uniqueProducts.Length > 0)
            discoverySources.Add("UninstallRegistry");
        if (uniqueApplications.Length > 0)
            discoverySources.Add("ApplicationPaths");

        var version = uniqueClickToRun.Select(item => item.Version)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? uniqueProducts.Select(item => item.DisplayVersion)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var architecture = uniqueClickToRun.Select(item => item.Platform)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        return new OfficeInventorySnapshot(
            new OfficeInventory(
                uniqueClickToRun.Length > 0 || uniqueProducts.Length > 0 || uniqueApplications.Length > 0,
                version,
                architecture,
                discoverySources,
                uniqueClickToRun,
                uniqueProducts,
                uniqueApplications),
            issues);
    }

    private static void CollectSource<T>(string name, Func<T> read, Action<T> add,
        List<OfficeInventoryIssue> issues)
    {
        try
        {
            add(read());
        }
        catch (Exception ex)
        {
            issues.Add(new OfficeInventoryIssue(
                name,
                "Failed to read one Office inventory source.",
                ex.GetType().Name));
        }
    }

}
