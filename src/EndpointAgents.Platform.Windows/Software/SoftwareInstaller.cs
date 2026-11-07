using EndpointAgents.Platform.Windows.Office;
using EndpointAgents.Shared.Office;
using EndpointAgents.Shared.Packages;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;

namespace EndpointAgents.Platform.Windows.Software;

public sealed record SoftwareInstallResult(string State, int? ExitCode, bool RestartRequired,
    string? Detail = null);

/// <summary>
/// Runs a package's typed installer with compiled code: the command line comes only from the
/// signed software section (never from the caller or the AI), and no shell is involved.
/// </summary>
[SupportedOSPlatform("windows")]
public static class SoftwareInstaller
{
    private static readonly string[] FontExtensions = [".ttf", ".otf", ".ttc"];

    /// <summary>The process to start for an installer kind; null for copy-files and manual.</summary>
    internal static ProcessStartInfo? BuildStartInfo(string stagedPath, SoftwareInstall install,
        string systemDirectory)
    {
        var start = new ProcessStartInfo
        {
            WorkingDirectory = Path.GetDirectoryName(stagedPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        switch (install.Kind)
        {
            case InstallerKinds.Msi:
            {
                start.FileName = Path.Combine(systemDirectory, "msiexec.exe");
                var arguments = new StringBuilder("/i ").Append(Quote(stagedPath)).Append(" /qn /norestart");
                foreach (var (key, value) in install.MsiProperties ?? new Dictionary<string, string>())
                    arguments.Append(' ').Append(key).Append("=\"").Append(value).Append('"');
                start.Arguments = arguments.ToString();
                return start;
            }
            case InstallerKinds.Msix:
                // Provision for every user of the PC; DISM is a fixed Windows program, not a shell.
                start.FileName = Path.Combine(systemDirectory, "dism.exe");
                start.Arguments = $"/Online /Add-ProvisionedAppxPackage /PackagePath:{Quote(stagedPath)} /SkipLicense /NoRestart";
                return start;
            case InstallerKinds.CopyFiles:
            case InstallerKinds.Manual:
                return null;
            default:
                start.FileName = stagedPath;
                start.Arguments = string.Join(' ', InstallerKinds.SilentSwitches(install.Kind)
                    .Concat((install.Arguments ?? []).Select(argument => argument.Contains(' ') ? Quote(argument) : argument)));
                return start;
        }
    }

    /// <summary>Reads an exit code with the package's signed return-code lists.</summary>
    internal static (string State, bool Restart) MapExitCode(SoftwareInstall install, int? exitCode)
    {
        var codes = install.ReturnCodes ?? new InstallReturnCodes(SoftwareSpecPolicy.DefaultSuccessCodes,
            SoftwareSpecPolicy.DefaultRestartCodes, SoftwareSpecPolicy.DefaultRetryCodes);
        if (exitCode is not { } code) return (SoftwareRunStates.InstallerFailed, false);
        if (codes.Success?.Contains(code) == true) return (SoftwareRunStates.Installed, false);
        if (codes.Restart?.Contains(code) == true) return (SoftwareRunStates.Installed, true);
        if (codes.Retry?.Contains(code) == true) return (SoftwareRunStates.RetryLater, false);
        return (SoftwareRunStates.InstallerFailed, false);
    }

    public static async Task<SoftwareInstallResult> InstallAsync(string stagedPath,
        SoftwareInstall install, CancellationToken cancellationToken)
    {
        if (install.Kind == InstallerKinds.Manual)
            return new SoftwareInstallResult(SoftwareRunStates.ManualOnly, null, false, install.ManualReason);
        if (install.Kind == InstallerKinds.CopyFiles)
            return CopyFiles(stagedPath, install);

        var start = BuildStartInfo(Path.GetFullPath(stagedPath), install, Environment.SystemDirectory)!;
        var codes = install.ReturnCodes!;
        var allCodes = (codes.Success ?? []).Concat(codes.Restart ?? []).Concat(codes.Retry ?? []).Distinct().ToArray();
        // Reuse the hardened package process runner: hidden, time-limited, and stopped when an
        // installer opens a window that needs a click.
        var playbook = new BatchPlaybook(3, [], SuccessExitCodes: allCodes,
            TimeoutSeconds: Math.Min(install.TimeoutSeconds ?? SoftwareSpecPolicy.DefaultTimeoutSeconds,
                BatchPlaybookPolicy.MaximumRunTimeoutSeconds));
        var run = await OfficeBugfixRunner.RunProcessAsync(start, playbook, cancellationToken);
        if (run.State == OfficeBugfixRunStates.GuiDialogBlocked)
            return new SoftwareInstallResult(SoftwareRunStates.InstallerFailed, run.ExitCode, false,
                "The installer opened a window that needs a click; it cannot run silently with these settings.");
        if (run.State is OfficeBugfixRunStates.TimedOut or OfficeBugfixRunStates.Cancelled)
            return new SoftwareInstallResult(SoftwareRunStates.InstallerFailed, run.ExitCode, false,
                run.State == OfficeBugfixRunStates.TimedOut ? "The installer did not finish in time." : "The install was cancelled.");
        var (state, restart) = MapExitCode(install, run.ExitCode);
        return new SoftwareInstallResult(state, run.ExitCode, restart);
    }

    /// <summary>Extracts the signed zip to its fixed target. Fonts are also registered for all users.</summary>
    internal static SoftwareInstallResult CopyFiles(string zipPath, SoftwareInstall install,
        string? targetOverride = null, bool registerFonts = true)
    {
        var target = targetOverride ?? install.CopyTarget switch
        {
            CopyTargets.Fonts => WindowsMachineFacts.ResolveRoot(PathRoots.Fonts)!,
            CopyTargets.ProgramFiles => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), install.CopyFolder!),
            CopyTargets.PublicDesktop => Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            _ => throw new InvalidDataException("Unknown copy target.")
        };
        var root = Path.GetFullPath(target).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        var copied = 0;
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            var relative = entry.FullName.Replace('\\', '/');
            if (install.CopyTarget is CopyTargets.Fonts or CopyTargets.PublicDesktop)
                relative = Path.GetFileName(relative); // flat folders
            if (install.CopyTarget == CopyTargets.Fonts
                && !FontExtensions.Contains(Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase))
                continue;
            var destination = Path.GetFullPath(Path.Combine(root, relative));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The zip contains a path outside its target folder.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
            copied++;
            if (install.CopyTarget == CopyTargets.Fonts && registerFonts && OperatingSystem.IsWindows())
            {
                using var fonts = Registry.LocalMachine.CreateSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts", writable: true);
                var name = Path.GetFileNameWithoutExtension(relative)
                    + (Path.GetExtension(relative).Equals(".otf", StringComparison.OrdinalIgnoreCase) ? " (OpenType)" : " (TrueType)");
                fonts.SetValue(name, Path.GetFileName(destination), RegistryValueKind.String);
            }
        }
        return copied == 0
            ? new SoftwareInstallResult(SoftwareRunStates.InstallerFailed, null, false, "The zip has no files for this target.")
            // New fonts show in apps after the user signs out and back in.
            : new SoftwareInstallResult(SoftwareRunStates.Installed, 0, install.CopyTarget == CopyTargets.Fonts,
                $"{copied} file(s) copied.");
    }

    private static string Quote(string value) => $"\"{value}\"";
}
