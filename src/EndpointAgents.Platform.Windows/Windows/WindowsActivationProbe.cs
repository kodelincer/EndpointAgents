using EndpointAgents.Shared.Windows;
using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace EndpointAgents.Platform.Windows.Windows;

public interface IWindowsActivationProbe
{
    Task<WindowsActivationReport> ProbeAsync(CancellationToken cancellationToken);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsActivationProbe : IWindowsActivationProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private const string PowerShellScript =
        "$ErrorActionPreference='Stop';" +
        "$p=Get-CimInstance SoftwareLicensingProduct -Filter \"ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f' AND PartialProductKey IS NOT NULL\"|" +
        "Where-Object {$_.PartialProductKey}|Sort-Object @{Expression={if($_.LicenseStatus -eq 1){0}else{1}}}|" +
        "Select-Object -First 1 Name,Description,LicenseStatus,LicenseStatusReason,GracePeriodRemaining,ProductKeyChannel;" +
        "if($null -eq $p){'{}'}else{$p|ConvertTo-Json -Compress}";

    public async Task<WindowsActivationReport> ProbeAsync(
        CancellationToken cancellationToken)
    {
        var machine = CollectMachineInfo();
        try
        {
            var output = await RunAsync(cancellationToken);
            return Parse(output, machine);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed(machine, "ToolTimedOut");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Failed(machine, "ToolExecutionFailed");
        }
    }

    internal static WindowsActivationReport Parse(string json,
        WindowsMachineInfo? machine = null)
    {
        machine ??= new WindowsMachineInfo(null, null, null, null,
            RuntimeInformation.OSArchitecture.ToString());
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("LicenseStatus", out var statusValue)
            || !statusValue.TryGetInt32(out var status))
            return Failed(machine, "LicenseNotFound");

        var reason = root.TryGetProperty("LicenseStatusReason", out var reasonValue)
            && reasonValue.TryGetInt64(out var reasonNumber)
            ? $"0x{unchecked((uint)reasonNumber):X8}" : null;
        int? grace = root.TryGetProperty("GracePeriodRemaining", out var graceValue)
            && graceValue.TryGetInt32(out var graceMinutes) ? graceMinutes : null;
        var channel = ReadString(root, "ProductKeyChannel")
            ?? ParseChannel(ReadString(root, "Description"));

        return new WindowsActivationReport(machine.ProductName, machine.EditionId,
            machine.DisplayVersion, machine.Build, machine.Architecture,
            MapState(status), status, reason, grace, channel, true);
    }

    internal static string MapState(int status) => status switch
    {
        0 => WindowsActivationStates.Unlicensed,
        1 => WindowsActivationStates.Licensed,
        2 or 3 or 4 or 6 => WindowsActivationStates.Grace,
        5 => WindowsActivationStates.Notification,
        _ => WindowsActivationStates.Unknown
    };

    private static async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("-NoLogo");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(PowerShellScript);
        if (!process.Start())
            throw new InvalidOperationException("The Windows activation probe did not start.");

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        var output = await stdout;
        _ = await stderr;
        if (process.ExitCode != 0 || output.Length > 64 * 1024)
            throw new InvalidDataException("The Windows activation probe failed.");
        return output;
    }

    private static WindowsMachineInfo CollectMachineInfo()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = key?.GetValue("CurrentBuildNumber")?.ToString();
        var ubr = key?.GetValue("UBR")?.ToString();
        return new WindowsMachineInfo(
            WindowsVersionInfo.NormalizeProductName(
                key?.GetValue("ProductName")?.ToString(), build),
            key?.GetValue("EditionID")?.ToString(),
            key?.GetValue("DisplayVersion")?.ToString(),
            string.IsNullOrWhiteSpace(ubr) ? build : $"{build}.{ubr}",
            RuntimeInformation.OSArchitecture.ToString());
    }

    private static WindowsActivationReport Failed(WindowsMachineInfo machine,
        string failureCode) => new(machine.ProductName, machine.EditionId,
        machine.DisplayVersion, machine.Build, machine.Architecture,
        WindowsActivationStates.Unknown, null, null, null, null, false, failureCode);

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string? ParseChannel(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return null;
        var comma = description.LastIndexOf(',');
        var value = (comma >= 0 ? description[(comma + 1)..] : description).Trim();
        return value.Length is > 0 and <= 80 ? value : null;
    }
}

public sealed record WindowsMachineInfo(string? ProductName, string? EditionId,
    string? DisplayVersion, string? Build, string Architecture);
