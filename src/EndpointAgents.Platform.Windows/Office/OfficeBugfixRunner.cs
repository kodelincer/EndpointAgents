using EndpointAgents.Shared.Office;
using EndpointAgents.Shared.Security;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace EndpointAgents.Platform.Windows.Office;

/// <summary>Runs one hash-pinned Office BAT from protected service staging.</summary>
[SupportedOSPlatform("windows")]
public sealed class OfficeBugfixRunner
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(8);
    internal const int OutputLimitBytes = 1024 * 1024;

    public async Task<OfficeBugfixRunResult> RunAsync(string path,
        OfficeBugfixArtifactDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(
                WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException(
                "The EndpointAgents service does not have an administrative token.");
        if (!BatchPlaybookPolicy.TryNormalize(descriptor.Playbook,
                out var playbook, out _)
            || !IsSha256(descriptor.PlaybookSha256)
            || !BatchPlaybookPolicy.ComputeSha256(playbook!).Equals(
                descriptor.PlaybookSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Office repair playbook is not approved.");
        if (!await IsApprovedAsync(path, descriptor.ArtifactId,
                descriptor.FileName, descriptor.Sha256, cancellationToken))
            throw new InvalidDataException("The staged Office repair is not approved.");

        return await RunProcessAsync(CreateStartInfo(path), playbook!,
            cancellationToken);
    }

    internal static ProcessStartInfo CreateStartInfo(string path)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            WorkingDirectory = Path.GetDirectoryName(path)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/s");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(path);
        return startInfo;
    }

    internal static async Task<OfficeBugfixRunResult> RunProcessAsync(
        ProcessStartInfo startInfo, BatchPlaybook playbook,
        CancellationToken cancellationToken, TimeSpan? overallTimeout = null,
        int outputLimitBytes = OutputLimitBytes)
    {
        if (!BatchPlaybookPolicy.TryNormalize(playbook, out var normalized,
                out var validationError))
            throw new ArgumentException(validationError, nameof(playbook));
        if (outputLimitBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(outputLimitBytes));
        if (cancellationToken.IsCancellationRequested)
            return Result(null, null, OfficeBugfixRunStates.Cancelled, 0, 0);

        using var process = new Process { StartInfo = startInfo };
        var collector = new BatchOutputCollector(outputLimitBytes);
        Task[] pumps = [];
        var completedSteps = 0;
        int? processId = null;

        using var overall = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        overall.CancelAfter(overallTimeout ?? Timeout);
        try
        {
            if (!process.Start())
                return Result(null, null, OfficeBugfixRunStates.ProcessFailed,
                    completedSteps, 0);
            processId = process.Id;
            pumps =
            [
                PumpAsync(process.StandardOutput, collector),
                PumpAsync(process.StandardError, collector)
            ];

            for (var index = 0; index < normalized!.Steps.Count; index++)
            {
                var step = normalized.Steps[index];
                using var stepTimeout = CancellationTokenSource
                    .CreateLinkedTokenSource(overall.Token);
                stepTimeout.CancelAfter(TimeSpan.FromSeconds(step.TimeoutSeconds));

                PromptWaitResult prompt;
                try
                {
                    prompt = await collector.WaitForPromptAsync(step.Expect,
                        stepTimeout.Token);
                }
                catch (OperationCanceledException)
                {
                    var state = cancellationToken.IsCancellationRequested
                        ? OfficeBugfixRunStates.Cancelled
                        : OfficeBugfixRunStates.PromptTimedOut;
                    await StopAsync(process, pumps);
                    return Result(processId, TryGetExitCode(process), state,
                        completedSteps, index);
                }

                if (prompt == PromptWaitResult.OutputLimitExceeded)
                {
                    await StopAsync(process, pumps);
                    return Result(processId, TryGetExitCode(process),
                        OfficeBugfixRunStates.OutputLimitExceeded, completedSteps,
                        index);
                }
                if (prompt == PromptWaitResult.ReaderFailed)
                {
                    await StopAsync(process, pumps);
                    return Result(processId, TryGetExitCode(process),
                        OfficeBugfixRunStates.ProcessFailed, completedSteps, index);
                }
                if (prompt == PromptWaitResult.Ended)
                {
                    await StopAsync(process, pumps);
                    return Result(processId, TryGetExitCode(process),
                        OfficeBugfixRunStates.ExitedBeforePlaybookCompleted,
                        completedSteps, index);
                }

                // Clear all previously received text before releasing the approved
                // response so a repeated menu cannot satisfy the next step.
                collector.BeginNextStep();
                try
                {
                    await process.StandardInput.WriteAsync(step.Send.AsMemory(),
                        overall.Token);
                    if (step.Submit == BatchPlaybookSubmitKinds.Line)
                        await process.StandardInput.WriteLineAsync(
                            ReadOnlyMemory<char>.Empty, overall.Token);
                    await process.StandardInput.FlushAsync(overall.Token);
                    completedSteps++;
                }
                catch (OperationCanceledException)
                {
                    var state = cancellationToken.IsCancellationRequested
                        ? OfficeBugfixRunStates.Cancelled
                        : OfficeBugfixRunStates.PromptTimedOut;
                    await StopAsync(process, pumps);
                    return Result(processId, TryGetExitCode(process), state,
                        completedSteps, index);
                }
                catch (Exception ex) when (ex is IOException
                    or InvalidOperationException or ObjectDisposedException)
                {
                    await StopAsync(process, pumps);
                    return Result(processId, TryGetExitCode(process),
                        OfficeBugfixRunStates.ExitedBeforePlaybookCompleted,
                        completedSteps, index);
                }
            }

            process.StandardInput.Close();
            var exitTask = process.WaitForExitAsync(overall.Token);
            var winner = await Task.WhenAny(exitTask, collector.OutputLimitTask,
                collector.ReaderFailureTask);
            if (winner == collector.OutputLimitTask)
            {
                await StopAsync(process, pumps);
                return Result(processId, TryGetExitCode(process),
                    OfficeBugfixRunStates.OutputLimitExceeded, completedSteps, null);
            }
            if (winner == collector.ReaderFailureTask)
            {
                await StopAsync(process, pumps);
                return Result(processId, TryGetExitCode(process),
                    OfficeBugfixRunStates.ProcessFailed, completedSteps, null);
            }

            await exitTask;
            var drainTask = Task.WhenAll(pumps);
            winner = await Task.WhenAny(drainTask, collector.OutputLimitTask,
                collector.ReaderFailureTask);
            if (winner == collector.OutputLimitTask)
                return Result(processId, process.ExitCode,
                    OfficeBugfixRunStates.OutputLimitExceeded, completedSteps, null);
            if (winner == collector.ReaderFailureTask)
                return Result(processId, process.ExitCode,
                    OfficeBugfixRunStates.ProcessFailed, completedSteps, null);
            await drainTask;

            return Result(processId, process.ExitCode,
                process.ExitCode == 0 ? OfficeBugfixRunStates.Completed
                    : OfficeBugfixRunStates.ProcessFailed,
                completedSteps, null);
        }
        catch (OperationCanceledException)
        {
            await StopAsync(process, pumps);
            return Result(processId, TryGetExitCode(process),
                cancellationToken.IsCancellationRequested
                    ? OfficeBugfixRunStates.Cancelled
                    : OfficeBugfixRunStates.PromptTimedOut,
                completedSteps,
                completedSteps < normalized!.Steps.Count ? completedSteps : null);
        }
        catch
        {
            await StopAsync(process, pumps);
            return Result(processId, TryGetExitCode(process),
                OfficeBugfixRunStates.ProcessFailed, completedSteps,
                completedSteps < normalized!.Steps.Count ? completedSteps : null);
        }
    }

    internal static async Task<bool> IsApprovedAsync(string path, string artifactId,
        string fileName, string expectedSha256, CancellationToken cancellationToken)
    {
        if (artifactId.Length is < 1 or > 64
            || artifactId.Any(character =>
                !(character is >= 'a' and <= 'z' || char.IsAsciiDigit(character)
                  || character is '-' or '_' or '.'))
            || !(artifactId[^1] is >= 'a' and <= 'z'
                || char.IsAsciiDigit(artifactId[^1]))
            || !fileName.Equals($"{artifactId}.bat", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!IsSha256(expectedSha256)) return false;
        var root = Path.GetFullPath(OfficeRemediationPaths.StagingRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var expected = Path.GetFullPath(Path.Combine(root, artifactId,
            expectedSha256.ToUpperInvariant(), fileName));
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        if (!fullPath.Equals(expected, StringComparison.OrdinalIgnoreCase)
            || !ProtectedWindowsPath.IsDirectorySecure(
                OfficeRemediationPaths.StagingRoot, requireProtectedAcl: true)
            || !ProtectedWindowsPath.IsDirectorySecure(directory,
                requireProtectedAcl: false)
            || !ProtectedWindowsPath.IsFileSecure(fullPath))
            return false;

        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous
            | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken));
        return actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task PumpAsync(StreamReader reader,
        BatchOutputCollector collector)
    {
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory());
                if (count == 0) break;
                collector.Append(buffer.AsSpan(0, count),
                    reader.CurrentEncoding.GetByteCount(buffer.AsSpan(0, count)));
            }
            collector.CompleteReader();
        }
        catch (Exception ex)
        {
            collector.CompleteReader(ex);
        }
    }

    private static async Task StopAsync(Process process, IReadOnlyList<Task> pumps)
    {
        try { process.StandardInput.Close(); } catch { }
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { }
        try
        {
            if (!process.HasExited)
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch { }
        if (pumps.Count > 0)
        {
            try
            {
                await Task.WhenAll(pumps).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch { }
        }
    }

    private static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(Uri.IsHexDigit);

    private static int? TryGetExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : null; }
        catch { return null; }
    }

    private static OfficeBugfixRunResult Result(int? processId, int? exitCode,
        string state, int completedSteps, int? failedStepIndex) =>
        new(processId, exitCode, state, completedSteps, failedStepIndex);
}

