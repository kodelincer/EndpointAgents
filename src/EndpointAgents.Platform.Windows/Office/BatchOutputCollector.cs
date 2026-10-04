using System.Text;

namespace EndpointAgents.Platform.Windows.Office;

internal enum PromptWaitResult
{
    Matched,
    Ended,
    OutputLimitExceeded,
    ReaderFailed,
    FailureMarker
}

internal readonly record struct PromptMatch(PromptWaitResult Result, int CandidateIndex);

/// <summary>
/// Normalizes streamed BAT output and matches literal prompts against text received
/// after the previous response. A bounded transcript is kept for output markers.
/// </summary>
internal sealed class BatchOutputCollector
{
    private enum AnsiState { None, Escape, Csi, Osc, OscEscape }

    private readonly object _gate = new();
    private readonly StringBuilder _window = new();
    private readonly StringBuilder _transcript = new();
    private readonly int _outputLimitBytes;
    private readonly IReadOnlyList<string> _failureMarkers;
    private readonly int _longestMarker;
    private TaskCompletionSource<bool> _changed = NewSignal();
    private readonly TaskCompletionSource<bool> _outputLimit = NewSignal();
    private readonly TaskCompletionSource<bool> _readerFailure = NewSignal();
    private readonly TaskCompletionSource<bool> _failureMarker = NewSignal();
    private int _remainingReaders = 2;
    private long _capturedBytes;
    private bool _pendingSpace;
    private bool _transcriptPendingSpace;
    private int _scanFrom;
    private AnsiState _ansiState;

    internal BatchOutputCollector(int outputLimitBytes,
        IReadOnlyList<string>? failureMarkers = null)
    {
        _outputLimitBytes = outputLimitBytes;
        _failureMarkers = failureMarkers ?? [];
        _longestMarker = _failureMarkers.Count == 0 ? 0
            : _failureMarkers.Max(marker => marker.Length);
    }

    internal Task OutputLimitTask => _outputLimit.Task;
    internal Task ReaderFailureTask => _readerFailure.Task;
    internal Task FailureMarkerTask => _failureMarker.Task;

    internal void Append(ReadOnlySpan<char> text, int byteCount)
    {
        lock (_gate)
        {
            _capturedBytes += byteCount;
            if (_capturedBytes > _outputLimitBytes)
            {
                _outputLimit.TrySetResult(true);
                Pulse();
                return;
            }

            foreach (var character in text)
                AppendNormalized(character);
            ScanFailureMarkers();
            Pulse();
        }
    }

    internal void CompleteReader(Exception? error = null)
    {
        lock (_gate)
        {
            if (error is not null) _readerFailure.TrySetResult(true);
            _remainingReaders--;
            Pulse();
        }
    }

    internal void BeginNextStep()
    {
        lock (_gate)
        {
            _window.Clear();
            _pendingSpace = false;
        }
    }

    internal bool TranscriptContains(string literal)
    {
        lock (_gate) return IndexOfLiteral(_transcript, literal, 0) >= 0;
    }

    /// <summary>Waits until one expected prompt appears and returns the earliest one.</summary>
    internal async Task<PromptMatch> WaitForAnyAsync(IReadOnlyList<string> expected,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_outputLimit.Task.IsCompleted)
                    return new(PromptWaitResult.OutputLimitExceeded, -1);
                if (_readerFailure.Task.IsCompleted)
                    return new(PromptWaitResult.ReaderFailed, -1);
                if (_failureMarker.Task.IsCompleted)
                    return new(PromptWaitResult.FailureMarker, -1);

                var best = -1;
                var bestPosition = int.MaxValue;
                for (var candidate = 0; candidate < expected.Count; candidate++)
                {
                    var position = IndexOfLiteral(_window, expected[candidate], 0);
                    if (position >= 0 && position < bestPosition)
                    {
                        best = candidate;
                        bestPosition = position;
                    }
                }
                if (best >= 0) return new(PromptWaitResult.Matched, best);
                if (_remainingReaders == 0) return new(PromptWaitResult.Ended, -1);
                changed = _changed.Task;
            }
            await changed.WaitAsync(cancellationToken);
        }
    }

    private void ScanFailureMarkers()
    {
        if (_failureMarkers.Count == 0 || _failureMarker.Task.IsCompleted) return;
        var start = Math.Max(0, Math.Min(_scanFrom, _transcript.Length)
            - (_longestMarker - 1));
        foreach (var marker in _failureMarkers)
        {
            if (IndexOfLiteral(_transcript, marker, start) < 0) continue;
            _failureMarker.TrySetResult(true);
            break;
        }
        _scanFrom = _transcript.Length;
    }

    private void AppendNormalized(char character)
    {
        switch (_ansiState)
        {
            case AnsiState.Escape:
                _ansiState = character switch
                {
                    '[' => AnsiState.Csi,
                    ']' => AnsiState.Osc,
                    _ => AnsiState.None
                };
                return;
            case AnsiState.Csi:
                if (character is >= '@' and <= '~') _ansiState = AnsiState.None;
                return;
            case AnsiState.Osc:
                if (character == '\a') _ansiState = AnsiState.None;
                else if (character == '\u001b') _ansiState = AnsiState.OscEscape;
                return;
            case AnsiState.OscEscape:
                _ansiState = character == '\\' ? AnsiState.None : AnsiState.Osc;
                return;
        }

        if (character == '\u001b')
        {
            _ansiState = AnsiState.Escape;
            return;
        }
        if (character == '\b')
        {
            if (_window.Length > 0) _window.Length--;
            if (_transcript.Length > 0) _transcript.Length--;
            return;
        }
        if (char.IsWhiteSpace(character))
        {
            _pendingSpace = _window.Length > 0;
            _transcriptPendingSpace = _transcript.Length > 0;
            return;
        }
        if (char.IsControl(character)) return;
        if (_pendingSpace)
        {
            _window.Append(' ');
            _pendingSpace = false;
        }
        if (_transcriptPendingSpace)
        {
            _transcript.Append(' ');
            _transcriptPendingSpace = false;
        }
        _window.Append(character);
        _transcript.Append(character);
    }

    private static int IndexOfLiteral(StringBuilder haystack, string needle, int start)
    {
        for (var index = start; index <= haystack.Length - needle.Length; index++)
        {
            var matches = true;
            for (var offset = 0; offset < needle.Length; offset++)
            {
                if (char.ToUpperInvariant(haystack[index + offset])
                    == char.ToUpperInvariant(needle[offset]))
                    continue;
                matches = false;
                break;
            }
            if (matches) return index;
        }
        return -1;
    }

    private void Pulse()
    {
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> NewSignal() => new(
        TaskCreationOptions.RunContinuationsAsynchronously);
}
