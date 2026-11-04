using System.Text;
using System.Text.Json;

namespace EndpointAgents.Shared.Problems;

/// <summary>
/// Reports waiting to be sent, kept in one small file so they survive a restart or a network
/// outage. The same problem is counted, not added again; when the file is full the oldest report
/// goes first; at most <see cref="ProblemLimits.MaxReportsPerHour"/> leave per hour. It never
/// throws: reporting a problem must not cause another one.
/// </summary>
public sealed class ProblemOutbox
{
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private readonly string _path;
    private readonly int _maxBytes;
    private readonly int _maxPerHour;
    private readonly Func<DateTimeOffset> _clock;
    private readonly List<ProblemReport> _reports;
    private readonly Queue<DateTimeOffset> _sent = new();
    private DateTimeOffset _lastSave = DateTimeOffset.MinValue;
    private bool _dirty;

    public ProblemOutbox(string path, int maxBytes = ProblemLimits.MaxOutboxBytes,
        int maxPerHour = ProblemLimits.MaxReportsPerHour, Func<DateTimeOffset>? clock = null)
    {
        _path = path;
        _maxBytes = maxBytes;
        _maxPerHour = maxPerHour;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _reports = Load(path);
    }

    public int Count { get { lock (_gate) return _reports.Count; } }

    /// <summary>Adds a report. <paramref name="saveNow"/> is for a crash, when nothing runs afterwards.</summary>
    public void Add(ProblemReport report, bool saveNow = false)
    {
        lock (_gate)
        {
            var index = _reports.FindIndex(item => item.Fingerprint == report.Fingerprint
                && item.AgentVersion == report.AgentVersion);
            if (index >= 0)
            {
                var known = _reports[index];
                _reports[index] = known with
                {
                    Count = known.Count + report.Count,
                    LastSeenUtc = report.LastSeenUtc > known.LastSeenUtc ? report.LastSeenUtc : known.LastSeenUtc
                };
            }
            else _reports.Add(report);
            _dirty = true;
            Trim();
            if (saveNow || _clock() - _lastSave >= SaveInterval) Save();
        }
    }

    /// <summary>
    /// The next reports to send: under the hourly limit and small enough for one hub call.
    /// They stay in the outbox until <see cref="MarkSent"/>.
    /// </summary>
    public IReadOnlyList<ProblemReport> NextBatch(int maxCharacters = ProblemLimits.MaxBatchCharacters)
    {
        lock (_gate)
        {
            ForgetOldSends();
            var allowed = _maxPerHour - _sent.Count;
            var batch = new List<ProblemReport>();
            var characters = 0;
            foreach (var report in _reports)
            {
                if (batch.Count >= allowed) break;
                var size = Size(report);
                if (batch.Count > 0 && characters + size > maxCharacters) break;
                batch.Add(report);
                characters += size;
            }
            return batch;
        }
    }

    /// <summary>
    /// The control plane took these. Whatever was counted since they were read stays waiting,
    /// with only the new count.
    /// </summary>
    public void MarkSent(IReadOnlyList<ProblemReport> sent)
    {
        lock (_gate)
        {
            foreach (var report in sent)
            {
                _sent.Enqueue(_clock());
                var index = _reports.FindIndex(item => item.Fingerprint == report.Fingerprint
                    && item.AgentVersion == report.AgentVersion);
                if (index < 0) continue;
                var remaining = _reports[index].Count - report.Count;
                if (remaining > 0)
                    _reports[index] = _reports[index] with
                    {
                        Count = remaining, FirstSeenUtc = report.LastSeenUtc
                    };
                else _reports.RemoveAt(index);
            }
            _dirty = true;
            Save();
        }
    }

    /// <summary>Writes what is waiting; called regularly so a burst of errors is written once.</summary>
    public void Flush()
    {
        lock (_gate) if (_dirty) Save();
    }

    private void ForgetOldSends()
    {
        var cutoff = _clock() - TimeSpan.FromHours(1);
        while (_sent.Count > 0 && _sent.Peek() <= cutoff) _sent.Dequeue();
    }

    private void Trim()
    {
        // The list part of the file is two bytes plus a comma per report.
        var total = 2 + _reports.Sum(report => Size(report) + 1);
        while (total > _maxBytes && _reports.Count > 0)
        {
            var oldest = 0;
            for (var index = 1; index < _reports.Count; index++)
                if (_reports[index].LastSeenUtc < _reports[oldest].LastSeenUtc) oldest = index;
            total -= Size(_reports[oldest]) + 1;
            _reports.RemoveAt(oldest);
        }
    }

    private static int Size(ProblemReport report) =>
        Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(report));

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_reports),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, _path, overwrite: true);
            _dirty = false;
            _lastSave = _clock();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static List<ProblemReport> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<ProblemReport>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}
