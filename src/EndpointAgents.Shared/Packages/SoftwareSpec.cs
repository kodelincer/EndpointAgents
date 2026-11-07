using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EndpointAgents.Shared.Packages;

/// <summary>
/// The typed "software" section of an install package: what the PC must have, how the
/// installer is run, how the PC knows the application is there, and which runtime the
/// package provides to other packages. The administrator writes it once; it is part of
/// what the package is signed over, so neither the AI nor the control plane can change it.
/// </summary>
/// <param name="Licensed">Licensed software always waits for a helpdesk member's approval.</param>
/// <param name="Provides">A runtime id from <see cref="KnownRuntimes"/> this package installs,
/// so applications that need it can have it installed first.</param>
public sealed record SoftwareSpec(
    string? Name = null,
    string? Version = null,
    bool Licensed = false,
    SoftwareRequirements? Requirements = null,
    SoftwareInstall? Install = null,
    IReadOnlyList<DetectionRule>? Detection = null,
    string? Provides = null);

/// <summary>What the PC must meet before the software is approved and again before it installs.</summary>
public sealed record SoftwareRequirements(
    int? MinWindowsBuild = null,
    IReadOnlyList<string>? Editions = null,
    IReadOnlyList<string>? Architectures = null,
    int? MinMemoryMb = null,
    int? MinFreeDiskMb = null,
    int? MinCpuCores = null,
    int? MinScreenWidth = null,
    int? MinScreenHeight = null,
    IReadOnlyList<string>? Runtimes = null,
    bool? DomainJoined = null,
    IReadOnlyList<DetectionRule>? MustNotBeInstalled = null);

/// <param name="Kind">One of <see cref="InstallerKinds"/>.</param>
/// <param name="MsiProperties">MSI public properties (msi only), such as ALLUSERS=1.</param>
/// <param name="Arguments">Extra switches after the kind's own silent switches (exe kinds only);
/// for <c>exe</c> these are the vendor's documented silent switches.</param>
/// <param name="CopyTarget">copy-files only: one of <see cref="CopyTargets"/>.</param>
/// <param name="CopyFolder">copy-files to program-files: the folder name under Program Files.</param>
/// <param name="ManualReason">manual only: why the installer cannot run silently.</param>
public sealed record SoftwareInstall(
    string Kind,
    IReadOnlyDictionary<string, string>? MsiProperties = null,
    IReadOnlyList<string>? Arguments = null,
    string? CopyTarget = null,
    string? CopyFolder = null,
    int? TimeoutSeconds = null,
    InstallReturnCodes? ReturnCodes = null,
    string? ManualReason = null);

/// <summary>How installer exit codes are read. Codes not listed mean the install failed.</summary>
public sealed record InstallReturnCodes(
    IReadOnlyList<int>? Success = null,
    IReadOnlyList<int>? Restart = null,
    IReadOnlyList<int>? Retry = null);

/// <summary>One way to know an application is installed. Any matching rule counts.</summary>
/// <param name="Kind">One of <see cref="DetectionKinds"/>.</param>
/// <param name="DisplayName">uninstall-entry: the Apps &amp; features name, matched from its start.</param>
/// <param name="Root">file: one of <see cref="PathRoots"/>.</param>
/// <param name="Path">file: path under <paramref name="Root"/>, such as Google\Chrome\Application\chrome.exe.</param>
public sealed record DetectionRule(
    string Kind,
    string? ProductCode = null,
    string? DisplayName = null,
    string? MinVersion = null,
    string? Root = null,
    string? Path = null,
    string? FamilyName = null);

public static class InstallerKinds
{
    public const string Msi = "msi";
    public const string Msix = "msix";
    public const string Inno = "inno";
    public const string Nsis = "nsis";
    public const string WixBurn = "wix-burn";
    public const string InstallShield = "installshield";
    public const string Squirrel = "squirrel";
    public const string Exe = "exe";
    public const string CopyFiles = "copy-files";
    /// <summary>Cannot be installed silently; a helpdesk member installs it by hand.</summary>
    public const string Manual = "manual";

