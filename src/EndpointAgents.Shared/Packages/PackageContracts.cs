using EndpointAgents.Shared.Office;

namespace EndpointAgents.Shared.Packages;

public static class PackageFileTypes
{
    public const string Bat = "bat";
    public const string Exe = "exe";

    public static bool IsKnown(string? type) => type is Bat or Exe;

    /// <summary>The extension a package file of this type must have.</summary>
    public static string Extension(string type) => $".{type}";
}

public static class PackageRoutes
{
    public const string Root = "/api/files/repair-packages";
    public const string Catalog = Root + "/catalog";

    public static string Download(string packageId) => $"{Root}/{packageId}";

    public static string SupportFilePrefix(string packageId) => $"{Root}/{packageId}/file";
}

public static class PackageCommands
{
    /// <summary>Downloads, verifies and runs one catalog package by its playbook.</summary>
    public const string Run = "system.package_run";
}

/// <summary>
/// One published catalog package as the endpoint receives it: a BAT or EXE, its
/// hash-pinned supporting files, and the hash-pinned playbook that decides its
/// fixed arguments, answers, timeout and success rules.
/// </summary>
public sealed record PackageDescriptor(
    string PackageId,
    string DisplayName,
    string Description,
    string FileType,
    string FileName,
    string Sha256,
    string DownloadPath,
    BatchPlaybook Playbook,
    string PlaybookSha256,
    IReadOnlyList<BatchSupportFile> SupportFiles,
    string? BundleSha256,
    string? MinWindowsVersion,
    string? MaxWindowsVersionExclusive,
    string? Publisher,
    IReadOnlyList<string> Symptoms,
    PackageSignature? Signature = null);

public sealed record PackageCatalog(IReadOnlyList<PackageDescriptor> Packages);

public static class PackageDescriptorPolicy
{
    public const int MaximumSymptoms = 16;
    public const int MaximumSymptomCharacters = 160;

    public static bool IsValidId(string? id) => id is { Length: >= 1 and <= 64 }
        && id.All(character => character is >= 'a' and <= 'z'
            || char.IsAsciiDigit(character) || character is '-' or '_' or '.')
        && char.IsAsciiLetterOrDigit(id[0]) && char.IsAsciiLetterOrDigit(id[^1]);

    /// <summary>
    /// Whether an administrator's file name inside the package folder has the
    /// extension of its declared type. Endpoints stage it as <see cref="StagedFileName"/>.
    /// </summary>
    public static bool IsValidSourceFileName(string? type, string? fileName) =>
        PackageFileTypes.IsKnown(type)
        && fileName is { Length: > 0 and <= 128 }
        && Path.GetFileName(fileName) == fileName
        && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && fileName.EndsWith(PackageFileTypes.Extension(type!),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>The name a package file is staged and run under: <c>&lt;id&gt;.&lt;type&gt;</c>.</summary>
    public static string StagedFileName(string packageId, string type) =>
        $"{packageId}{PackageFileTypes.Extension(type)}";

    /// <summary>
    /// Checks everything the endpoint relies on before it downloads or runs a package,
    /// and returns the normalized playbook whose hash matched the pin.
    /// </summary>
    public static bool TryValidate(PackageDescriptor? descriptor,
        out BatchPlaybook? playbook)
    {
        playbook = null;
        if (descriptor is null
            || !IsValidId(descriptor.PackageId)
            || !PackageFileTypes.IsKnown(descriptor.FileType)
            // Every EXE must carry a pinned vendor signature, verified before it runs.
            || descriptor.FileType == PackageFileTypes.Exe
                && string.IsNullOrWhiteSpace(descriptor.Publisher)
            || descriptor.FileName != StagedFileName(descriptor.PackageId,
                descriptor.FileType)
            || !BatchSupportFilePolicy.IsSha256(descriptor.Sha256)
            || descriptor.DownloadPath != PackageRoutes.Download(descriptor.PackageId)
            || !BatchSupportFilePolicy.IsSha256(descriptor.PlaybookSha256)
            || !OptionalVersion(descriptor.MinWindowsVersion)
            || !OptionalVersion(descriptor.MaxWindowsVersionExclusive)
            || descriptor.SupportFiles is null
            || descriptor.SupportFiles.Any(file => file is null
                || file.FileName.Equals(descriptor.FileName,
                    StringComparison.OrdinalIgnoreCase))
            || !BatchSupportFilePolicy.IsValidBundle(descriptor.Sha256,
                descriptor.SupportFiles, descriptor.BundleSha256,
                PackageRoutes.SupportFilePrefix(descriptor.PackageId))
            || !BatchPlaybookPolicy.TryNormalize(descriptor.Playbook,
                out var normalized, out _)
            || !BatchPlaybookPolicy.ComputeSha256(normalized!).Equals(
                descriptor.PlaybookSha256, StringComparison.OrdinalIgnoreCase))
            return false;
        // A BAT receives its answers through cmd.exe; an EXE that takes answers must
        // be a console program, which the control plane checks when it publishes.
        playbook = normalized;
        return true;
    }

    /// <summary>Whether this Windows version is inside the package's range.</summary>
    public static bool IsCompatible(PackageDescriptor descriptor, Version windowsVersion) =>
        (descriptor.MinWindowsVersion is null
            || windowsVersion >= Version.Parse(descriptor.MinWindowsVersion))
        && (descriptor.MaxWindowsVersionExclusive is null
            || windowsVersion < Version.Parse(descriptor.MaxWindowsVersionExclusive));

    private static bool OptionalVersion(string? value) =>
        value is null || Version.TryParse(value, out _);
}
