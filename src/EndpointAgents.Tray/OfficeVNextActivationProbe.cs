using EndpointAgents.Shared.LocalIpc;
using EndpointAgents.Shared.Office;
using System.Diagnostics;

namespace EndpointAgents.Tray;

/// <summary>Runs Microsoft's installed vNext diagnostic in the interactive user session.</summary>
public sealed class OfficeVNextActivationProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public async Task<TrayIpcResponse> ProbeAsync(string requestId,
        CancellationToken cancellationToken)
    {
        var scriptPath = FindVNextDiagnostic();
        if (scriptPath is null)
            return Response(requestId, false, false, [], "ToolUnavailable");

        try
        {
            var output = await RunAsync(scriptPath, cancellationToken);
            var products = OfficeActivationOutputParser.ParseVNext(output.StandardOutput);
            var succeeded = output.ExitCode == 0;
            return Response(requestId, true, succeeded, products,
                succeeded ? null : "ToolExecutionFailed");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Response(requestId, true, false, [], "ToolTimedOut");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Response(requestId, true, false, [], "ToolExecutionFailed");
        }
    }

    internal static string? FindVNextDiagnostic()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };
        return roots.Where(root => !string.IsNullOrWhiteSpace(root))
            .SelectMany(root => new[]
            {
                Path.Combine(root, "Microsoft Office", "Office16", "vnextdiag.ps1"),
                Path.Combine(root, "Microsoft Office", "root", "Office16", "vnextdiag.ps1")
            })
            .FirstOrDefault(File.Exists);
    }

    private static async Task<ProcessResult> RunAsync(string scriptPath,
        CancellationToken cancellationToken)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var powershell = Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0",
            "powershell.exe");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = powershell,
                WorkingDirectory = Path.GetDirectoryName(scriptPath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("-NoLogo");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(scriptPath);
        process.StartInfo.ArgumentList.Add("-action");
        process.StartInfo.ArgumentList.Add("list");

        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private static TrayIpcResponse Response(string requestId, bool toolAvailable,
        bool succeeded, IReadOnlyList<OfficeActivationProduct> products, string? failureCode)
    {
        var probe = new OfficeActivationProbeResult(OfficeActivationMechanisms.VNext,
            toolAvailable, succeeded, products, failureCode);
        return new TrayIpcResponse(requestId, true, "OfficeActivationInspected",
            OfficeActivation: probe);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