    public static readonly IReadOnlyList<string> All =
        [Msi, Msix, Inno, Nsis, WixBurn, InstallShield, Squirrel, Exe, CopyFiles, Manual];

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind);

    public static bool IsExeKind(string kind) =>
        kind is Inno or Nsis or WixBurn or InstallShield or Squirrel or Exe;

    /// <summary>The package file type a kind installs from, or null when any type may be used (manual).</summary>
    public static string? FileTypeFor(string kind) => kind switch
    {
        Msi => PackageFileTypes.Msi,
        Msix => PackageFileTypes.Msix,
        CopyFiles => PackageFileTypes.Zip,
        Manual => null,
        _ => PackageFileTypes.Exe
    };

    /// <summary>The silent switches each known installer framework uses.</summary>
    public static IReadOnlyList<string> SilentSwitches(string kind) => kind switch
    {
        Inno => ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-"],
        Nsis => ["/S"],
        WixBurn => ["/quiet", "/norestart"],
        InstallShield => ["/s", "/v\"/qn /norestart\""],
        Squirrel => ["--silent"],
        _ => []
    };
}

public static class CopyTargets
{
    public const string Fonts = "fonts";
    public const string ProgramFiles = "program-files";
    public const string PublicDesktop = "public-desktop";
    public static readonly IReadOnlyList<string> All = [Fonts, ProgramFiles, PublicDesktop];
}

public static class DetectionKinds
{
    public const string MsiProduct = "msi-product";
    public const string UninstallEntry = "uninstall-entry";
    public const string File = "file";
    public const string MsixFamily = "msix-family";
    public static readonly IReadOnlyList<string> All = [MsiProduct, UninstallEntry, File, MsixFamily];
}

public static class PathRoots
{
    public const string ProgramFiles = "ProgramFiles";
    public const string ProgramFilesX86 = "ProgramFilesX86";
    public const string ProgramData = "ProgramData";
    public const string Windows = "Windows";
    public const string Fonts = "Fonts";
    public static readonly IReadOnlyList<string> All = [ProgramFiles, ProgramFilesX86, ProgramData, Windows, Fonts];
}

/// <summary>Runtimes the PC can check for, and that a runtime package can provide.</summary>
public static class KnownRuntimes
{
    public static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>
    {
        ["dotnet-desktop-6"] = ".NET Desktop Runtime 6",
        ["dotnet-desktop-8"] = ".NET Desktop Runtime 8",
        ["dotnet-desktop-9"] = ".NET Desktop Runtime 9",
        ["dotnet-desktop-10"] = ".NET Desktop Runtime 10",
        ["vcredist-2015-x64"] = "Visual C++ 2015-2022 Redistributable (x64)",
        ["vcredist-2015-x86"] = "Visual C++ 2015-2022 Redistributable (x86)",
        ["webview2"] = "Microsoft Edge WebView2 Runtime",
        ["java-8"] = "Java 8 Runtime"
    };

    public static bool IsKnown(string? id) => id is not null && All.ContainsKey(id);

    public static string Name(string id) => All.TryGetValue(id, out var name) ? name : id;
}

