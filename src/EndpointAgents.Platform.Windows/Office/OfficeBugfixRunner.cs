using EndpointAgents.Shared.Office;
using EndpointAgents.Shared.Security;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;

namespace EndpointAgents.Platform.Windows.Office;

/// <summary>Runs one hash-pinned Office BAT from protected service staging.</summary>
[SupportedOSPlatform("windows")]
public sealed class OfficeBugfixRunner
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(8);

    public async Task<OfficeBugfixRunResult> RunAsync(string path,
        string artifactId, string fileName, string expectedSha256,
        CancellationToken cancellationToken)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(
                WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException(
                "The EndpointAgents service does not have an administrative token.");
        if (!await IsApprovedAsync(path, artifactId, fileName, expectedSha256,
                cancellationToken))
            throw new InvalidDataException("The staged Office repair is not approved.");

        using var process = new Process { StartInfo = CreateStartInfo(path) };
        if (!process.Start())
            throw new InvalidOperationException("The Office repair process did not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.StandardInput.WriteLineAsync("1".AsMemory(), timeout.Token);
            await process.StandardInput.FlushAsync(timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            return new OfficeBugfixRunResult(process.Id, process.ExitCode, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return new OfficeBugfixRunResult(process.Id, null, true);
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
            CreateNoWindow = true,
            RedirectStandardInput = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/s");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(path);
        return startInfo;
    }

    internal static async Task<bool> IsApprovedAsync(string path, string artifactId,
        string fileName, string expectedSha256, CancellationToken cancellationToken)
    {
        if (artifactId.Length is < 1 or > 64
            || artifactId.Any(character =>
                !(character is >= 'a' and <= 'z' || char.IsAsciiDigit(character)
                  || character is '-' or '_' or '.'))
            || !(artifactId[^1] is >= 'a' and <= 'z'
                || char.IsAsciiDigit(artifactId[^1]))
            || !fileName.Equals($"{artifactId}.bat", StringComparison.OrdinalIgnoreCase))
            return false;

        if (expectedSha256.Length != 64
            || !expectedSha256.All(Uri.IsHexDigit))
            return false;
        var root = Path.GetFullPath(OfficeRemediationPaths.StagingRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var expected = Path.GetFullPath(Path.Combine(root, artifactId,
            expectedSha256.ToUpperInvariant(), fileName));
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        if (!fullPath.Equals(expected, StringComparison.OrdinalIgnoreCase)
            || !ProtectedWindowsPath.IsDirectorySecure(
                OfficeRemediationPaths.StagingRoot, requireProtectedAcl: true)
            || !ProtectedWindowsPath.IsDirectorySecure(directory,
                requireProtectedAcl: false)
            || !ProtectedWindowsPath.IsFileSecure(fullPath))
            return false;

        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous
            | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
        return actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record OfficeBugfixRunResult(
    int ProcessId,
    int? ExitCode,
    bool TimedOut);
