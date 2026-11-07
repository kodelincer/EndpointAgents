using ControlPlane.Api.Chat;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ControlPlane.Api.Problems.Forwarding;

public static class LogServerTypes
{
    public const string Splunk = "Splunk";
    public const string Graylog = "Graylog";
    public const string Syslog = "Syslog";
}

public static class LogServerProtocols
{
    public const string Https = "https";
    public const string Http = "http";
    public const string Udp = "udp";
    public const string Tcp = "tcp";
    public const string Tls = "tls";
}

/// <summary>The outside log server. Off until an admin turns it on. The token is never part of this.</summary>
public sealed record LogServerSettings(
    bool Enabled,
    string Type,
    string Host,
    int Port,
    string Protocol,
    string? Index,
    string? CertificateThumbprint,
    string? UpdatedBy = null,
    DateTimeOffset? UpdatedAtUtc = null)
{
    public static readonly LogServerSettings Default =
        new(false, LogServerTypes.Splunk, "", 8088, LogServerProtocols.Https, null, null);
}

public static partial class LogServerValidation
{
    public const int MaxTokenCharacters = 200;

    /// <summary>Returns what is wrong, or null.</summary>
    public static string? Problem(LogServerSettings settings, bool hasToken)
    {
        if (settings.Type is not (LogServerTypes.Splunk or LogServerTypes.Graylog or LogServerTypes.Syslog))
            return "The type must be Splunk, Graylog or Syslog.";
        if (!Host().IsMatch(settings.Host))
            return "Enter the server name or address only, without http:// or a path.";
        if (settings.Port is < 1 or > 65535) return "The port must be between 1 and 65535.";
        var protocols = settings.Type == LogServerTypes.Syslog
            ? new[] { LogServerProtocols.Udp, LogServerProtocols.Tcp, LogServerProtocols.Tls }
            : [LogServerProtocols.Https, LogServerProtocols.Http];
        if (!protocols.Contains(settings.Protocol))
            return $"For {settings.Type} the protocol must be {string.Join(" or ", protocols)}.";
        if (settings.Type == LogServerTypes.Splunk)
        {
            if (!hasToken) return "Splunk needs an HTTP Event Collector token.";
            // The token would cross the network in clear text.
            if (settings.Protocol == LogServerProtocols.Http && !IsLoopback(settings.Host))
                return "Splunk needs HTTPS: the token must not cross the network unencrypted.";
        }
        if (settings.Index is { Length: > 0 } index && !IndexName().IsMatch(index))
            return "The Splunk index name may hold letters, digits, - and _.";
        if (settings.CertificateThumbprint is { Length: > 0 } thumbprint
            && NormalizeThumbprint(thumbprint) is null)
            return "The certificate fingerprint must be a SHA-256 value (64 hexadecimal characters).";
        return null;
    }

    public static string? NormalizeThumbprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = value.Replace(":", "").Replace(" ", "").Trim().ToUpperInvariant();
        return clean.Length == 64 && clean.All(Uri.IsHexDigit) ? clean : null;
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9.\-]{0,251}[A-Za-z0-9])?$|^\[?[0-9A-Fa-f:]{2,45}\]?$")]
    private static partial Regex Host();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,80}$")]
    private static partial Regex IndexName();
}

/// <summary>
/// Log server settings, in the main database so clearing the problem file keeps them. The token
/// goes through ASP.NET data protection, the keys of which Windows protects; it is never returned
/// to the dashboard and never logged.
/// </summary>
public sealed class LogServerSettingsStore(IOptions<ChatOptions> options, IDataProtectionProvider protection)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _databasePath = Path.GetFullPath(options.Value.DatabasePath);
    private readonly IDataProtector _protector = protection.CreateProtector("ControlPlane.Problems.LogServerToken.v1");

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS log_server_settings (
                setting_key TEXT PRIMARY KEY,
                setting_json TEXT NOT NULL,
                protected_token TEXT NULL,
                updated_at_utc TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<LogServerSettings> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT setting_json FROM log_server_settings WHERE setting_key = 'log-server';";
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (json is null) return LogServerSettings.Default;
        try { return JsonSerializer.Deserialize<LogServerSettings>(json, Json) ?? LogServerSettings.Default; }
        catch (JsonException) { return LogServerSettings.Default; }
    }

    public async Task<bool> HasTokenAsync(CancellationToken cancellationToken) =>
        await ReadProtectedAsync(cancellationToken) is not null;

    /// <summary>The saved token, or null when there is none or it can no longer be read.</summary>
    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken)
    {
        var stored = await ReadProtectedAsync(cancellationToken);
        if (stored is null) return null;
        try { return _protector.Unprotect(stored); }
        catch (global::System.Security.Cryptography.CryptographicException) { return null; }
    }

    /// <summary>A null <paramref name="newToken"/> keeps the saved token; an empty one removes it.</summary>
    public async Task SaveAsync(LogServerSettings settings, string? newToken, CancellationToken cancellationToken)
    {
        var stored = newToken is null ? await ReadProtectedAsync(cancellationToken)
            : newToken.Length == 0 ? null : _protector.Protect(newToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO log_server_settings (setting_key, setting_json, protected_token, updated_at_utc)
            VALUES ('log-server', $json, $token, $now)
            ON CONFLICT(setting_key) DO UPDATE SET setting_json = excluded.setting_json,
                protected_token = excluded.protected_token, updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(settings, Json));
        command.Parameters.AddWithValue("$token", (object?)stored ?? DBNull.Value);
        command.Parameters.AddWithValue("$now",
            DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<string?> ReadProtectedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT protected_token FROM log_server_settings WHERE setting_key = 'log-server';";
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }
}
