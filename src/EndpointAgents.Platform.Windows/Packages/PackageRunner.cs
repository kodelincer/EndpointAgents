using EndpointAgents.Platform.Windows.Office;
using EndpointAgents.Shared.Office;
using EndpointAgents.Shared.Packages;
using EndpointAgents.Shared.Security;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace EndpointAgents.Platform.Windows.Packages;

/// <summary>
/// Runs one staged, hash-verified catalog package by its playbook: a BAT through
/// <c>cmd.exe /d /s /c</c>, an EXE directly, hidden, with only the playbook's fixed
/// arguments and answers.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PackageRunner(PackageArtifactClient artifacts,
    IOptions<PackageSigningOptions> signing)
{
    public async Task<PackageRunResult> RunAsync(string path, PackageDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        if (artifacts.RequireAdministrator)
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(
                    WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException(
                    "The EndpointAgents service does not have an administrative token.");
        }
        if (!PackageDescriptorPolicy.TryValidate(descriptor, out var playbook))
            throw new InvalidDataException("The package or its playbook is not approved.");
        if (!PackageSignaturePolicy.Verify(descriptor, signing.Value.TrustedPublicKeys))
            throw new InvalidDataException("The package is not signed by a trusted administrator key.");
        if (!await IsStagedAndApprovedAsync(path, descriptor, cancellationToken)
            || !await BatchSupportFileDownloader.AreApprovedAsync(path,
                descriptor.SupportFiles, artifacts.RequireProtectedAcl, cancellationToken))
            throw new InvalidDataException("The staged package is not approved.");
        if (descriptor.FileType == PackageFileTypes.Exe && descriptor.Publisher is not null
            && !descriptor.Publisher.Equals(AuthenticodeVerifier.GetVerifiedSigner(path),
                StringComparison.OrdinalIgnoreCase))
            return new PackageRunResult(null, null,
                PackageRunStates.SignatureRejected, false, 0, null);

        var result = await OfficeBugfixRunner.RunProcessAsync(
            CreateStartInfo(Path.GetFullPath(path), descriptor.FileType,
                playbook!.Arguments ?? []),
            playbook, cancellationToken);
        return new PackageRunResult(result.ProcessId, result.ExitCode, result.State,
            result.State == OfficeBugfixRunStates.Completed
                && BatchPlaybookPolicy.IsRestartExitCode(playbook, result.ExitCode),
            result.CompletedStepCount, result.FailedStepIndex, result.OutputTail);
    }

    internal static ProcessStartInfo CreateStartInfo(string path, string fileType,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = Path.GetDirectoryName(path)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Never write a byte-order mark into the first answer.
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };
        if (fileType == PackageFileTypes.Exe)
        {
            startInfo.FileName = path;
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            return startInfo;
        }

        // cmd /s strips the outer quotes and runs the rest literally. Arguments are
        // playbook-validated to exclude quotes and every cmd.exe metacharacter.
        startInfo.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var command = new StringBuilder().Append('"').Append(path).Append('"');
        foreach (var argument in arguments)
            command.Append(' ').Append(argument.Contains(' ')
                ? $"\"{argument}\"" : argument);
        startInfo.Arguments = $"/d /s /c \"{command}\"";
        return startInfo;
    }

    private async Task<bool> IsStagedAndApprovedAsync(string path,
        PackageDescriptor descriptor, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.Equals(Path.GetFullPath(artifacts.GetStagedPath(descriptor)),
                StringComparison.OrdinalIgnoreCase))
            return false;
        var directory = Path.GetDirectoryName(fullPath)!;
        var trusted = artifacts.RequireProtectedAcl
            ? ProtectedWindowsPath.IsDirectorySecure(artifacts.StagingRoot,
                  requireProtectedAcl: true)
              && ProtectedWindowsPath.IsDirectorySecure(directory,
                  requireProtectedAcl: false)
              && ProtectedWindowsPath.IsFileSecure(fullPath)
            : File.Exists(fullPath)
              && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) == 0
              && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0;
        if (!trusted) return false;

        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous
            | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
        return actual.Equals(descriptor.Sha256, StringComparison.OrdinalIgnoreCase);
    }
}

public static class PackageRunStates
{
    /// <summary>The EXE is not validly signed by the pinned publisher.</summary>
    public const string SignatureRejected = "SignatureRejected";
}

public sealed record PackageRunResult(
    int? ProcessId,
    int? ExitCode,
    string State,
    bool RestartRequired,
    int CompletedStepCount,
    int? FailedStepIndex,
    string? OutputTail = null);