public static partial class SoftwareSpecPolicy
{
    public const int MaximumDetectionRules = 4;
    public const int MaximumProperties = 16;
    public const int MaximumArguments = 16;
    public const int MaximumArgumentCharacters = 200;
    public const int DefaultTimeoutSeconds = 1800;
    public const int MinimumTimeoutSeconds = 30;
    public const int MaximumTimeoutSeconds = 3600;
    public static readonly IReadOnlyList<int> DefaultSuccessCodes = [0];
    public static readonly IReadOnlyList<int> DefaultRestartCodes = [3010, 1641];
    /// <summary>MSI 1618: another installation is already running.</summary>
    public static readonly IReadOnlyList<int> DefaultRetryCodes = [1618];
    public static readonly IReadOnlyList<string> Architectures = ["x64", "x86", "arm64"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Validates the administrator's section and returns it in one canonical form (trimmed,
    /// lower-case kinds, sorted MSI properties, default return codes filled in), which is
    /// what gets hashed and signed.
    /// </summary>
    /// <param name="fileType">The package file type, which the installer kind must match.</param>
    public static bool TryNormalize(SoftwareSpec? spec, string fileType,
        out SoftwareSpec? normalized, out string? error)
    {
        var box = new ErrorBox();
        var ok = NormalizeCore(spec, fileType, out normalized, box);
        error = ok ? null : box.Value ?? "software is invalid.";
        if (!ok) normalized = null;
        return ok;
    }

    private sealed class ErrorBox { public string? Value; }

    private static bool NormalizeCore(SoftwareSpec? spec, string fileType,
        out SoftwareSpec? normalized, ErrorBox box)
    {
        normalized = null;
        if (spec is null) { box.Value = "software is empty."; return false; }
        bool Fail(string message) { box.Value = message; return false; }

        var name = Clean(spec.Name);
        var version = Clean(spec.Version);
        if (name is { Length: > 120 }) return Fail("software.name can have at most 120 characters.");
        if (version is { Length: > 40 }) return Fail("software.version can have at most 40 characters.");
        var provides = Clean(spec.Provides)?.ToLowerInvariant();
        if (provides is not null && !KnownRuntimes.IsKnown(provides))
            return Fail($"software.provides must be one of: {string.Join(", ", KnownRuntimes.All.Keys)}.");

        SoftwareRequirements? requirements = null;
        if (spec.Requirements is { } r)
        {
            if (!InRange(r.MinWindowsBuild, 10240, 99999))
                return Fail("requirements.minWindowsBuild must be a Windows build number such as 19045 (10240 or higher).");
            if (!InRange(r.MinMemoryMb, 256, 1_048_576)) return Fail("requirements.minMemoryMb must be between 256 and 1048576.");
            if (!InRange(r.MinFreeDiskMb, 10, 10_485_760)) return Fail("requirements.minFreeDiskMb must be between 10 and 10485760.");
            if (!InRange(r.MinCpuCores, 1, 256)) return Fail("requirements.minCpuCores must be between 1 and 256.");
            if (!InRange(r.MinScreenWidth, 640, 16384) || !InRange(r.MinScreenHeight, 480, 16384))
                return Fail("requirements.minScreenWidth/minScreenHeight must be a screen size such as 1366 x 768.");
            var editions = CleanList(r.Editions);
            if (editions.Any(edition => !EditionPattern().IsMatch(edition)))
                return Fail("requirements.editions must be Windows EditionID values such as Professional or Enterprise.");
            var architectures = CleanList(r.Architectures).Select(item => item.ToLowerInvariant()).Distinct().ToArray();
            if (architectures.Any(item => !Architectures.Contains(item)))
                return Fail("requirements.architectures may contain x64, x86 and arm64.");
            var runtimes = CleanList(r.Runtimes).Select(item => item.ToLowerInvariant()).Distinct().ToArray();
            if (runtimes.Any(item => !KnownRuntimes.IsKnown(item)))
                return Fail($"requirements.runtimes may contain: {string.Join(", ", KnownRuntimes.All.Keys)}.");
            if (!TryNormalizeRules(r.MustNotBeInstalled, "requirements.mustNotBeInstalled", out var conflicts, out box.Value))
                return false;
            requirements = new SoftwareRequirements(r.MinWindowsBuild, NullIfEmpty(editions),
                NullIfEmpty(architectures), r.MinMemoryMb, r.MinFreeDiskMb, r.MinCpuCores,
                r.MinScreenWidth, r.MinScreenHeight, NullIfEmpty(runtimes), r.DomainJoined,
                NullIfEmpty(conflicts));
        }

        if (!TryNormalizeRules(spec.Detection, "software.detection", out var detection, out box.Value))
            return false;

        SoftwareInstall? install = null;
        if (spec.Install is { } i)
        {
            var kind = Clean(i.Kind)?.ToLowerInvariant() ?? string.Empty;
            if (!InstallerKinds.IsKnown(kind))
                return Fail($"software.install.kind must be one of: {string.Join(", ", InstallerKinds.All)}.");
            var expectedType = InstallerKinds.FileTypeFor(kind);
            if (expectedType is not null && expectedType != fileType)
                return Fail($"software.install.kind \"{kind}\" installs from a .{expectedType} file, but file.type is \"{fileType}\".");
            if (kind != InstallerKinds.Manual && detection.Count == 0)
                return Fail("software.detection needs at least one rule, so the PC can tell whether the install worked.");

            SortedDictionary<string, string>? properties = null;
            if (i.MsiProperties is { Count: > 0 })
            {
                if (kind != InstallerKinds.Msi) return Fail("software.install.msiProperties applies only to kind msi.");
                if (i.MsiProperties.Count > MaximumProperties) return Fail($"software.install.msiProperties can have at most {MaximumProperties} entries.");
                properties = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var (key, value) in i.MsiProperties)
                {
                    var cleanKey = key?.Trim() ?? string.Empty;
                    if (!PropertyPattern().IsMatch(cleanKey))
                        return Fail($"MSI property '{cleanKey}' must use capital letters, digits, dots or underscores.");
                    if (cleanKey is "REBOOT" && value?.Trim() is not ("ReallySuppress" or "R"))
                        return Fail("MSI property REBOOT may only be ReallySuppress.");
                    if (!IsSafeValue(value, 256))
                        return Fail($"MSI property {cleanKey} has an empty, too long, or unsafe value (no quotes or control characters).");
                    properties[cleanKey] = value!.Trim();
                }
            }

            var arguments = (i.Arguments ?? []).Select(argument => argument?.Trim() ?? string.Empty).ToArray();
            if (arguments.Length > 0 && !InstallerKinds.IsExeKind(kind))
                return Fail("software.install.arguments applies only to EXE installer kinds.");
            if (arguments.Length > MaximumArguments
                || arguments.Any(argument => !IsSafeValue(argument, MaximumArgumentCharacters)))
                return Fail($"software.install.arguments: at most {MaximumArguments} switches of up to {MaximumArgumentCharacters} characters, without quotes or control characters.");
            if (kind == InstallerKinds.Exe && arguments.Length == 0)
                return Fail("software.install.arguments must list the vendor's silent-install switches for kind exe.");

            var target = Clean(i.CopyTarget)?.ToLowerInvariant();
            var folder = Clean(i.CopyFolder);
            if (kind == InstallerKinds.CopyFiles)
            {
                if (target is null || !CopyTargets.All.Contains(target))
                    return Fail($"software.install.copyTarget must be one of: {string.Join(", ", CopyTargets.All)}.");
                if (target == CopyTargets.ProgramFiles && (folder is null || !FolderPattern().IsMatch(folder) || folder.Contains("..")))
                    return Fail("software.install.copyFolder must be a plain folder name such as \"Company Templates\".");
                if (target != CopyTargets.ProgramFiles && folder is not null)
                    return Fail("software.install.copyFolder applies only to copyTarget program-files.");
            }
            else if (target is not null || folder is not null)
                return Fail("software.install.copyTarget and copyFolder apply only to kind copy-files.");

            var timeout = i.TimeoutSeconds ?? DefaultTimeoutSeconds;
            if (timeout is < MinimumTimeoutSeconds or > MaximumTimeoutSeconds)
                return Fail($"software.install.timeoutSeconds must be between {MinimumTimeoutSeconds} and {MaximumTimeoutSeconds}.");

            var success = Codes(i.ReturnCodes?.Success, DefaultSuccessCodes);
            var restart = Codes(i.ReturnCodes?.Restart, DefaultRestartCodes);
            var retry = Codes(i.ReturnCodes?.Retry, DefaultRetryCodes);
            if (success.Count == 0) return Fail("software.install.returnCodes.success needs at least one code.");
            if (success.Concat(restart).Concat(retry).Count() != success.Concat(restart).Concat(retry).Distinct().Count())
                return Fail("software.install.returnCodes: a code can be in only one list.");
            if (success.Count + restart.Count + retry.Count > 16)
                return Fail("software.install.returnCodes can list at most 16 codes in all.");

            var manualReason = Clean(i.ManualReason);
            if (kind == InstallerKinds.Manual && manualReason is null)
                return Fail("software.install.manualReason must say why this installer cannot run silently.");
            if (kind != InstallerKinds.Manual && manualReason is not null)
                return Fail("software.install.manualReason applies only to kind manual.");
            if (manualReason is { Length: > 300 }) return Fail("software.install.manualReason can have at most 300 characters.");

            install = new SoftwareInstall(kind, properties, NullIfEmpty(arguments), target, folder,
                kind == InstallerKinds.Manual ? null : timeout,
                kind == InstallerKinds.Manual ? null : new InstallReturnCodes(success, restart, retry),
                manualReason);
        }
        else if (fileType is PackageFileTypes.Msi or PackageFileTypes.Msix or PackageFileTypes.Zip)
            return Fail($"A .{fileType} package needs software.install, so the PC knows how to install it.");

        normalized = new SoftwareSpec(name, version, spec.Licensed, requirements, install,
            NullIfEmpty(detection), provides);
        return true;
    }

