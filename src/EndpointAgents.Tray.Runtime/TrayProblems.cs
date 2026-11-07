using EndpointAgents.Shared.Problems;
using System.Reflection;

namespace EndpointAgents.Tray;

/// <summary>
/// Crashes of the Tray. They are saved in the user's own folder first, because after a crash the
/// service may not be reachable, and handed to the service when the Tray next runs.
/// </summary>
public static class TrayProblems
{
    private static readonly Lazy<ProblemOutbox> Outbox = new(() => new ProblemOutbox(ProblemPaths.TrayOutbox));
    private static readonly string? Version =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3);

    public static void Report(Exception exception)
    {
        try
        {
            Outbox.Value.Add(ProblemText.FromException(ProblemKinds.Crash, "Tray", exception, Version,
                DateTimeOffset.UtcNow), saveNow: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    /// <summary>Hands what is saved to the service. What it cannot hand over stays for next time.</summary>
    public static async Task SendSavedAsync(ServiceIpcClient service, CancellationToken cancellationToken)
    {
        try
        {
            while (Outbox.Value.Count > 0)
            {
                var batch = Outbox.Value.NextBatch(maxCharacters: 16 * 1024);
                if (batch.Count == 0) return;
                await service.ReportProblemsAsync(batch.Take(10).ToArray(), cancellationToken);
                Outbox.Value.MarkSent(batch.Take(10).ToArray());
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
}
