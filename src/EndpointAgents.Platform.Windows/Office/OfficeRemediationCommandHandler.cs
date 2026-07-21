using EndpointAgents.Core.Commands;
using EndpointAgents.Platform.Windows.Diagnostics;
using EndpointAgents.Platform.Windows.Ibsng;
using EndpointAgents.Shared.LocalIpc;
using EndpointAgents.Shared.Office;
using EndpointAgents.Shared.Rpc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EndpointAgents.Platform.Windows.Office;

/// <summary>
/// Downloads and launches one compatible administrator-approved Office remediation.
/// The caller supplies only an artifact ID; filenames, hashes, paths, and arguments
/// are never accepted from the command channel.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class OfficeRemediationCommandHandler(
    OfficeActivationService activation,
    IOfficeUserSessionSource sessionSource,
    ITrayCommandClient trayClient,
    IOptions<OfficeRemediationOptions> options,
    Uri hubUri,
    ILogger<OfficeRemediationCommandHandler> logger) : IEndpointCommandHandler
{
    private static readonly string[] OfficeProcessNames =
        ["WINWORD", "EXCEL", "POWERPNT", "OUTLOOK", "ONENOTE", "MSACCESS", "MSPUB"];
    private readonly OfficeRemediationOptions _options = options.Value;

    public string Method => "software.office_remediation_run";

    public async Task<RpcResponse> HandleAsync(RpcRequest request,
        CommandExecutionContext context, CancellationToken cancellationToken)
    {
        if (!TryParseArtifactId(request.Params, out var artifactId))
            return RpcResponse.Failure(request.Id, 400,
                "params.artifactId must be an approved artifact ID.");

        var status = await activation.GetStatusAsync(request.Id, cancellationToken);
        var descriptor = status.ApprovedArtifacts.FirstOrDefault(item =>
            item.ArtifactId.Equals(artifactId, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null
            || !status.Report.CompatibleArtifactIds.Contains(artifactId,
                StringComparer.OrdinalIgnoreCase))
        {
            return RpcResponse.Failure(request.Id, 409,
                "The requested artifact is not compatible with the endpoint's current Office product, version, and activation state.",
                new
                {
                    status.Report.OfficeVersion,
                    status.Report.ProductFamilies,
                    status.Report.OverallState,
                    status.Report.CompatibleArtifactIds
                });
        }

        if (!descriptor.Kind.Equals(OfficeRemediationArtifactKinds.MicrosoftSignedExecutable,
                StringComparison.OrdinalIgnoreCase)
            || !Path.GetExtension(descriptor.FileName).Equals(".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return RpcResponse.Failure(request.Id, 415,
                "Only Microsoft-signed executable remediation artifacts can be run by the endpoint agent.");
        }

        if (Path.GetFileName(descriptor.FileName) != descriptor.FileName)
            return RpcResponse.Failure(request.Id, 412, "The catalog artifact filename is invalid.");

        if (descriptor.RequiresOfficeAppsClosed)
        {
            var running = GetRunningOfficeProcesses();
            if (running.Length > 0)
                return RpcResponse.Failure(request.Id, 409,
                    "Close the listed Office applications before running this remediation.",
                    new { RunningProcesses = running });
        }

        var session = sessionSource.GetActiveSession();
        if (session is null)
            return RpcResponse.Failure(request.Id, 404,
                "No interactive Windows user is available to approve and run the remediation.");

        var artifactDirectory = Path.GetFullPath(Path.Combine(_options.StagingRoot, artifactId));
        var stagingRoot = Path.GetFullPath(_options.StagingRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!artifactDirectory.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
            return RpcResponse.Failure(request.Id, 412, "The remediation staging path is invalid.");

        Directory.CreateDirectory(artifactDirectory);
        var destination = Path.Combine(artifactDirectory, descriptor.FileName);
        var downloaded = false;
        try
        {
            if (!await IsApprovedLocalFileAsync(destination, descriptor.Sha256, cancellationToken))
            {
                await DownloadAsync(descriptor, destination, cancellationToken);
                downloaded = true;
            }

            var launch = new OfficeRemediationLaunchRequest(descriptor.ArtifactId,
                descriptor.DisplayName, descriptor.Description, destination, descriptor.Sha256);
            var trayRequest = new TrayIpcRequest(request.Id,
                TrayIpcProtocol.OfficeRemediationLaunchMethod, string.Empty, string.Empty, null,
                OfficeRemediation: launch);
            var trayResponse = await trayClient.SendAsync(session.SessionId, trayRequest,
                cancellationToken);
            if (!trayResponse.Success)
            {
                var code = trayResponse.State == "TrayUnavailable" ? 503
                    : trayResponse.State == "UserDeclined" ? 409 : 412;
                return RpcResponse.Failure(request.Id, code, trayResponse.State,
                    new { trayResponse.Message, descriptor.ArtifactId, session.UserName });
            }

            return RpcResponse.Success(request.Id, DiagnosticResponse.Create(context, new
            {
                State = trayResponse.State,
                descriptor.ArtifactId,
                descriptor.DisplayName,
                descriptor.RequiresRestart,
                Downloaded = downloaded,
                trayResponse.ProcessId,
                trayResponse.SignerSubject,
                session.SessionId,
                session.UserName
            }, status.Issues.Select(issue => new DiagnosticWarning(issue.Source,
                issue.Message, issue.Exception)).ToArray()));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Office remediation {ArtifactId} failed before launch.",
                artifactId);
            return RpcResponse.Failure(request.Id, 500,
                "The approved Office remediation could not be prepared.",
                DiagnosticResponse.Sanitize(ex));
        }
    }

    internal static bool TryParseArtifactId(JsonElement? parameters, out string artifactId)
    {
        artifactId = string.Empty;
        if (parameters is not { ValueKind: JsonValueKind.Object } element
            || !element.TryGetProperty("artifactId", out var value)
            || value.ValueKind != JsonValueKind.String)
            return false;
        artifactId = value.GetString()?.Trim() ?? string.Empty;
        return ArtifactIdPattern().IsMatch(artifactId);
    }

    private async Task DownloadAsync(OfficeRemediationArtifactDescriptor descriptor,
        string destination, CancellationToken cancellationToken)
    {
        var artifactUri = new Uri(hubUri,
            $"/api/files/office/{Uri.EscapeDataString(descriptor.ArtifactId)}");
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        using var response = await client.GetAsync(artifactUri,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException(
                $"The control plane returned HTTP {(int)response.StatusCode} for the approved Office artifact.");
        if (response.Content.Headers.ContentLength is long length
            && length > _options.MaxDownloadBytes)
            throw new InvalidDataException("The Office artifact exceeds the configured download limit.");

        var partial = destination + $".{Guid.NewGuid():N}.download";
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    total += read;
                    if (total > _options.MaxDownloadBytes)
                        throw new InvalidDataException(
                            "The Office artifact exceeds the configured download limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
            }

            if (!await IsApprovedLocalFileAsync(partial, descriptor.Sha256, cancellationToken))
                throw new InvalidDataException(
                    "The downloaded Office artifact does not match the approved executable hash.");
            File.Move(partial, destination, overwrite: true);
        }
        finally
        {
            try { File.Delete(partial); } catch { }
        }
    }

    private static async Task<bool> IsApprovedLocalFileAsync(string path, string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var header = new byte[2];
        if (await stream.ReadAsync(header, cancellationToken) != 2
            || header[0] != (byte)'M' || header[1] != (byte)'Z')
            return false;
        stream.Position = 0;
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        return actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] GetRunningOfficeProcesses() => OfficeProcessNames
        .Where(IsProcessRunning)
        .ToArray();

    private static bool IsProcessRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex ArtifactIdPattern();
}
