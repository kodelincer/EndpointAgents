using EndpointAgents.Shared.Windows;
using EndpointAgents.Shared.Security;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;

namespace EndpointAgents.Platform.Windows.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsBugfixRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    public async Task<WindowsBugfixRunResult> RunAsync(string path,
        string expectedSha256, CancellationToken cancellationToken)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(
                WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException(
                "The EndpointAgents service does not have an administrative token.");
        var fullPath = Path.GetFullPath(path);
        if (expectedSha256.Length != 64
            || !expectedSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException(
                "The staged Windows remediation is not approved.");
        var root = Path.GetFullPath(WindowsRemediationPaths.StagingRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var expectedPath = Path.GetFullPath(Path.Combine(root,
            WindowsRemediationPaths.ArtifactId, expectedSha256.ToUpperInvariant(),
            WindowsRemediationPaths.FileName));
        if (!fullPath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase)
            || !ProtectedWindowsPath.IsDirectorySecure(
                WindowsRemediationPaths.StagingRoot, requireProtectedAcl: true)
            || !ProtectedWindowsPath.IsDirectorySecure(
                Path.GetDirectoryName(fullPath)!, requireProtectedAcl: false)
            || !ProtectedWindowsPath.IsFileSecure(fullPath)
            || !await MatchesHashAsync(fullPath, expectedSha256, cancellationToken))
            throw new InvalidDataException("The staged Windows remediation is not approved.");

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

public sealed record WindowsBugfixRunResult(int? ExitCode, bool TimedOut);
