using System.Diagnostics;
using System.Security.Cryptography;
using EndpointAgents.Shared.Office;

namespace EndpointAgents.Platform.Windows.Office;

public sealed class OfficeBugfixRunner
{
    public async Task<int> RunAsync(string path, string expectedSha256,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetFullPath(OfficeRemediationPaths.StagingRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(fullPath).Equals("office-activation-repair.bat",
                StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullPath)
            || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0
            || !await MatchesHashAsync(fullPath, expectedSha256, cancellationToken))
            throw new InvalidDataException("The staged Office bugfix is not approved.");

        using var process = new Process { StartInfo = CreateStartInfo(fullPath) };
        if (!process.Start())
            throw new InvalidOperationException("The Office bugfix process did not start.");
        await process.StandardInput.WriteLineAsync("1");
        await process.StandardInput.FlushAsync(cancellationToken);
        process.StandardInput.Close();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return process.ExitCode;
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

    private static async Task<bool> MatchesHashAsync(string path, string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
            return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous
            | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
        return actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }
}
