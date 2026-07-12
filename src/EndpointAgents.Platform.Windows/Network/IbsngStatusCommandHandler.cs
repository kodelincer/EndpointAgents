using EndpointAgents.Core.Commands;
using EndpointAgents.Platform.Windows.Diagnostics;
using EndpointAgents.Shared.Rpc;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text.Json;

namespace EndpointAgents.Platform.Windows.Network;

[SupportedOSPlatform("windows")]
public sealed class IbsngStatusCommandHandler : IEndpointCommandHandler
{
    private readonly ILogger<IbsngStatusCommandHandler> _logger;
    private readonly string? _portalUrl;
    private readonly bool _includeWindowsUser;

    public IbsngStatusCommandHandler(
        ILogger<IbsngStatusCommandHandler> logger,
        string? portalUrl,
        bool includeWindowsUser)
    {
        _logger = logger;
        _portalUrl = portalUrl;
        _includeWindowsUser = includeWindowsUser;
    }

    public string Method => "network.ibsng_status";

    public async Task<RpcResponse> HandleAsync(
        RpcRequest request,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        var options = ParseOptions(request.Params);
        var portalUrl = options.PortalUrl ?? _portalUrl;
        var includeWindowsUser = options.IncludeWindowsUser ?? _includeWindowsUser;
        var warnings = new List<DiagnosticWarning>();
        var checks = new List<DiagnosticCheckResult>();

        if (string.IsNullOrWhiteSpace(portalUrl))
        {
            warnings.Add(new DiagnosticWarning(
                "network.ibsng.configuration",
                "IBSng portal URL is not configured. Set Diagnostics:Ibsng:PortalUrl or pass params.portalUrl."));
        }
        else
        {
            checks.Add(await RunPortalCheckAsync(portalUrl, cancellationToken));
        }

        checks.Add(await RunRasCheckAsync(cancellationToken));

        var result = new
        {
            PortalUrl = portalUrl,
            AccountHint = includeWindowsUser ? Environment.UserName : null,
            CredentialsCollected = false,
            Note = "EndpointAgents does not collect IBSng passwords, cookies, browser tokens, or session secrets."
        };

        return RpcResponse.Success(
            request.Id,
            DiagnosticResponse.Create(context, result, warnings, checks));
    }

    private async Task<DiagnosticCheckResult> RunPortalCheckAsync(
        string portalUrl,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false
            };

            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(8)
            };

            var uri = BuildUri(portalUrl);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            stopwatch.Stop();

            var redirectLocation = response.Headers.Location?.ToString();
            var captivePortalLikely =
                (int)response.StatusCode is >= 300 and < 400
                && !string.IsNullOrWhiteSpace(redirectLocation);

            return new DiagnosticCheckResult(
                "ibsng.portal",
                response.IsSuccessStatusCode || captivePortalLikely ? "Pass" : "Warning",
                new
                {
                    PortalUrl = uri.ToString(),
                    StatusCode = (int)response.StatusCode,
                    response.ReasonPhrase,
                    RedirectLocation = redirectLocation,
                    CaptivePortalLikely = captivePortalLikely || LooksLikeCaptivePortal(redirectLocation)
                },
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "IBSng portal check failed.");

            return new DiagnosticCheckResult(
                "ibsng.portal",
                "Fail",
                null,
                stopwatch.ElapsedMilliseconds,
                DiagnosticResponse.Sanitize(ex));
        }
    }

    private static async Task<DiagnosticCheckResult> RunRasCheckAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "rasdial",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            var exitTask = process.WaitForExitAsync(cancellationToken);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
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
            stopwatch.Stop();

            return new DiagnosticCheckResult(
                "ibsng.ras_connections",
                "Pass",
                new
                {
                    ExitCode = exited ? process.ExitCode : (int?)null,
                    TimedOut = !exited,
                    Output = Truncate(output),
                    Error = Truncate(error)
                },
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();

            return new DiagnosticCheckResult(
                "ibsng.ras_connections",
                "Warning",
                null,
                stopwatch.ElapsedMilliseconds,
                DiagnosticResponse.Sanitize(ex));
        }
    }

    private static IbsngOptions ParseOptions(JsonElement? parameters)
    {
        string? portalUrl = null;
        bool? includeWindowsUser = null;

        if (parameters is { ValueKind: JsonValueKind.Object } element)
        {
            if (element.TryGetProperty("portalUrl", out var portalElement)
                && portalElement.ValueKind == JsonValueKind.String)
            {
                portalUrl = portalElement.GetString();
            }

            if (element.TryGetProperty("includeWindowsUser", out var userElement)
                && userElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                includeWindowsUser = userElement.GetBoolean();
            }
        }

        return new IbsngOptions(portalUrl, includeWindowsUser);
    }

    private static Uri BuildUri(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return uri;
        }

        return new Uri($"http://{value}");
    }

    private static bool LooksLikeCaptivePortal(string? redirectLocation)
    {
        if (string.IsNullOrWhiteSpace(redirectLocation))
        {
            return false;
        }

        var value = redirectLocation.ToLowerInvariant();
        return value.Contains("ibs", StringComparison.Ordinal)
            || value.Contains("login", StringComparison.Ordinal)
            || value.Contains("portal", StringComparison.Ordinal)
            || value.Contains("auth", StringComparison.Ordinal);
    }

    private static string Truncate(string value, int maxLength = 4000)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
    }

    private sealed record IbsngOptions(
        string? PortalUrl,
        bool? IncludeWindowsUser);
}
