using EndpointAgents.Shared.Office;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Office;

/// <summary>Inspects volume Office licensing with the locally installed Microsoft OSPP script.</summary>
[SupportedOSPlatform("windows")]
public sealed class OfficeVolumeActivationProbe : IOfficeVolumeActivationProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public async Task<OfficeActivationProbeResult> ProbeAsync(OfficeInventory inventory,
        CancellationToken cancellationToken)
    {
        var script = FindOsppScript(inventory);
        if (script is null)
            return new OfficeActivationProbeResult(OfficeActivationMechanisms.Ospp,
                false, false, [], "ToolUnavailable");

        try
        {
            var output = await RunAsync(script, cancellationToken);
            var products = OfficeActivationOutputParser.ParseOspp(
                output.StandardOutput + Environment.NewLine + output.StandardError);
            return new OfficeActivationProbeResult(OfficeActivationMechanisms.Ospp,
                true, output.ExitCode == 0, products,
                output.ExitCode == 0 ? null : "ToolExecutionFailed");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new OfficeActivationProbeResult(OfficeActivationMechanisms.Ospp,
                true, false, [], "ToolTimedOut");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new OfficeActivationProbeResult(OfficeActivationMechanisms.Ospp,
                true, false, [], "ToolExecutionFailed");
        }
    }

    internal static string? FindOsppScript(OfficeInventory inventory)
    {
        var roots = inventory.ClickToRun.Select(item => item.InstallationPath)
            .Where(path => !string.IsNullOrWhiteSpace(path)).Cast<string>()
            .Concat(new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Microsoft Office"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "Microsoft Office")
            })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return roots.SelectMany(root => new[]
            {
                Path.Combine(root, "root", "Office16", "OSPP.VBS"),
                Path.Combine(root, "Office16", "OSPP.VBS"),
                Path.Combine(root, "Office15", "OSPP.VBS"),
                Path.Combine(root, "Office14", "OSPP.VBS")
            })
            .FirstOrDefault(File.Exists);
    }

    private static async Task<ProcessResult> RunAsync(string scriptPath,
        CancellationToken cancellationToken)
    {
        var cscript = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cscript.exe");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = cscript,
                WorkingDirectory = Path.GetDirectoryName(scriptPath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("//Nologo");
        process.StartInfo.ArgumentList.Add(scriptPath);
        process.StartInfo.ArgumentList.Add("/dstatusall");

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

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
