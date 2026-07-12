using EndpointAgents.Core.Commands;
using EndpointAgents.Platform.Windows.Diagnostics;
using EndpointAgents.Shared.Rpc;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Software;

[SupportedOSPlatform("windows")]
public sealed class OfficeStatusCommandHandler : IEndpointCommandHandler
{
    private readonly ILogger<OfficeStatusCommandHandler> _logger;

    public OfficeStatusCommandHandler(ILogger<OfficeStatusCommandHandler> logger)
    {
        _logger = logger;
    }

    public string Method => "software.office_status";

    public async Task<RpcResponse> HandleAsync(
        RpcRequest request,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        var warnings = new List<DiagnosticWarning>();
        var checks = new List<DiagnosticCheckResult>();

        var clickToRun = Safe("office.click_to_run", () => CollectClickToRun(), warnings);
        var installedProducts = Safe("office.installed_products", () => CollectInstalledProducts(), warnings);

        checks.Add(await RunProcessCheckAsync(
            "office.activation",
            FindOsppScript(),
            "//Nologo \"{0}\" /dstatus",
            output => new
            {
                RawStatus = Truncate(output),
                Licenses = ParseOfficeLicenses(output)
            },
            cancellationToken));

        checks.Add(await RunKmsDnsCheckAsync(cancellationToken));
        checks.Add(await RunKmsPortCheckAsync(clickToRun?.KmsHost, cancellationToken));
        checks.Add(await CollectServiceStatusAsync("ClickToRunSvc", cancellationToken));
        checks.Add(await CollectServiceStatusAsync("sppsvc", cancellationToken));
        checks.Add(await RunOfficeEventLogCheckAsync(cancellationToken));

        var result = new
        {
            Installed = installedProducts?.Length > 0 || clickToRun?.Installed == true,
            Products = installedProducts ?? Array.Empty<object>(),
            ClickToRun = clickToRun,
            SystemTime = new
            {
                LocalNow = DateTimeOffset.Now,
                UtcNow = DateTimeOffset.UtcNow,
                TimeZone = TimeZoneInfo.Local.DisplayName
            },
            FullProductKeysCollected = false,
            SensitiveTokensCollected = false
        };

        return RpcResponse.Success(
            request.Id,
            DiagnosticResponse.Create(context, result, warnings, checks));
    }

    private ClickToRunStatus CollectClickToRun()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration");

        if (key is null)
        {
            return new ClickToRunStatus(false, null, null, null, null, null, null, null);
        }

