using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EndpointAgents.Shared.Office;

public static class BatchPlaybookSubmitKinds
{
    public const string Key = "key";
    public const string Line = "line";
}

/// <summary>An ordered, administrator-approved interaction script for one BAT.</summary>
/// <remarks>
/// Version 1 is a strictly linear list of prompts. Version 2 adds per-step
/// alternatives, optional and bounded repeating steps, accepted exit codes, and
/// literal success/failure output markers. Version 3 allows zero steps (a no-input
/// run whose standard input is closed at start), fixed literal command-line
/// arguments, an overall run timeout, and exit codes that mean "succeeded, restart
/// needed". All matching is literal.
/// </remarks>
public sealed record BatchPlaybook(
    int Version,
    IReadOnlyList<BatchPlaybookStep> Steps,
    IReadOnlyList<int>? SuccessExitCodes = null,
    string? SuccessMarker = null,
    IReadOnlyList<string>? FailureMarkers = null,
    int? GuiDialogGraceSeconds = null,
    IReadOnlyList<string>? Arguments = null,
    int? TimeoutSeconds = null,
    IReadOnlyList<int>? RestartExitCodes = null);

public sealed record BatchPlaybookStep(
    string Expect,
    string Send,
    string Submit,
    int TimeoutSeconds,
    IReadOnlyList<BatchPlaybookChoice>? Alternatives = null,
    bool Optional = false,
    int MaxRepeats = 1);

/// <summary>Another prompt the same step may show, with its own fixed response.</summary>
public sealed record BatchPlaybookChoice(
    string Expect,
    string Send,
    string Submit);

public static class BatchPlaybookPolicy
{
    public const int CurrentVersion = 3;
    public const int MinimumSteps = 1;
    public const int MaximumSteps = 32;
    public const int MaximumResponseCharacters = 64;
    public const int MinimumStepTimeoutSeconds = 5;
    public const int MaximumStepTimeoutSeconds = 300;
    public const int MaximumAlternatives = 8;
    public const int MaximumRepeats = 20;
    public const int MaximumTotalResponses = 64;
    public const int MaximumExitCodes = 16;
    public const int MaximumFailureMarkers = 16;
    public const int MaximumMarkerCharacters = 128;
    public const int DefaultGuiDialogGraceSeconds = 15;
    public const int MinimumGuiDialogGraceSeconds = 5;
    public const int MaximumGuiDialogGraceSeconds = 600;
    public const int MaximumArguments = 16;
    public const int MaximumArgumentCharacters = 128;
    public const int DefaultRunTimeoutSeconds = 480;
    public const int MinimumRunTimeoutSeconds = 30;
    public const int MaximumRunTimeoutSeconds = 3600;

    // Letters, digits, spaces and the punctuation that switches and paths need.
    // Quotes, %, !, ^, &, |, <, > and parentheses are excluded so cmd.exe cannot
    // reinterpret an argument when a BAT is started through it.
    private const string ArgumentPunctuation = " -_./\\:=,+@#";

    private static readonly IReadOnlyList<int> DefaultSuccessExitCodes = [0];

    private static readonly string[] SensitiveTerms =
    [
        "password", "passwd", "credential", "secret", "token",
        "product key", "productkey", "license key", "licensekey",
        "api key", "apikey"
    ];

    /// <summary>Exit codes that count as success; version 1 accepts only 0.</summary>
    public static IReadOnlyList<int> GetSuccessExitCodes(BatchPlaybook playbook) =>
        playbook.SuccessExitCodes is { Count: > 0 } codes
            ? codes : DefaultSuccessExitCodes;

    /// <summary>How long a GUI window may stay open before the run counts as blocked.</summary>
    public static TimeSpan GetGuiDialogGrace(BatchPlaybook playbook) =>
        TimeSpan.FromSeconds(playbook.GuiDialogGraceSeconds
            ?? DefaultGuiDialogGraceSeconds);

    /// <summary>The playbook's own overall run limit; versions 1 and 2 leave it to the runner.</summary>
    public static TimeSpan? GetRunTimeout(BatchPlaybook playbook) =>
        playbook.TimeoutSeconds is int seconds ? TimeSpan.FromSeconds(seconds) : null;

