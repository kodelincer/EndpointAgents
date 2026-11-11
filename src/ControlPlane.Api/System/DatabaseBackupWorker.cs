using ControlPlane.Api.Chat;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using System.Globalization;

namespace ControlPlane.Api.System;

/// <summary>Settings for the daily copy of the control-plane database (section <c>Backup</c>).</summary>
public sealed class DatabaseBackupOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Folder for the copies. Empty means a <c>Backups</c> folder next to the database.</summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>How many daily copies to keep.</summary>
    public int KeepCopies { get; set; } = 7;
}

/// <summary>
/// Keeps one consistent copy of the control-plane database per day. The database holds every
/// enrolled PC, the operator accounts, the settings and the conversations; without a copy a
/// damaged file means enrolling every PC again. Plain SQLite, no AI.
/// </summary>
public sealed class DatabaseBackupWorker(
    IOptions<ChatOptions> chat,
    IOptions<DatabaseBackupOptions> options,
    TimeProvider clock,
    ILogger<DatabaseBackupWorker> logger) : BackgroundService
{
    public const string FilePrefix = "control-plane-";
    private readonly DatabaseBackupOptions _options = options.Value;

    public string BackupDirectory => string.IsNullOrWhiteSpace(_options.Directory)
        ? Path.Combine(Path.GetDirectoryName(chat.Value.DatabasePath)!, "Backups")
        : Path.GetFullPath(_options.Directory);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Daily database backup is turned off (Backup:Enabled).");
            return;
        }
        try
        {
            // Let the start-up work finish first.
            await Task.Delay(TimeSpan.FromMinutes(2), clock, stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
            do
            {
                try
                {
                    var created = await BackUpIfDueAsync(stoppingToken);
                    if (created is not null)
                        logger.LogInformation("Database backup written to {Path}.", created);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex) { logger.LogError(ex, "The daily database backup failed."); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Writes today's copy unless it exists, then removes the oldest ones. Returns the new file.</summary>
    public async Task<string?> BackUpIfDueAsync(CancellationToken cancellationToken)
    {
        var source = chat.Value.DatabasePath;
        if (!File.Exists(source)) return null;
        var directory = BackupDirectory;
        global::System.IO.Directory.CreateDirectory(directory);
        var day = clock.GetLocalNow().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var target = Path.Combine(directory, $"{FilePrefix}{day}.db");
        if (File.Exists(target)) return null;

        var partial = target + ".part";
        File.Delete(partial);
        try
        {
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = source,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString()))
            {
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                // VACUUM INTO writes a complete, consistent copy while the API keeps working.
                command.CommandText = "PRAGMA busy_timeout=30000; VACUUM INTO $target;";
                command.Parameters.AddWithValue("$target", partial);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            File.Move(partial, target, overwrite: false);
        }
        finally
        {
            try { File.Delete(partial); } catch (IOException) { }
        }
        RemoveOldCopies(directory);
        return target;
    }

    private void RemoveOldCopies(string directory)
    {
        var keep = Math.Clamp(_options.KeepCopies, 1, 365);
        // The date in the name sorts the same way as the date itself.
        foreach (var old in global::System.IO.Directory.GetFiles(directory, FilePrefix + "*.db")
                     .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(keep))
        {
            try { File.Delete(old); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not remove the old database backup {Path}.", old);
            }
        }
    }
}