        return new ClickToRunStatus(
            Installed: true,
            VersionToReport: key.GetValue("VersionToReport")?.ToString(),
            Platform: key.GetValue("Platform")?.ToString(),
            ClientCulture: key.GetValue("ClientCulture")?.ToString(),
            UpdateChannel: key.GetValue("UpdateChannel")?.ToString(),
            CDNBaseUrl: key.GetValue("CDNBaseUrl")?.ToString(),
            ProductReleaseIds: key.GetValue("ProductReleaseIds")?.ToString(),
            KmsHost: key.GetValue("KeyManagementServiceName")?.ToString());
    }

    private object[] CollectInstalledProducts()
    {
        var products = new List<object>();

        CollectUninstallProducts(RegistryView.Registry64, products);
        CollectUninstallProducts(RegistryView.Registry32, products);

        return products
            .GroupBy(product => product.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static void CollectUninstallProducts(RegistryView view, List<object> products)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var uninstallKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");

        if (uninstallKey is null)
        {
            return;
        }

        foreach (var subKeyName in uninstallKey.GetSubKeyNames())
        {
            using var subKey = uninstallKey.OpenSubKey(subKeyName);
            var displayName = subKey?.GetValue("DisplayName")?.ToString();

            if (string.IsNullOrWhiteSpace(displayName)
                || !displayName.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
                || !displayName.Contains("Office", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            products.Add(new
            {
                DisplayName = displayName,
                DisplayVersion = subKey?.GetValue("DisplayVersion")?.ToString(),
                Publisher = subKey?.GetValue("Publisher")?.ToString(),
                InstallDate = subKey?.GetValue("InstallDate")?.ToString(),
                RegistryView = view.ToString()
            });
        }
    }

    private async Task<DiagnosticCheckResult> RunProcessCheckAsync(
        string name,
        string? filePath,
        string argumentFormat,
        Func<string, object> parse,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return new DiagnosticCheckResult(
                name,
                "Warning",
                new { Message = "Office licensing script ospp.vbs was not found." },
                stopwatch.ElapsedMilliseconds);
        }

        try
        {
            var output = await RunProcessAsync(
                "cscript.exe",
                string.Format(argumentFormat, filePath),
                TimeSpan.FromSeconds(15),
                cancellationToken);

            stopwatch.Stop();

            return new DiagnosticCheckResult(
                name,
                output.ExitCode == 0 ? "Pass" : "Warning",
                parse(output.Output + output.Error),
                stopwatch.ElapsedMilliseconds,
                output.ExitCode == 0 ? null : new { output.ExitCode });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Office diagnostic check {CheckName} failed.", name);

            return new DiagnosticCheckResult(
                name,
                "Warning",
                null,
                stopwatch.ElapsedMilliseconds,
                DiagnosticResponse.Sanitize(ex));
        }
    }

    private static async Task<DiagnosticCheckResult> RunKmsDnsCheckAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var output = await RunProcessAsync(
                "nslookup.exe",
                "-type=srv _vlmcs._tcp",
                TimeSpan.FromSeconds(8),
                cancellationToken);

            stopwatch.Stop();

            return new DiagnosticCheckResult(
                "office.kms_dns",
                output.ExitCode == 0 ? "Pass" : "Warning",
                new
                {
                    output.ExitCode,
                    Output = Truncate(output.Output),
                    Error = Truncate(output.Error)
                },
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();

            return new DiagnosticCheckResult(
                "office.kms_dns",
                "Warning",
                null,
                stopwatch.ElapsedMilliseconds,
                DiagnosticResponse.Sanitize(ex));
        }
    }

    private static async Task<DiagnosticCheckResult> RunKmsPortCheckAsync(
        string? kmsHost,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(kmsHost))
        {
            return new DiagnosticCheckResult(
                "office.kms_port_1688",
                "Warning",
                new { Message = "No KMS host was found in local Office configuration." },
                stopwatch.ElapsedMilliseconds);
        }

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(kmsHost, 1688, cancellationToken);
            stopwatch.Stop();

            return new DiagnosticCheckResult(
                "office.kms_port_1688",
                "Pass",
                new
                {
                    Host = kmsHost,
                    Port = 1688,
                    Connected = true
                },
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();

            return new DiagnosticCheckResult(
                "office.kms_port_1688",
                "Fail",
                new
                {
                    Host = kmsHost,
                    Port = 1688,
                    Connected = false
                },
                stopwatch.ElapsedMilliseconds,
                DiagnosticResponse.Sanitize(ex));
        }
    }

    private static async Task<DiagnosticCheckResult> CollectServiceStatusAsync(
        string serviceName,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var output = await RunProcessAsync(
                "sc.exe",
                $"query {serviceName}",
                TimeSpan.FromSeconds(5),
                cancellationToken);
            stopwatch.Stop();

            return new DiagnosticCheckResult(
                $"service.{serviceName}",
                output.ExitCode == 0 && output.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)
                    ? "Pass"
                    : "Warning",
                new
                {
                    ServiceName = serviceName,
                    output.ExitCode,
                    Output = Truncate(output.Output),
                    Error = Truncate(output.Error)
                },
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            return new DiagnosticCheckResult(
                $"service.{serviceName}",
                "Warning",
                null,
                stopwatch.ElapsedMilliseconds,
                DiagnosticResponse.Sanitize(ex));
        }
    }

    private static async Task<DiagnosticCheckResult> RunOfficeEventLogCheckAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var output = await RunProcessAsync(
                "wevtutil.exe",
                "qe Application /c:20 /rd:true /f:text /q:\"*[System[(Level=2 or Level=3)]]\"",
                TimeSpan.FromSeconds(8),
                cancellationToken);
            stopwatch.Stop();

            return new DiagnosticCheckResult(
                "eventlog.office_licensing",
                output.ExitCode == 0 ? "Pass" : "Warning",
                new
                {
                    output.ExitCode,
                    Output = Truncate(output.Output),
                    Error = Truncate(output.Error)
                },
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            return new DiagnosticCheckResult(
                "eventlog.office_licensing",
                "Warning",
                null,
                stopwatch.ElapsedMilliseconds,
                DiagnosticResponse.Sanitize(ex));
        }
    }

    private static string? FindOsppScript()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        var candidates = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .SelectMany(root => new[]
            {
                Path.Combine(root, "Microsoft Office", "Office16", "OSPP.VBS"),
                Path.Combine(root, "Microsoft Office", "Office15", "OSPP.VBS"),
                Path.Combine(root, "Microsoft Office", "Office14", "OSPP.VBS"),
                Path.Combine(root, "Microsoft Office", "root", "Office16", "OSPP.VBS")
            });

        return candidates.FirstOrDefault(File.Exists);
    }

    private static object[] ParseOfficeLicenses(string output)
    {
        return output
            .Split("LICENSE NAME:", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1)
            .Select(block => new
            {
                LicenseName = FirstValue(block),
                LicenseDescription = ValueAfter(block, "LICENSE DESCRIPTION:"),
                LicenseStatus = ValueAfter(block, "LICENSE STATUS:"),
                ErrorCode = ValueAfter(block, "ERROR CODE:"),
                LastFiveProductKeyCharacters = ValueAfter(block, "Last 5 characters of installed product key:")
            })
            .Cast<object>()
            .ToArray();
    }

    private T? Safe<T>(
        string source,
        Func<T> action,
        List<DiagnosticWarning> warnings)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Source}: failed to collect Office diagnostic section.", source);
            warnings.Add(new DiagnosticWarning(source, "Failed to collect Office diagnostic section.", ex.GetType().Name));
            return default;
        }
    }

    private static async Task<ProcessOutput> RunProcessAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        var exitTask = process.WaitForExitAsync(cancellationToken);
        var timeoutTask = Task.Delay(timeout, cancellationToken);
        var exited = await Task.WhenAny(exitTask, timeoutTask) == exitTask;

        if (!exited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort cleanup after a timed out diagnostic command.
            }
        }

        var output = await outputTask;
        var error = await errorTask;

        return new ProcessOutput(exited ? process.ExitCode : -1, output, error);
    }

    private static string? FirstValue(string block)
    {
        return block
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
    }

    private static string? ValueAfter(string block, string marker)
    {
        var line = block
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(x => x.StartsWith(marker, StringComparison.OrdinalIgnoreCase));

        return line is null ? null : line[marker.Length..].Trim();
    }

    private static string Truncate(string value, int maxLength = 4000)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
    }

    private sealed record ClickToRunStatus(
        bool Installed,
        string? VersionToReport,
        string? Platform,
        string? ClientCulture,
        string? UpdateChannel,
        string? CDNBaseUrl,
        string? ProductReleaseIds,
        string? KmsHost);

    private sealed record ProcessOutput(
        int ExitCode,
        string Output,
        string Error);
}