    /// <summary>Exit codes that mean the run succeeded but Windows must restart.</summary>
    public static bool IsRestartExitCode(BatchPlaybook playbook, int? exitCode) =>
        exitCode is int code && playbook.RestartExitCodes?.Contains(code) == true;

    public static bool HasArguments(BatchPlaybook? playbook) =>
        playbook?.Arguments is { Count: > 0 };

    public static bool TryNormalize(BatchPlaybook? playbook,
        out BatchPlaybook? normalized, out string? error)
    {
        normalized = null;
        if (playbook is null)
            return Fail("A playbook is required.", out error);
        if (playbook.Version is < 1 or > CurrentVersion)
            return Fail("The playbook version must be 1, 2 or 3.", out error);
        var isV2 = playbook.Version >= 2;
        var isV3 = playbook.Version == 3;
        if (playbook.Steps is null
            || playbook.Steps.Count < (isV3 ? 0 : MinimumSteps)
            || playbook.Steps.Count > MaximumSteps)
            return Fail(isV3
                    ? "A version 3 playbook can contain at most 32 ordered steps."
                    : "A playbook must contain between 1 and 32 ordered steps.",
                out error);
        if (!isV3 && (playbook.Arguments is not null
                || playbook.TimeoutSeconds is not null
                || playbook.RestartExitCodes is not null))
            return Fail("Arguments, timeoutSeconds and restartExitCodes require playbook version 3.",
                out error);
        if (!isV2 && (playbook.SuccessExitCodes is not null
                || playbook.SuccessMarker is not null
                || playbook.FailureMarkers is not null
                || playbook.GuiDialogGraceSeconds is not null))
            return Fail("Exit codes and output markers require playbook version 2.",
                out error);

        var steps = new List<BatchPlaybookStep>(playbook.Steps.Count);
        for (var index = 0; index < playbook.Steps.Count; index++)
        {
            var step = playbook.Steps[index];
            if (step is null)
                return Fail($"Playbook step {index} is missing.", out error);
            if (!isV2 && (step.Alternatives is not null || step.Optional
                    || step.MaxRepeats != 1))
                return Fail($"Playbook step {index} uses alternatives, optional, or repeats, which require playbook version 2.",
                    out error);

            if (!TryNormalizeChoice(index,
                    new BatchPlaybookChoice(step.Expect, step.Send, step.Submit),
                    out var primary, out error))
                return false;
            if (step.TimeoutSeconds is < MinimumStepTimeoutSeconds
                or > MaximumStepTimeoutSeconds)
                return Fail($"Playbook step {index} timeout must be between 5 and 300 seconds.",
                    out error);
            if (!isV2)
            {
                steps.Add(new BatchPlaybookStep(primary!.Expect, primary.Send,
                    primary.Submit, step.TimeoutSeconds));
                continue;
            }

            if (step.MaxRepeats is < 1 or > MaximumRepeats)
                return Fail($"Playbook step {index} maxRepeats must be between 1 and {MaximumRepeats}.",
                    out error);
            var alternatives = new List<BatchPlaybookChoice>();
            foreach (var alternative in step.Alternatives ?? [])
            {
                if (!TryNormalizeChoice(index, alternative, out var choice, out error))
                    return false;
                alternatives.Add(choice!);
            }
            if (alternatives.Count > MaximumAlternatives)
                return Fail($"Playbook step {index} can have at most {MaximumAlternatives} alternatives.",
                    out error);
            if (alternatives.Select(choice => choice.Expect)
                    .Prepend(primary!.Expect)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != alternatives.Count + 1)
                return Fail($"Playbook step {index} has duplicate expected prompts.",
                    out error);

            steps.Add(new BatchPlaybookStep(primary.Expect, primary.Send,
                primary.Submit, step.TimeoutSeconds, alternatives, step.Optional,
                step.MaxRepeats));
        }

        if (!isV2)
        {
            normalized = new BatchPlaybook(1, steps);
            error = null;
            return true;
        }

        var exitCodes = playbook.SuccessExitCodes ?? DefaultSuccessExitCodes;
        if (exitCodes.Count is < 1 or > MaximumExitCodes
            || exitCodes.Distinct().Count() != exitCodes.Count)
            return Fail($"successExitCodes must contain 1 to {MaximumExitCodes} distinct values.",
                out error);

        string? successMarker = null;
        if (playbook.SuccessMarker is not null
            && !TryNormalizeMarker(playbook.SuccessMarker, "successMarker",
                out successMarker, out error))
            return false;

        var failureMarkers = new List<string>();
        foreach (var marker in playbook.FailureMarkers ?? [])
        {
            if (!TryNormalizeMarker(marker, "failureMarkers", out var normalizedMarker,
                    out error))
                return false;
            failureMarkers.Add(normalizedMarker!);
        }
        if (failureMarkers.Count > MaximumFailureMarkers
            || failureMarkers.Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != failureMarkers.Count)
            return Fail($"failureMarkers must contain at most {MaximumFailureMarkers} distinct values.",
                out error);

        if (playbook.GuiDialogGraceSeconds is < MinimumGuiDialogGraceSeconds
            or > MaximumGuiDialogGraceSeconds)
            return Fail($"guiDialogGraceSeconds must be between {MinimumGuiDialogGraceSeconds} and {MaximumGuiDialogGraceSeconds}.",
                out error);

        var grace = playbook.GuiDialogGraceSeconds ?? DefaultGuiDialogGraceSeconds;
        if (!isV3)
        {
            normalized = new BatchPlaybook(2, steps, exitCodes.Order().ToArray(),
                successMarker, failureMarkers, grace);
            error = null;
            return true;
        }

        var arguments = playbook.Arguments ?? [];
        if (arguments.Count > MaximumArguments)
            return Fail($"arguments can contain at most {MaximumArguments} values.",
                out error);
        for (var index = 0; index < arguments.Count; index++)
            if (!IsValidArgument(arguments[index]))
                return Fail($"Argument {index} must be 1 to {MaximumArgumentCharacters} characters of letters, digits, spaces or - _ . / \\ : = , + @ #, and must not contain secrets.",
                    out error);

        var timeout = playbook.TimeoutSeconds ?? DefaultRunTimeoutSeconds;
        if (timeout is < MinimumRunTimeoutSeconds or > MaximumRunTimeoutSeconds)
            return Fail($"timeoutSeconds must be between {MinimumRunTimeoutSeconds} and {MaximumRunTimeoutSeconds}.",
                out error);

        var restartCodes = playbook.RestartExitCodes ?? [];
        if (restartCodes.Count > MaximumExitCodes
            || restartCodes.Distinct().Count() != restartCodes.Count)
            return Fail($"restartExitCodes must contain at most {MaximumExitCodes} distinct values.",
                out error);
        if (restartCodes.Intersect(exitCodes).Any())
            return Fail("An exit code cannot be in both successExitCodes and restartExitCodes.",
                out error);

        normalized = new BatchPlaybook(3, steps, exitCodes.Order().ToArray(),
            successMarker, failureMarkers, grace, arguments.ToArray(), timeout,
            restartCodes.Order().ToArray());
        error = null;
        return true;
    }

