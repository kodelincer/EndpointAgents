using Microsoft.AspNetCore.SignalR.Client;

namespace EndpointAgents.Service;

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
        if (retryContext.PreviousRetryCount >= BaseDelays.Length)
        {
            return null;
        }

        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(250, 2001));
        return BaseDelays[retryContext.PreviousRetryCount] + jitter;
    }
}
