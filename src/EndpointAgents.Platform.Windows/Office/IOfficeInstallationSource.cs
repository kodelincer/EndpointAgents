using Microsoft.Win32;

namespace EndpointAgents.Platform.Windows.Office;

/// <summary>Reads Office installation evidence from one Windows registry view.</summary>
public interface IOfficeInstallationSource
{
    OfficeClickToRunInstallation? ReadClickToRun(RegistryView view);

    IReadOnlyList<OfficeInstalledProduct> ReadInstalledProducts(RegistryView view);

    IReadOnlyList<OfficeInstalledApplication> ReadInstalledApplications(
        RegistryView view, IReadOnlyCollection<string> installationPaths);
}
