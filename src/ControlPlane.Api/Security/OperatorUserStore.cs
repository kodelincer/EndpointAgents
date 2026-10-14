using ControlPlane.Api.Chat;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ControlPlane.Api.Security;

public sealed record OperatorUser(
    string UserId,
    string UserName,
    string DisplayName,
    string Role,
    bool Disabled,
    bool MustChangePassword,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastSignInAtUtc,
    string Source = OperatorSources.Local)
{
    internal string PasswordHash { get; init; } = string.Empty;
    internal string SecurityStamp { get; init; } = string.Empty;
}

/// <summary>Dashboard operator accounts, kept in the control-plane SQLite database.</summary>
public sealed partial class OperatorUserStore(IOptions<ChatOptions> options,
    ILogger<OperatorUserStore> logger)
{
    public const string BootstrapUserName = "admin";
    public const string BootstrapPasswordFileName = "initial-admin-password.txt";

    private readonly string _databasePath = Path.GetFullPath(options.Value.DatabasePath);

    public string BootstrapPasswordPath => Path.Combine(
        Path.GetDirectoryName(_databasePath)!, BootstrapPasswordFileName);

    /// <summary>Creates the table and, on a database with no operators, one admin whose
    /// one-time password is written next to the database (never logged).</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using (var create = connection.CreateCommand())
        {
            create.CommandText =
                """
                CREATE TABLE IF NOT EXISTS operator_users (
                    user_id TEXT PRIMARY KEY,
                    user_name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    display_name TEXT NOT NULL,
                    role TEXT NOT NULL,
                    password_hash TEXT NOT NULL,
                    must_change_password INTEGER NOT NULL DEFAULT 1,
                    disabled INTEGER NOT NULL DEFAULT 0,
                    security_stamp TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    last_sign_in_at_utc TEXT NULL
                );
                """;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        // Databases created before directory sign-in have no source column.
        await using (var columns = connection.CreateCommand())
        {
            columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('operator_users') WHERE name = 'source';";
            if (Convert.ToInt64(await columns.ExecuteScalarAsync(cancellationToken)) == 0)
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE operator_users ADD COLUMN source TEXT NOT NULL DEFAULT 'local';";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM operator_users;";
        if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken)) > 0) return;

        var password = OperatorPasswordHasher.GenerateTemporary();
        await CreateAsync(BootstrapUserName, "Administrator", OperatorRoles.Admin, password,
            mustChangePassword: true, cancellationToken);
        await File.WriteAllTextAsync(BootstrapPasswordPath,
            $"""
            EndpointAgents dashboard first sign-in
            User name: {BootstrapUserName}
            Password:  {password}

            You will be asked to choose a new password. Delete this file afterwards.
            """, cancellationToken);
        logger.LogWarning("Created the first dashboard administrator '{UserName}'. Its one-time "
            + "password is in {Path}.", BootstrapUserName, BootstrapPasswordPath);
    }

    public static string? ValidateUserName(string? userName) =>
        userName is not null && UserNamePattern().IsMatch(userName)
            ? null
            : "User name must be 3-32 characters: letters, digits, dot, dash or underscore.";

    public static string NormalizeUserName(string? userName) =>
        (userName ?? string.Empty).Trim().ToLowerInvariant();

    public async Task<OperatorUser> CreateAsync(string userName, string displayName,
        string role, string password, bool mustChangePassword,
        CancellationToken cancellationToken, string source = OperatorSources.Local)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO operator_users (user_id, user_name, display_name, role, password_hash,
                must_change_password, disabled, security_stamp, created_at_utc, updated_at_utc, source)
            VALUES ($id, $userName, $displayName, $role, $hash, $mustChange, 0, $stamp, $now, $now, $source);
            """;
        var userId = Guid.NewGuid().ToString("N");
        command.Parameters.AddWithValue("$id", userId);
        command.Parameters.AddWithValue("$userName", NormalizeUserName(userName));
        command.Parameters.AddWithValue("$displayName", displayName.Trim());
        command.Parameters.AddWithValue("$role", role);
        // A directory account has no password here: the directory checks it at every sign-in.
        command.Parameters.AddWithValue("$hash",
            source == OperatorSources.Local ? OperatorPasswordHasher.Hash(password) : string.Empty);
        command.Parameters.AddWithValue("$mustChange", mustChangePassword ? 1 : 0);
        command.Parameters.AddWithValue("$stamp", NewStamp());
        command.Parameters.AddWithValue("$now", Format(now));
        command.Parameters.AddWithValue("$source", source);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return (await FindByIdAsync(userId, cancellationToken))!;
    }

    public Task<OperatorUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) =>
        FindAsync("user_id = $value", userId, cancellationToken);

    public Task<OperatorUser?> FindByUserNameAsync(string userName,
        CancellationToken cancellationToken) =>
        FindAsync("user_name = $value", NormalizeUserName(userName), cancellationToken);

    public async Task<IReadOnlyList<OperatorUser>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} ORDER BY disabled, user_name;";
        return await ReadAllAsync(command, cancellationToken);
    }

    public async Task<int> CountEnabledAdminsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM operator_users WHERE role = $role AND disabled = 0;";
        command.Parameters.AddWithValue("$role", OperatorRoles.Admin);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    /// <summary>Changes role, name or enabled state; a role or access change signs the
    /// user out everywhere by rotating the security stamp.</summary>
    public async Task UpdateAsync(string userId, string displayName, string role, bool disabled,
        bool rotateStamp, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE operator_users SET display_name = $displayName, role = $role,
                disabled = $disabled, updated_at_utc = $now,
                security_stamp = CASE WHEN $rotate = 1 THEN $stamp ELSE security_stamp END
            WHERE user_id = $id;
            """;
        command.Parameters.AddWithValue("$id", userId);
        command.Parameters.AddWithValue("$displayName", displayName.Trim());
        command.Parameters.AddWithValue("$role", role);
        command.Parameters.AddWithValue("$disabled", disabled ? 1 : 0);
        command.Parameters.AddWithValue("$rotate", rotateStamp ? 1 : 0);
        command.Parameters.AddWithValue("$stamp", NewStamp());
        command.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Sets a new password and signs out every other session of this user.</summary>
    public async Task<OperatorUser?> SetPasswordAsync(string userId, string password,
        bool mustChangePassword, CancellationToken cancellationToken)
    {
        await using (var connection = await OpenAsync(cancellationToken))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                UPDATE operator_users SET password_hash = $hash,
                    must_change_password = $mustChange, security_stamp = $stamp,
                    updated_at_utc = $now
                WHERE user_id = $id;
                """;
            command.Parameters.AddWithValue("$id", userId);
            command.Parameters.AddWithValue("$hash", OperatorPasswordHasher.Hash(password));
            command.Parameters.AddWithValue("$mustChange", mustChangePassword ? 1 : 0);
            command.Parameters.AddWithValue("$stamp", NewStamp());
            command.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        return await FindByIdAsync(userId, cancellationToken);
    }

    public async Task RecordSignInAsync(string userId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE operator_users SET last_sign_in_at_utc = $now WHERE user_id = $id;";
        command.Parameters.AddWithValue("$id", userId);
        command.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string SelectColumns =
        """
        SELECT user_id, user_name, display_name, role, disabled, must_change_password,
            created_at_utc, last_sign_in_at_utc, password_hash, security_stamp, source
        FROM operator_users
        """;

    private async Task<OperatorUser?> FindAsync(string where, string value,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} WHERE {where};";
        command.Parameters.AddWithValue("$value", value);
        return (await ReadAllAsync(command, cancellationToken)).FirstOrDefault();
    }

    private static async Task<IReadOnlyList<OperatorUser>> ReadAllAsync(SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var users = new List<OperatorUser>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            users.Add(new OperatorUser(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetInt64(4) != 0,
                reader.GetInt64(5) != 0, Parse(reader.GetString(6)),
                reader.IsDBNull(7) ? null : Parse(reader.GetString(7)),
                reader.GetString(10))
            {
                PasswordHash = reader.GetString(8),
                SecurityStamp = reader.GetString(9)
            });
        return users;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
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

    private static string NewStamp() => Guid.NewGuid().ToString("N");

    private static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    [GeneratedRegex("^[a-z0-9._-]{3,32}$")]
    private static partial Regex UserNamePattern();
}