public static class OfficeBugfixRunStates
{
    public const string Completed = "Completed";
    public const string PromptTimedOut = "PromptTimedOut";
    public const string ExitedBeforePlaybookCompleted =
        "ExitedBeforePlaybookCompleted";
    public const string OutputLimitExceeded = "OutputLimitExceeded";
    public const string Cancelled = "Cancelled";
    public const string ProcessFailed = "ProcessFailed";
}

public sealed record OfficeBugfixRunResult(
    int? ProcessId,
    int? ExitCode,
    string State,
    int CompletedStepCount,
    int? FailedStepIndex);

internal enum PromptWaitResult
{
    Matched,
    Ended,
    OutputLimitExceeded,
    ReaderFailed
}

internal sealed class BatchOutputCollector
{
    private enum AnsiState { None, Escape, Csi, Osc, OscEscape }

    private readonly object _gate = new();
    private readonly StringBuilder _window = new();
    private readonly int _outputLimitBytes;
    private TaskCompletionSource<bool> _changed = NewSignal();
    private readonly TaskCompletionSource<bool> _outputLimit = NewSignal();
    private readonly TaskCompletionSource<bool> _readerFailure = NewSignal();
    private int _remainingReaders = 2;
    private long _capturedBytes;
    private bool _pendingSpace;
    private AnsiState _ansiState;