    private static bool IsValidArgument(string? argument) =>
        argument is { Length: >= 1 and <= MaximumArgumentCharacters }
        && argument.Trim().Length > 0
        && argument.All(character => char.IsAsciiLetterOrDigit(character)
            || ArgumentPunctuation.Contains(character))
        && !ContainsSensitiveResponse(argument)
        // A product key embedded in a switch such as /key:XXXXX-XXXXX-...
        && !Regex.IsMatch(argument, "[A-Za-z0-9]{5}(-[A-Za-z0-9]{5}){4}");

    public static string ComputeSha256(BatchPlaybook playbook)
    {
        if (!TryNormalize(playbook, out var normalized, out var error))
            throw new ArgumentException(error, nameof(playbook));

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false),
                   leaveOpen: true))
        {
            // The version 1 layout must stay byte-identical so pinned hashes remain valid.
            writer.Write(normalized!.Version);
            writer.Write(normalized.Steps.Count);
            foreach (var step in normalized.Steps)
            {
                writer.Write(step.Expect);
                writer.Write(step.Send);
                writer.Write(step.Submit);
                writer.Write(step.TimeoutSeconds);
                if (normalized.Version == 1) continue;

                writer.Write(step.Alternatives!.Count);
                foreach (var choice in step.Alternatives)
                {
                    writer.Write(choice.Expect);
                    writer.Write(choice.Send);
                    writer.Write(choice.Submit);
                }
                writer.Write(step.Optional);
                writer.Write(step.MaxRepeats);
            }
            if (normalized.Version >= 2)
            {
                writer.Write(normalized.SuccessExitCodes!.Count);
                foreach (var code in normalized.SuccessExitCodes) writer.Write(code);
                writer.Write(normalized.SuccessMarker is not null);
                writer.Write(normalized.SuccessMarker ?? string.Empty);
                writer.Write(normalized.FailureMarkers!.Count);
                foreach (var marker in normalized.FailureMarkers) writer.Write(marker);
                writer.Write(normalized.GuiDialogGraceSeconds!.Value);
            }
            if (normalized.Version == 3)
            {
                writer.Write(normalized.Arguments!.Count);
                foreach (var argument in normalized.Arguments) writer.Write(argument);
                writer.Write(normalized.TimeoutSeconds!.Value);
                writer.Write(normalized.RestartExitCodes!.Count);
                foreach (var code in normalized.RestartExitCodes) writer.Write(code);
            }
        }
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer()
            .AsSpan(0, checked((int)stream.Length))));
    }

    /// <summary>
    /// Normalizes literal prompt text in the same way as captured terminal output.
    /// ANSI escape removal is performed by the streaming endpoint collector.
    /// </summary>
    public static string NormalizeTerminalText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (char.IsControl(character)) continue;
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(character);
        }
        return builder.ToString();
    }

    private static bool TryNormalizeChoice(int index, BatchPlaybookChoice? choice,
        out BatchPlaybookChoice? normalized, out string? error)
    {
        normalized = null;
        if (choice is null)
            return Fail($"Playbook step {index} has a missing alternative.", out error);
        var expect = NormalizeTerminalText(choice.Expect);
        if (expect.Length == 0)
            return Fail($"Playbook step {index} must have a non-empty expected prompt.",
                out error);
        if (choice.Send is null || choice.Send.Length > MaximumResponseCharacters
            || choice.Send.Any(char.IsControl))
            return Fail($"Playbook step {index} has an invalid response.", out error);
        if (ContainsSensitiveResponse(choice.Send))
            return Fail($"Playbook step {index} cannot contain credentials, tokens, or product keys.",
                out error);

        var submit = (choice.Submit ?? string.Empty).Trim().ToLowerInvariant();
        if (submit is not BatchPlaybookSubmitKinds.Key
            and not BatchPlaybookSubmitKinds.Line)
            return Fail($"Playbook step {index} submission must be 'key' or 'line'.",
                out error);
        if (submit == BatchPlaybookSubmitKinds.Key && choice.Send.Length != 1)
            return Fail($"Playbook step {index} key response must contain exactly one character.",
                out error);

        normalized = new BatchPlaybookChoice(expect, choice.Send, submit);
        error = null;
        return true;
    }

    private static bool TryNormalizeMarker(string? value, string name,
        out string? normalized, out string? error)
    {
        normalized = NormalizeTerminalText(value);
        if (normalized.Length is 0 or > MaximumMarkerCharacters)
            return Fail($"Each {name} value must contain 1 to {MaximumMarkerCharacters} visible characters.",
                out error);
        error = null;
        return true;
    }

    private static bool ContainsSensitiveResponse(string response)
    {
        var normalized = response.Trim().ToLowerInvariant();
        if (SensitiveTerms.Any(term => normalized.Contains(term,
                StringComparison.Ordinal)))
            return true;

        var groups = response.Split('-');
        return groups.Length == 5
            && groups.All(group => group.Length == 5
                && group.All(char.IsAsciiLetterOrDigit));
    }

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }
}
