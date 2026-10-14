using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace EndpointAgents.Platform.Windows.Actions.Infrastructure;

public sealed record WebProbeResult(
    bool Reachable,
    int? StatusCode,
    bool AuthenticationRequired,
    bool? CertificateValid,
    string? CertificateErrors,
    DateTimeOffset? CertificateExpiresUtc,
    bool? UsedProxy,
    long ElapsedMs,
    string? Error);

/// <summary>One GET to an environment-map web address. Reads at most a little of the body.</summary>
public static class WebProbe
{
    public static async Task<WebProbeResult> ProbeAsync(string url, bool useDefaultCredentials,
        CancellationToken cancellationToken)
    {
        bool? certificateValid = null;
        string? certificateErrors = null;
        DateTimeOffset? expires = null;
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            UseDefaultCredentials = useDefaultCredentials,
            UseProxy = true,
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            {
                certificateValid = errors == SslPolicyErrors.None;
                certificateErrors = errors == SslPolicyErrors.None ? null : errors.ToString();
                if (certificate is not null)
                    expires = new DateTimeOffset(certificate.NotAfter).ToUniversalTime();
                // Keep going so the probe can still report the HTTP status.
                return true;
            }
        };
        bool? usedProxy = null;
        try
        {
            var proxy = handler.Proxy ?? HttpClient.DefaultProxy;
            var uri = new Uri(url);
            usedProxy = !proxy.IsBypassed(uri) && proxy.GetProxy(uri) is { } address
                && address != uri;
        }
        catch { }

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await client.GetAsync(url,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;
            return new WebProbeResult(true, status,
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                certificateValid, certificateErrors, expires, usedProxy,
                watch.ElapsedMilliseconds, null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WebProbeResult(false, null, false, certificateValid, certificateErrors,
                expires, usedProxy, watch.ElapsedMilliseconds, "Timeout");
        }
        catch (HttpRequestException ex)
        {
            return new WebProbeResult(false, null, false, certificateValid, certificateErrors,
                expires, usedProxy, watch.ElapsedMilliseconds,
                ex.HttpRequestError.ToString());
        }
    }
}