    internal BatchOutputCollector(int outputLimitBytes) =>
        _outputLimitBytes = outputLimitBytes;

    internal Task OutputLimitTask => _outputLimit.Task;
    internal Task ReaderFailureTask => _readerFailure.Task;

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

    internal async Task<PromptWaitResult> WaitForPromptAsync(string expected,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_outputLimit.Task.IsCompleted)
                    return PromptWaitResult.OutputLimitExceeded;
                if (_readerFailure.Task.IsCompleted)
                    return PromptWaitResult.ReaderFailed;
                if (ContainsLiteral(_window, expected))
                    return PromptWaitResult.Matched;
                if (_remainingReaders == 0) return PromptWaitResult.Ended;
                changed = _changed.Task;
            }
            await changed.WaitAsync(cancellationToken);
        }
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
            return;
        }
        if (char.IsWhiteSpace(character))
        {
            _pendingSpace = _window.Length > 0;
            return;
        }
        if (char.IsControl(character)) return;
        if (_pendingSpace)
        {
            _window.Append(' ');
            _pendingSpace = false;
        }
        _window.Append(character);
    }

    private static bool ContainsLiteral(StringBuilder haystack, string needle)
    {
        if (needle.Length > haystack.Length) return false;
        for (var start = 0; start <= haystack.Length - needle.Length; start++)
        {
            var matches = true;
            for (var offset = 0; offset < needle.Length; offset++)
            {
                if (char.ToUpperInvariant(haystack[start + offset])
                    == char.ToUpperInvariant(needle[offset]))
                    continue;
                matches = false;
                break;
            }
            if (matches) return true;
        }
        return false;
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