    /// <summary>SHA-256 of the canonical form; part of the signed package statement.</summary>
    public static string ComputeSha256(SoftwareSpec normalized) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(normalized, Json))));

    private static bool TryNormalizeRules(IReadOnlyList<DetectionRule>? rules, string field,
        out List<DetectionRule> normalized, out string? error)
    {
        normalized = [];
        error = null;
        if (rules is null) return true;
        if (rules.Count > MaximumDetectionRules)
        {
            error = $"{field} can have at most {MaximumDetectionRules} rules.";
            return false;
        }
        foreach (var rule in rules)
        {
            var kind = Clean(rule?.Kind)?.ToLowerInvariant() ?? string.Empty;
            var minVersion = Clean(rule?.MinVersion);
            if (minVersion is not null && !Version.TryParse(minVersion, out _))
            {
                error = $"{field}: minVersion must be a version such as 120.0.6099.71.";
                return false;
            }
            DetectionRule? clean = kind switch
            {
                DetectionKinds.MsiProduct when Clean(rule!.ProductCode) is { } code && GuidPattern().IsMatch(code)
                    => new DetectionRule(kind, ProductCode: code.ToUpperInvariant()),
                DetectionKinds.UninstallEntry when Clean(rule!.DisplayName) is { Length: <= 128 } displayName
                        && IsSafeValue(displayName, 128)
                    => new DetectionRule(kind, DisplayName: displayName, MinVersion: minVersion),
                DetectionKinds.File when Clean(rule!.Root) is { } root && PathRoots.All.Contains(root)
                        && Clean(rule.Path) is { } path && IsSafeRelativePath(path)
                    => new DetectionRule(kind, Root: root, Path: path.Replace('/', '\\'), MinVersion: minVersion),
                DetectionKinds.MsixFamily when Clean(rule!.FamilyName) is { } family && FamilyPattern().IsMatch(family)
                    => new DetectionRule(kind, FamilyName: family),
                _ => null
            };
            if (clean is null)
            {
                error = $"{field}: each rule needs a kind ({string.Join(", ", DetectionKinds.All)}) and its fields: msi-product a productCode like {{GUID}}, uninstall-entry a displayName, file a root ({string.Join(", ", PathRoots.All)}) and a relative path, msix-family a familyName.";
                return false;
            }
            normalized.Add(clean);
        }
        return true;
    }

    internal static bool IsSafeRelativePath(string path)
    {
        if (path.Length is 0 or > 240 || System.IO.Path.IsPathRooted(path) || path.Contains(':')) return false;
        var segments = path.Replace('/', '\\').Split('\\');
        return segments.All(segment => segment.Length > 0 && segment != "." && segment != ".."
            && segment.IndexOfAny(['"', '<', '>', '|', '?', '*']) < 0
            && !segment.Any(char.IsControl));
    }

    private static bool IsSafeValue(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maximum
        && value.IndexOf('"') < 0 && !value.Any(char.IsControl);

    private static IReadOnlyList<int> Codes(IReadOnlyList<int>? codes, IReadOnlyList<int> fallback) =>
        codes is null ? fallback : codes.Distinct().ToArray();

    private static bool InRange(int? value, int minimum, int maximum) =>
        value is null || value >= minimum && value <= maximum;

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string[] CleanList(IReadOnlyList<string>? values) =>
        (values ?? []).Select(value => value?.Trim() ?? string.Empty).Where(value => value.Length > 0).ToArray();

    private static IReadOnlyList<T>? NullIfEmpty<T>(IReadOnlyCollection<T> values) =>
        values.Count == 0 ? null : values.ToArray();

    [GeneratedRegex("^[A-Za-z]{2,40}$")] private static partial Regex EditionPattern();
    [GeneratedRegex("^[A-Z][A-Z0-9_.]{0,63}$")] private static partial Regex PropertyPattern();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}$")] private static partial Regex FolderPattern();
    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$")] private static partial Regex GuidPattern();
    [GeneratedRegex(@"^[A-Za-z0-9.-]{3,50}_[a-z0-9]{13}$")] private static partial Regex FamilyPattern();
}
