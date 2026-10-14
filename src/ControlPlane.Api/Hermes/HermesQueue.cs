using ControlPlane.Api.Chat;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace ControlPlane.Api.Hermes;

public sealed record HermesQueueSettings(int MaxParallelRuns, int MaxQueueLength,
    int MaxWaitMinutes)
{
    public const int MaxParallelLimit = 32;
    public const int MaxQueueLimit = 1000;
    public const int MaxWaitLimit = 240;

    public string? Validate() =>
        MaxParallelRuns is < 1 or > MaxParallelLimit
            ? $"Max parallel requests must be between 1 and {MaxParallelLimit}."
            : MaxQueueLength is < 1 or > MaxQueueLimit
                ? $"Max queue length must be between 1 and {MaxQueueLimit}."
                : MaxWaitMinutes is < 1 or > MaxWaitLimit
                    ? $"Max waiting time must be between 1 and {MaxWaitLimit} minutes."
                    : null;
}

public sealed record HermesQueueStatus(HermesQueueSettings Settings, int Running,
    int Waiting, DateTimeOffset? OldestWaitingSinceUtc);

// Admin-tunable limits of the control plane's queue in front of Hermes. Hermes never
// sees them: the worker leases at most MaxParallelRuns, and the hub refuses new
// requests when MaxQueueLength runs already wait.
public sealed class HermesQueue(ChatStore store, IOptions<HermesOptions> options)
{
    private const string SettingKey = "hermes.queue";
    private volatile HermesQueueSettings _current =
        new(options.Value.MaxConcurrentRuns, 50, 10);

    public HermesQueueSettings Current => _current;

    public ChatQueueLimits Limits => new(_current.MaxQueueLength);

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var saved = await store.GetSettingAsync(SettingKey, cancellationToken);
        if (saved is null) return;
        var settings = JsonSerializer.Deserialize<HermesQueueSettings>(saved);
        if (settings is not null && settings.Validate() is null) _current = settings;
    }

    public async Task<string?> SaveAsync(HermesQueueSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.Validate() is { } error) return error;
        await store.SetSettingAsync(SettingKey, JsonSerializer.Serialize(settings),
            cancellationToken);
        _current = settings;
        return null;
    }

    public async Task<HermesQueueStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var snapshot = await store.GetQueueSnapshotAsync(cancellationToken);
        return new HermesQueueStatus(_current, snapshot.Running, snapshot.Waiting,
            snapshot.OldestWaitingSinceUtc);
    }
}
