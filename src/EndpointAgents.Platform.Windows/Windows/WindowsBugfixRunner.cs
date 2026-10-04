using EndpointAgents.Shared.Windows;
using EndpointAgents.Shared.Office;
using EndpointAgents.Platform.Windows.Office;
using EndpointAgents.Shared.Security;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;

namespace EndpointAgents.Platform.Windows.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsBugfixRunner(
    IOptions<WindowsRemediationOptions> options)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    private readonly WindowsRemediationOptions _options = options.Value;

    public async Task<WindowsBugfixRunResult> RunAsync(string path,
        WindowsBugfixArtifactDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (_options.RequireAdministrator)
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(
                    WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException(
                    "The EndpointAgents service does not have an administrative token.");
        }
        if (!BatchSupportFilePolicy.IsValidBundle(descriptor.Sha256,
                descriptor.SupportFiles, descriptor.BundleSha256,
                $"/api/files/windows/bugfix/{descriptor.ArtifactId}/file"))
            throw new InvalidDataException("The Windows repair bundle is not approved.");
        if (!await IsApprovedAsync(path, descriptor.ArtifactId,
                descriptor.FileName, descriptor.Sha256, _options.StagingRoot,
                _options.RequireProtectedAcl, cancellationToken,
                descriptor.BundleSha256)
            || !await BatchSupportFileDownloader.AreApprovedAsync(path,
                descriptor.SupportFiles, _options.RequireProtectedAcl,
                cancellationToken))
            throw new InvalidDataException(
                "The staged Windows remediation is not approved.");

        if (descriptor.Playbook is not null)
        {
            if (!BatchPlaybookPolicy.TryNormalize(descriptor.Playbook,
                    out var playbook, out _)
                || descriptor.PlaybookSha256 is not { Length: 64 }
                || !BatchPlaybookPolicy.ComputeSha256(playbook!).Equals(
                    descriptor.PlaybookSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The Windows playbook is not approved.");
            var menuResult = await OfficeBugfixRunner.RunProcessAsync(
                OfficeBugfixRunner.CreateStartInfo(Path.GetFullPath(path)),
                playbook!, cancellationToken, Timeout);
            return new WindowsBugfixRunResult(menuResult.ExitCode,
                menuResult.State == OfficeBugfixRunStates.PromptTimedOut,
                menuResult.State, menuResult.CompletedStepCount,
                menuResult.FailedStepIndex, menuResult.OutputTail);
        }

        var fullPath = Path.GetFullPath(path);
        using var process = new Process { StartInfo = CreateStartInfo(fullPath) };
        if (!process.Start())
            throw new InvalidOperationException(
                "The Windows remediation process did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return new WindowsBugfixRunResult(process.ExitCode, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return new WindowsBugfixRunResult(null, true);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }

    internal static async Task<bool> IsApprovedAsync(string path,
        string expectedSha256, string stagingRoot, bool requireProtectedAcl,
        CancellationToken cancellationToken)
        => await IsApprovedAsync(path, WindowsRemediationPaths.ArtifactId,
            WindowsRemediationPaths.FileName, expectedSha256, stagingRoot,
            requireProtectedAcl, cancellationToken);

    internal static async Task<bool> IsApprovedAsync(string path,
        string artifactId, string fileName, string expectedSha256,
        string stagingRoot, bool requireProtectedAcl,
        CancellationToken cancellationToken, string? revisionSha256 = null)
    {
        if (artifactId.Length is < 1 or > 64
            || artifactId.Any(character =>
                !(character is >= 'a' and <= 'z' or >= '0' and <= '9'
                    or '-' or '_' or '.'))
            || fileName != $"{artifactId}.bat"
            && !(artifactId == WindowsRemediationPaths.ArtifactId
                && fileName == WindowsRemediationPaths.FileName)
            || expectedSha256.Length != 64
            || !expectedSha256.All(Uri.IsHexDigit))
            return false;
        var fullPath = Path.GetFullPath(path);
        var rootPath = Path.GetFullPath(stagingRoot);
        var root = rootPath
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var expectedPath = Path.GetFullPath(Path.Combine(root,
            artifactId, (revisionSha256 ?? expectedSha256).ToUpperInvariant(),
            fileName));
        var directory = Path.GetDirectoryName(fullPath)!;
        var trustedPath = requireProtectedAcl
            ? ProtectedWindowsPath.IsDirectorySecure(rootPath,
                requireProtectedAcl: true)
              && ProtectedWindowsPath.IsDirectorySecure(directory,
                  requireProtectedAcl: false)
              && ProtectedWindowsPath.IsFileSecure(fullPath)
            : Directory.Exists(rootPath)
              && (File.GetAttributes(rootPath) & FileAttributes.ReparsePoint) == 0
              && Directory.Exists(directory)
              && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0
              && File.Exists(fullPath)
              && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) == 0;
        if (!fullPath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase)
            || !trustedPath
            || !await MatchesHashAsync(fullPath, expectedSha256, cancellationToken))
            return false;
        return true;
    }

    internal static ProcessStartInfo CreateStartInfo(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            WorkingDirectory = Path.GetDirectoryName(path)!,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/s");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(path);
        return startInfo;
    }

    private static async Task<bool> MatchesHashAsync(string path, string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
            return false;
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
        return actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record WindowsBugfixRunResult(int? ExitCode, bool TimedOut,
    string? State = null, int CompletedStepCount = 0, int? FailedStepIndex = null,
    string? OutputTail = null);
