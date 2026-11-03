using ControlPlane.Api.Chat;
using ControlPlane.Api.Files;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Globalization;

namespace ControlPlane.Api.Software;

public sealed class SoftwareInstallOptions
{
    /// <summary>
    /// Install packages put on the "No approval needed" list the first time the control plane
    /// starts with this feature, so free software that installed without approval before
    /// keeps doing so. After that the list is only what admins set on the Packages page.
    /// </summary>
    public List<string> DefaultNoApproval { get; set; } =
        ["chrome-install", "persian-fonts-install", "foxit-reader-install",
         "adobe-connect-install", "photos-install"];
}

public sealed record NoApprovalEntry(string PackageId, string AddedBy, DateTimeOffset AddedAtUtc);

/// <summary>
/// The admin's "No approval needed" list for install packages. Every other install waits for a
/// helpdesk member's approval. Licensed software and packages that always need approval can
/// never be on it.
/// </summary>
public sealed class SoftwareApprovalStore(IOptions<ChatOptions> chatOptions,
    IOptions<SoftwareInstallOptions> options)
{
    private const string SeededKey = "software-no-approval-seeded";
    private readonly string _databasePath = Path.GetFullPath(chatOptions.Value.DatabasePath);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await using var connection = await OpenAsync(cancellationToken);
        await using (var create = connection.CreateCommand())
        {
            create.CommandText =
                """
                CREATE TABLE IF NOT EXISTS software_no_approval (
                    package_id TEXT PRIMARY KEY,
                    added_by TEXT NOT NULL,
                    added_at_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS software_meta (
                    meta_key TEXT PRIMARY KEY,
                    meta_value TEXT NOT NULL
                );
                """;
            await create.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM software_meta WHERE meta_key = $key;";
            check.Parameters.AddWithValue("$key", SeededKey);
            if (Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken)) > 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }
        }
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        foreach (var id in options.Value.DefaultNoApproval.Where(id => id.Length is > 0 and <= 64).Distinct())
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT OR IGNORE INTO software_no_approval VALUES ($id, 'default', $at);";
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$at", now);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var mark = connection.CreateCommand())
        {
            mark.CommandText = "INSERT INTO software_meta VALUES ($key, $at);";
            mark.Parameters.AddWithValue("$key", SeededKey);
            mark.Parameters.AddWithValue("$at", now);
            await mark.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<NoApprovalEntry>> GetAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT package_id, added_by, added_at_utc FROM software_no_approval ORDER BY package_id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<NoApprovalEntry>();
        while (await reader.ReadAsync(cancellationToken))
            entries.Add(new NoApprovalEntry(reader.GetString(0), reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture)));
        return entries;
    }

    public async Task<bool> ContainsAsync(string packageId, CancellationToken cancellationToken) =>
        (await GetAsync(cancellationToken)).Any(entry => entry.PackageId == packageId);

    public async Task SetAsync(string packageId, bool noApproval, string by,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = noApproval
            ? "INSERT OR REPLACE INTO software_no_approval VALUES ($id, $by, $at);"
            : "DELETE FROM software_no_approval WHERE package_id = $id;";
        command.Parameters.AddWithValue("$id", packageId);
        command.Parameters.AddWithValue("$by", by);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Whether a helpdesk member must approve this run: packages that always need approval
    /// (removals, licensed software, flagged packages), and installs not on the list.
    /// </summary>
    public async Task<bool> NeedsApprovalAsync(RepairPackageArtifact package,
        CancellationToken cancellationToken) =>
        package.RequiresApproval
        || package.IsInstall && !await ContainsAsync(package.Descriptor.PackageId, cancellationToken);

    /// <summary>Why a package cannot go on the list, or null when it can.</summary>
    public static string? CannotSkipApproval(RepairPackageArtifact? package) => package switch
    {
        null => "That package is not published.",
        { IsInstall: false } => "Only install packages can be on the list.",
        { Licensed: true } => "Licensed software always needs approval.",
        { RequiresApproval: true } => "This package always needs approval.",
        _ => null
    };

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
