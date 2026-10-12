using EndpointAgents.Shared.LocalIpc;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace EndpointAgents.Service;

/// <summary>
/// One attached Tray: the service writes catalogued <see cref="TrayIpcRequest"/> lines and matches
/// the Tray's <see cref="TrayIpcResponse"/> lines back by id.
/// </summary>
public sealed class TrayChannel : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TrayIpcResponse>> _pending = new();
    private readonly CancellationTokenSource _closed = new();

    public TrayChannel(Stream stream, int sessionId, string userSid, string userName)
    {
        _stream = stream;
        SessionId = sessionId;
        UserSid = userSid;
        UserName = userName;
        _reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        _writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    }

    public int SessionId { get; }
    public string UserSid { get; }
    public string UserName { get; }
    public bool IsClosed => _closed.IsCancellationRequested;

    /// <summary>Reads responses until the Tray disconnects; then fails every pending request.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        try
        {
            while (true)
            {
                var line = await LineFraming.ReadLineAsync(_reader,
                    TrayIpcProtocol.MaxMessageCharacters, linked.Token);
                if (line is null) break;
                TrayIpcResponse? response;
                try { response = JsonSerializer.Deserialize<TrayIpcResponse>(line); }
                catch (JsonException) { continue; }
                if (response is not null && _pending.TryRemove(response.Id, out var waiter))
                    waiter.TrySetResult(response);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (InvalidDataException) { }
        finally
        {
            Close();
        }
    }

    public async Task<TrayIpcResponse> SendAsync(TrayIpcRequest request, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var waiter = new TaskCompletionSource<TrayIpcResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (IsClosed || !_pending.TryAdd(request.Id, waiter))
            return Unavailable(request.Id, "The Tray channel is closed or the request id is in use.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        deadline.CancelAfter(timeout);
        try
        {
            await _writeGate.WaitAsync(deadline.Token);
            try
            {
                await _writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), deadline.Token);
            }
            finally
            {
                _writeGate.Release();
            }
            return await waiter.Task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable(request.Id, IsClosed
                ? "The active user's Tray disconnected."
                : "The active user's Tray did not respond before the timeout.");
        }
        catch (IOException ex)
        {
            Close();
            return Unavailable(request.Id, ex.Message);
        }
        finally
        {
            _pending.TryRemove(request.Id, out _);
        }
    }

    public void Close()
    {
        if (_closed.IsCancellationRequested) return;
        _closed.Cancel();
        foreach (var (id, waiter) in _pending)
            waiter.TrySetResult(Unavailable(id, "The active user's Tray disconnected."));
        _pending.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        _reader.Dispose();
        await _writer.DisposeAsync();
        await _stream.DisposeAsync();
    }

    private static TrayIpcResponse Unavailable(string id, string message) =>
        new(id, false, "TrayUnavailable", Message: message);
}

/// <summary>The attached Tray of each interactive Windows session; the newest attach wins.</summary>
public sealed class TrayChannelRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<int, TrayChannel> _channels = [];
    private readonly Dictionary<int, TaskCompletionSource<TrayChannel>> _waiters = [];

    public void Register(TrayChannel channel)
    {
        TrayChannel? replaced;
        TaskCompletionSource<TrayChannel>? waiter;
        lock (_gate)
        {
            _channels.TryGetValue(channel.SessionId, out replaced);
            _channels[channel.SessionId] = channel;
            if (_waiters.Remove(channel.SessionId, out waiter)) { }
        }
        replaced?.Close();
        waiter?.TrySetResult(channel);
    }

    public void Unregister(TrayChannel channel)
    {
        lock (_gate)
        {
            if (_channels.TryGetValue(channel.SessionId, out var current) && current == channel)
                _channels.Remove(channel.SessionId);
        }
    }

    public TrayChannel? Find(int sessionId)
    {
        lock (_gate)
            return _channels.TryGetValue(sessionId, out var channel) && !channel.IsClosed
                ? channel : null;
    }

    /// <summary>Waits briefly for a Tray that is still starting, e.g. right after logon.</summary>
    public async Task<TrayChannel?> WaitAsync(int sessionId, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Task<TrayChannel> pending;
        lock (_gate)
        {
            if (_channels.TryGetValue(sessionId, out var channel) && !channel.IsClosed)
                return channel;
            if (!_waiters.TryGetValue(sessionId, out var waiter))
                _waiters[sessionId] = waiter = new TaskCompletionSource<TrayChannel>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            pending = waiter.Task;
        }
        try
        {
            return await pending.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    public IReadOnlyList<int> AttachedSessions()
    {
        lock (_gate)
            return _channels.Where(item => !item.Value.IsClosed).Select(item => item.Key).ToArray();
    }
}
