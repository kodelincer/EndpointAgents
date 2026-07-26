using Microsoft.Extensions.Options;

namespace ControlPlane.Api.Chat;

public sealed class ChatRetentionWorker(
    ChatStore store,
    IOptions<ChatOptions> options,
    ILogger<ChatRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-options.Value.RetentionDays);
                var affected = await store.PurgeExpiredDataAsync(cutoff, stoppingToken);
                logger.LogInformation(
                    "Chat retention cleanup completed with {AffectedRows} affected rows.",
                    affected);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Chat retention cleanup failed.");
            }

            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }
}
