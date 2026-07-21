using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Office;

/// <summary>Reads machine-wide Office inventory without launching Office or licensing tools.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsOfficeInstallationSource : IOfficeInstallationSource
{
    private const string ClickToRunPath = @"SOFTWARE\Microsoft\Office\ClickToRun\Configuration";
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string AppPathsRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";

    private static readonly (string Name, string ExecutableName)[] Applications =
    [
        ("Word", "WINWORD.EXE"),
        ("Excel", "EXCEL.EXE"),
        ("PowerPoint", "POWERPNT.EXE"),
        ("Outlook", "OUTLOOK.EXE"),
        ("OneNote", "ONENOTE.EXE"),
        ("Access", "MSACCESS.EXE"),
        ("Publisher", "MSPUB.EXE"),
        ("Visio", "VISIO.EXE"),
        ("Project", "WINPROJ.EXE")
    ];

    public OfficeClickToRunInstallation? ReadClickToRun(RegistryView view)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var key = baseKey.OpenSubKey(ClickToRunPath);
        if (key is null)
            return null;

        return new OfficeClickToRunInstallation(
            view.ToString(),
            ReadString(key, "VersionToReport"),
            ReadString(key, "Platform"),
            ReadString(key, "ClientCulture"),
            ReadString(key, "UpdateChannel"),
            ReadString(key, "ProductReleaseIds"),
            ReadString(key, "InstallationPath"));
    }

    public IReadOnlyList<OfficeInstalledProduct> ReadInstalledProducts(RegistryView view)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var uninstallKey = baseKey.OpenSubKey(UninstallPath);
        if (uninstallKey is null)
            return Array.Empty<OfficeInstalledProduct>();

        var products = new List<OfficeInstalledProduct>();
        foreach (var subKeyName in uninstallKey.GetSubKeyNames())
        {
            using var productKey = TryOpenSubKey(uninstallKey, subKeyName);
            if (productKey is null)
                continue;

            var displayName = ReadString(productKey, "DisplayName");
            var publisher = ReadString(productKey, "Publisher");
            if (!IsOfficeProduct(displayName, publisher))
                continue;

            products.Add(new OfficeInstalledProduct(
                displayName!,
                ReadString(productKey, "DisplayVersion"),
                publisher,
                ReadString(productKey, "InstallDate"),
                view.ToString()));
        }

        return products;
    }

    public IReadOnlyList<OfficeInstalledApplication> ReadInstalledApplications(
        RegistryView view, IReadOnlyCollection<string> installationPaths)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        var installed = new List<OfficeInstalledApplication>();

        foreach (var application in Applications)
        {
            using var appPathKey = baseKey.OpenSubKey(
                $@"{AppPathsRoot}\{application.ExecutableName}");
            var executablePath = NormalizeExecutablePath(appPathKey?.GetValue(null)?.ToString());
            if (executablePath is null || !File.Exists(executablePath))
                executablePath = FindUnderInstallRoots(application.ExecutableName, installationPaths);
            if (executablePath is null)
                continue;

            var version = FileVersionInfo.GetVersionInfo(executablePath);
            installed.Add(new OfficeInstalledApplication(
                application.Name,
                application.ExecutableName,
                Path.GetFullPath(executablePath),
                version.FileVersion,
                version.ProductVersion,
                view.ToString()));
        }

        return installed;
    }

    private static string? FindUnderInstallRoots(
        string executableName, IReadOnlyCollection<string> installationPaths)
    {
        foreach (var root in installationPaths)
        {
            string[] candidates =
            [
                Path.Combine(root, executableName),
                Path.Combine(root, "root", "Office16", executableName),
                Path.Combine(root, "Office16", executableName),
                Path.Combine(root, "Office15", executableName),
                Path.Combine(root, "Office14", executableName)
            ];
            var match = candidates.FirstOrDefault(File.Exists);
            if (match is not null)
                return match;
        }
        return null;
    }

    private static string? NormalizeExecutablePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        if (!expanded.StartsWith('"'))
            return expanded;
        var closingQuote = expanded.IndexOf('"', 1);
        return closingQuote > 1 ? expanded[1..closingQuote] : expanded.Trim('"');
    }

    public static bool IsOfficeProduct(string? displayName, string? publisher)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return false;

        var isMicrosoft = displayName.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
            || publisher?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true;
        if (!isMicrosoft)
            return false;

        string[] officeNames =
        [
            "Office", "Microsoft 365", "Word", "Excel", "Outlook", "PowerPoint",
            "Access", "Publisher", "OneNote", "Visio", "Project"
        ];

        return officeNames.Any(name =>
            displayName.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private static RegistryKey? TryOpenSubKey(RegistryKey parent, string name)
    {
        try
        {
            return parent.OpenSubKey(name);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string? ReadString(RegistryKey key, string name) =>
        key.GetValue(name)?.ToString() is { } value && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
}
