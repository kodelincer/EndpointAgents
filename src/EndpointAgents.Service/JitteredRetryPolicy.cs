using Microsoft.AspNetCore.SignalR.Client;

namespace EndpointAgents.Service;

/// <summary>Provides bounded SignalR reconnect delays with random jitter.</summary>
/// <remarks>
/// Jitter reduces synchronized reconnects after a shared control-plane outage.
/// Returning <see langword="null"/> stops automatic reconnect so the worker's
/// outer loop can supervise a later full connection attempt.
/// </remarks>
internal sealed class JitteredRetryPolicy : IRetryPolicy
{
    private static readonly TimeSpan[] BaseDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30)
    ];

    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        // SignalR calls this once per reconnect attempt; the retry count selects
        // the progressively longer base delay above.
        if (retryContext.PreviousRetryCount >= BaseDelays.Length)
        {
            return null;
        }

        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(250, 2001));
        return BaseDelays[retryContext.PreviousRetryCount] + jitter;
    }
}
