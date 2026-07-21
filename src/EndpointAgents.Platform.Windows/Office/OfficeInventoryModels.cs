namespace EndpointAgents.Platform.Windows.Office;

/// <summary>Read-only inventory of Microsoft Office installations found on an endpoint.</summary>
public sealed record OfficeInventory(
    bool Installed,
    string? Version,
    string? Architecture,
    IReadOnlyList<string> DiscoverySources,
    IReadOnlyList<OfficeClickToRunInstallation> ClickToRun,
    IReadOnlyList<OfficeInstalledProduct> Products,
    IReadOnlyList<OfficeInstalledApplication> Applications);

/// <summary>Office Click-to-Run configuration read from the machine registry.</summary>
public sealed record OfficeClickToRunInstallation(
    string RegistryView,
    string? Version,
    string? Platform,
    string? ClientCulture,
    string? UpdateChannel,
    string? ProductReleaseIds,
    string? InstallationPath);

/// <summary>An Office product listed in the machine uninstall registry.</summary>
public sealed record OfficeInstalledProduct(
    string DisplayName,
    string? DisplayVersion,
    string? Publisher,
    string? InstallDate,
    string RegistryView);

/// <summary>An Office desktop application found through App Paths or an Office install root.</summary>
public sealed record OfficeInstalledApplication(
    string Name,
    string ExecutableName,
    string ExecutablePath,
    string? FileVersion,
    string? ProductVersion,
    string RegistryView);

/// <summary>A non-fatal inventory source failure.</summary>
public sealed record OfficeInventoryIssue(string Source, string Message, string Exception);

/// <summary>Inventory plus any non-fatal collection issues.</summary>
public sealed record OfficeInventorySnapshot(
    OfficeInventory Inventory,
    IReadOnlyList<OfficeInventoryIssue> Issues);
