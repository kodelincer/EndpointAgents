using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace EndpointAgents.Platform.Windows.Actions.Infrastructure;

public sealed record FixedToolResult(int ExitCode, string Output, bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

/// <summary>
/// Starts one of a few built-in Windows programs from System32 with arguments written in
/// code. No shell (cmd/PowerShell) is involved, the program name is checked against a fixed
/// list, and callers never pass text that came from the agent.
/// </summary>
[SupportedOSPlatform("windows")]
public static class FixedTool
{
    private static readonly HashSet<string> AllowedPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "ipconfig.exe", "w32tm.exe", "gpupdate.exe", "klist.exe", "netsh.exe",
        "tzutil.exe", "shutdown.exe", "powercfg.exe"
    };

    private const int MaxOutputCharacters = 8 * 1024;

    public static Task<FixedToolResult> RunAsync(string program, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken, string? standardInput = null)
    {
        if (!AllowedPrograms.Contains(program))
            throw new InvalidOperationException($"{program} is not an allowed program.");
        var path = Path.Combine(Environment.SystemDirectory, program);
        return RunPathAsync(path, arguments, timeout, cancellationToken, standardInput);
    }

    /// <summary>Starts a Defender quick scan and returns without waiting for it to finish.</summary>
    public static bool StartDefenderQuickScan()
    {
        var path = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
        if (!File.Exists(path)) return false;
        var start = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-Scan");
        start.ArgumentList.Add("-ScanType");
        start.ArgumentList.Add("1");
        using var process = Process.Start(start);
        return process is not null;
    }

    private static async Task<FixedToolResult> RunPathAsync(string path,
        IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken,
        string? standardInput)
    {
        if (!File.Exists(path))
            return new FixedToolResult(-1, "Program not found.", false);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = standardInput is not null,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            process.StandardInput.Close();
        }

        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            cancellationToken.ThrowIfCancellationRequested();
            return new FixedToolResult(-1, string.Empty, true);
        }
        var text = (await output) + (await error);
        return new FixedToolResult(process.ExitCode,
            text.Length > MaxOutputCharacters ? text[..MaxOutputCharacters] : text, false);
    }
}
