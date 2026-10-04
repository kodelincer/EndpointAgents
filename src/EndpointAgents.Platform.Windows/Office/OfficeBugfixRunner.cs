using EndpointAgents.Shared.Office;
using EndpointAgents.Shared.Security;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace EndpointAgents.Platform.Windows.Office;

/// <summary>Runs one hash-pinned Office BAT from protected service staging.</summary>
[SupportedOSPlatform("windows")]
public sealed class OfficeBugfixRunner(IOptions<OfficeRemediationOptions> options)
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromMinutes(8);
    internal const int OutputLimitBytes = 1024 * 1024;
    internal const int OutputTailCharacters = 4096;
    internal static readonly TimeSpan StartupAllowance = TimeSpan.FromSeconds(30);
    private readonly OfficeRemediationOptions _options = options.Value;

    public async Task<OfficeBugfixRunResult> RunAsync(string path,
        OfficeBugfixArtifactDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        if (_options.RequireAdministrator)
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(
                    WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException(
                    "The EndpointAgents service does not have an administrative token.");
        }
        if (!BatchPlaybookPolicy.TryNormalize(descriptor.Playbook,
                out var playbook, out _)
            || !IsSha256(descriptor.PlaybookSha256)
            || !BatchPlaybookPolicy.ComputeSha256(playbook!).Equals(
                descriptor.PlaybookSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Office repair playbook is not approved.");
        if (!BatchSupportFilePolicy.IsValidBundle(descriptor.Sha256,
                descriptor.SupportFiles, descriptor.BundleSha256,
                $"/api/files/office/bugfix/{descriptor.ArtifactId}/file"))
            throw new InvalidDataException("The Office repair bundle is not approved.");
        if (!await IsApprovedAsync(path, descriptor.ArtifactId,
                descriptor.FileName, descriptor.Sha256, cancellationToken,
                descriptor.BundleSha256, _options.StagingRoot,
                _options.RequireProtectedAcl)
            || !await BatchSupportFileDownloader.AreApprovedAsync(path,
                descriptor.SupportFiles, _options.RequireProtectedAcl,
                cancellationToken))
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
            RedirectStandardError = true,
            // Never inherit a console encoding that writes a byte-order mark into
            // the first answer. Responses are validated, single-line text.
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
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

        var collector = new BatchOutputCollector(outputLimitBytes,
            normalized!.FailureMarkers);
        var result = await RunProcessCoreAsync(startInfo, normalized, collector,
            cancellationToken, overallTimeout);
        // Unsuccessful runs keep a short, redacted end of the output for diagnosis.
        return result.State == OfficeBugfixRunStates.Completed ? result
            : result with
            {
                OutputTail = SensitiveTextRedactor.Redact(
                    collector.GetTranscriptTail(OutputTailCharacters))
            };
    }

    private static async Task<OfficeBugfixRunResult> RunProcessCoreAsync(
        ProcessStartInfo startInfo, BatchPlaybook normalized,
        BatchOutputCollector collector, CancellationToken cancellationToken,
        TimeSpan? overallTimeout)
    {
        using var process = new Process { StartInfo = startInfo };
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

            var steps = normalized!.Steps;
            var index = 0;
            var repeats = 0;
            while (index < steps.Count)
            {
                var candidates = GetCandidates(steps, index, repeats);
                using var stepTimeout = CancellationTokenSource
                    .CreateLinkedTokenSource(overall.Token);
                // Before the first response the wait also covers cmd start-up and
                // antivirus scanning of the freshly staged BAT.
                stepTimeout.CancelAfter(TimeSpan.FromSeconds(candidates.Max(
                    candidate => steps[candidate.StepIndex].TimeoutSeconds))
                    + (completedSteps == 0 ? StartupAllowance : TimeSpan.Zero));

                PromptMatch prompt;
                try
                {
                    prompt = await collector.WaitForAnyAsync(
                        candidates.Select(candidate => candidate.Choice.Expect)
                            .ToArray(), stepTimeout.Token);
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

                var stopState = prompt.Result switch
                {
                    PromptWaitResult.OutputLimitExceeded =>
                        OfficeBugfixRunStates.OutputLimitExceeded,
                    PromptWaitResult.ReaderFailed => OfficeBugfixRunStates.ProcessFailed,
                    PromptWaitResult.FailureMarker =>
                        OfficeBugfixRunStates.FailureMarkerDetected,
                    PromptWaitResult.Ended when !RemainingStepsAreOptional(steps,
                            index, repeats) =>
                        OfficeBugfixRunStates.ExitedBeforePlaybookCompleted,
                    _ => null
                };
                if (stopState is not null)
                {
                    await StopAsync(process, pumps);
                    return Result(processId, TryGetExitCode(process), stopState,
                        completedSteps, index);
                }
                if (prompt.Result == PromptWaitResult.Ended) break;

                var match = candidates[prompt.CandidateIndex];
                if (match.StepIndex == index && repeats >= steps[index].MaxRepeats
                    || completedSteps >= BatchPlaybookPolicy.MaximumTotalResponses)
                {
                    await StopAsync(process, pumps);
                    return Result(processId, TryGetExitCode(process),
                        OfficeBugfixRunStates.RepeatLimitExceeded, completedSteps,
                        index);
                }
                if (match.StepIndex != index)
                {
                    index = match.StepIndex;
                    repeats = 0;
                }

                // Clear all previously received text before releasing the approved
                // response so a repeated menu cannot satisfy the next step.
                collector.BeginNextStep();
                try
                {
                    // Send the response and its terminator in one pipe write. A
                    // pending set /p returns after the first write it sees, so a
                    // separate CR would be read later as an empty answer.
                    var response = match.Choice.Submit == BatchPlaybookSubmitKinds.Line
                        ? match.Choice.Send + "\r" : match.Choice.Send;
                    await WriteResponseAsync(process, response, overall.Token);
                    completedSteps++;
                    repeats++;
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

                if (steps[index].MaxRepeats == 1)
                {
                    index++;
                    repeats = 0;
                }
            }

            var exitTask = process.WaitForExitAsync(overall.Token);
            var winner = await Task.WhenAny(exitTask, collector.OutputLimitTask,
                collector.ReaderFailureTask, collector.FailureMarkerTask);
            if (winner != exitTask)
            {
                await StopAsync(process, pumps);
                return Result(processId, TryGetExitCode(process),
                    StateFor(winner, collector), completedSteps, null);
            }

            await exitTask;
            var drainTask = Task.WhenAll(pumps);
            winner = await Task.WhenAny(drainTask, collector.OutputLimitTask,
                collector.ReaderFailureTask);
            if (winner != drainTask)
                return Result(processId, process.ExitCode,
                    StateFor(winner, collector), completedSteps, null);
            await drainTask;

            return Result(processId, process.ExitCode,
                EvaluateCompletion(normalized, process.ExitCode, collector),
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
        string fileName, string expectedSha256, CancellationToken cancellationToken,
        string? revisionSha256 = null, string? stagingRoot = null,
        bool requireProtectedAcl = true)
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
        var rootPath = Path.GetFullPath(stagingRoot ?? OfficeRemediationPaths.StagingRoot);
        var root = rootPath
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var expected = Path.GetFullPath(Path.Combine(root, artifactId,
            (revisionSha256 ?? expectedSha256).ToUpperInvariant(), fileName));
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        var trustedPath = requireProtectedAcl
            ? ProtectedWindowsPath.IsDirectorySecure(rootPath, requireProtectedAcl: true)
              && ProtectedWindowsPath.IsDirectorySecure(directory,
                  requireProtectedAcl: false)
              && ProtectedWindowsPath.IsFileSecure(fullPath)
            : Directory.Exists(rootPath)
              && (File.GetAttributes(rootPath) & FileAttributes.ReparsePoint) == 0
              && Directory.Exists(directory)
              && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0
              && File.Exists(fullPath)
              && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) == 0;
        if (!fullPath.Equals(expected, StringComparison.OrdinalIgnoreCase)
            || !trustedPath)
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

    /// <summary>Writes the encoded response and its terminator as one pipe write.</summary>
    private static async Task WriteResponseAsync(Process process, string response,
        CancellationToken cancellationToken)
    {
        var bytes = process.StartInfo.StandardInputEncoding!.GetBytes(response);
        var stream = process.StandardInput.BaseStream;
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task StopAsync(Process process, IReadOnlyList<Task> pumps)
    {
        // Kill before closing input: end-of-file answers every pending set /p with an
        // empty line, which could let the BAT run a default menu branch.
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { }
        try { process.StandardInput.Close(); } catch { }
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

    /// <summary>
    /// Prompts that may answer the current wait: the current step, then later steps
    /// while every step before them may be skipped (optional, or already answered
    /// at least once in repeat mode).
    /// </summary>
    private static List<(int StepIndex, BatchPlaybookChoice Choice)> GetCandidates(
        IReadOnlyList<BatchPlaybookStep> steps, int index, int repeats)
    {
        var candidates = new List<(int, BatchPlaybookChoice)>();
        for (var stepIndex = index; stepIndex < steps.Count; stepIndex++)
        {
            var step = steps[stepIndex];
            candidates.Add((stepIndex,
                new BatchPlaybookChoice(step.Expect, step.Send, step.Submit)));
            foreach (var alternative in step.Alternatives ?? [])
                candidates.Add((stepIndex, alternative));
            var skippable = step.Optional || stepIndex == index && repeats > 0;
            if (!skippable) break;
        }
        return candidates;
    }

    private static bool RemainingStepsAreOptional(
        IReadOnlyList<BatchPlaybookStep> steps, int index, int repeats)
    {
        for (var stepIndex = index; stepIndex < steps.Count; stepIndex++)
        {
            if (stepIndex == index && repeats > 0) continue;
            if (!steps[stepIndex].Optional) return false;
        }
        return true;
    }

    private static string StateFor(Task winner, BatchOutputCollector collector) =>
        winner == collector.OutputLimitTask ? OfficeBugfixRunStates.OutputLimitExceeded
        : winner == collector.FailureMarkerTask
            ? OfficeBugfixRunStates.FailureMarkerDetected
            : OfficeBugfixRunStates.ProcessFailed;

    private static string EvaluateCompletion(BatchPlaybook playbook, int exitCode,
        BatchOutputCollector collector)
    {
        if (collector.FailureMarkerTask.IsCompleted)
            return OfficeBugfixRunStates.FailureMarkerDetected;
        if (!BatchPlaybookPolicy.GetSuccessExitCodes(playbook).Contains(exitCode))
            return OfficeBugfixRunStates.ProcessFailed;
        if (playbook.SuccessMarker is { } marker && !collector.TranscriptContains(marker))
            return OfficeBugfixRunStates.SuccessMarkerMissing;
        return OfficeBugfixRunStates.Completed;
    }
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
    public const string FailureMarkerDetected = "FailureMarkerDetected";
    public const string SuccessMarkerMissing = "SuccessMarkerMissing";
    public const string RepeatLimitExceeded = "RepeatLimitExceeded";
}

public sealed record OfficeBugfixRunResult(
    int? ProcessId,
    int? ExitCode,
    string State,
    int CompletedStepCount,
    int? FailedStepIndex,
    string? OutputTail = null);

