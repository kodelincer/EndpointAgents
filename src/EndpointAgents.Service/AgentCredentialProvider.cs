using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EndpointAgents.Service;

public sealed record AgentDeviceCredential(string AgentId, string DeviceToken);

public sealed class AgentCredentialProvider(
    IOptions<ControlPlaneOptions> options,
    ILogger<AgentCredentialProvider> logger)
{
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("EndpointAgents.DeviceCredential.v1");
    private readonly ControlPlaneOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<AgentDeviceCredential?> GetOrEnrollAsync(
        string agentId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = Load();
            if (existing is not null) return existing;
            if (string.IsNullOrWhiteSpace(_options.EnrollmentCode))
            {
                logger.LogWarning(
                    "No device credential or enrollment code is configured. Only legacy compatibility routes are available.");
                return null;
            }

            var hub = new Uri(_options.HubUrl, UriKind.Absolute);
            var enrollmentUri = new Uri(hub.GetLeftPart(UriPartial.Authority)
                + "/api/agent-enrollments/redeem");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var response = await client.PostAsJsonAsync(enrollmentUri, new
            {
                enrollmentCode = _options.EnrollmentCode.Trim(),
                agentId,
                machineName = Environment.MachineName
            }, cancellationToken);
            response.EnsureSuccessStatusCode();
            var redemption = await response.Content.ReadFromJsonAsync<AgentDeviceCredential>(
                cancellationToken: cancellationToken)
                ?? throw new InvalidDataException(
                    "The control plane returned an unreadable enrollment response.");
            Save(redemption);
            logger.LogInformation("Endpoint device enrollment completed for agent {AgentId}.",
                redemption.AgentId);
            return redemption;
        }
        finally
        {
            _gate.Release();
        }
    }

    private AgentDeviceCredential? Load()
    {
        if (!File.Exists(_options.DeviceCredentialPath)) return null;
        try
        {
            var protectedBytes = File.ReadAllBytes(_options.DeviceCredentialPath);
            var clear = ProtectedData.Unprotect(protectedBytes, Entropy,
                DataProtectionScope.LocalMachine);
            return JsonSerializer.Deserialize<AgentDeviceCredential>(clear)
                ?? throw new InvalidDataException("The device credential is empty.");
        }
        catch (Exception ex) when (ex is CryptographicException
                                   or JsonException or InvalidDataException)
        {
            throw new InvalidDataException(
                "The protected endpoint device credential cannot be read.", ex);
        }
    }

    private void Save(AgentDeviceCredential credential)
    {
        var directory = Path.GetDirectoryName(_options.DeviceCredentialPath)
            ?? throw new InvalidOperationException(
                "The device credential path has no parent directory.");
        Directory.CreateDirectory(directory);
        var clear = JsonSerializer.SerializeToUtf8Bytes(credential);
        var protectedBytes = ProtectedData.Protect(clear, Entropy,
            DataProtectionScope.LocalMachine);
        var temporaryPath = _options.DeviceCredentialPath + "." + Guid.NewGuid().ToString("N")
            + ".tmp";
        File.WriteAllBytes(temporaryPath, protectedBytes);
        File.Move(temporaryPath, _options.DeviceCredentialPath, overwrite: true);
    }
}
