using System.Security.Cryptography;
using System.Text;

namespace EndpointAgents.Shared.Office;

public static class BatchPlaybookSubmitKinds
{
    public const string Key = "key";
    public const string Line = "line";
}

/// <summary>An ordered, administrator-approved interaction script for one BAT.</summary>
public sealed record BatchPlaybook(
    int Version,
    IReadOnlyList<BatchPlaybookStep> Steps);

public sealed record BatchPlaybookStep(
    string Expect,
    string Send,
    string Submit,
    int TimeoutSeconds);

public static class BatchPlaybookPolicy
{
    public const int CurrentVersion = 1;
    public const int MinimumSteps = 1;
    public const int MaximumSteps = 32;
    public const int MaximumResponseCharacters = 64;
    public const int MinimumStepTimeoutSeconds = 5;
    public const int MaximumStepTimeoutSeconds = 300;

    private static readonly string[] SensitiveTerms =
    [
        "password", "passwd", "credential", "secret", "token",
        "product key", "productkey", "license key", "licensekey",
        "api key", "apikey"
    ];

    public static bool TryNormalize(BatchPlaybook? playbook,
        out BatchPlaybook? normalized, out string? error)
    {
        normalized = null;
        if (playbook is null)
            return Fail("A playbook is required.", out error);
        if (playbook.Version != CurrentVersion)
            return Fail("The playbook version must be 1.", out error);
        if (playbook.Steps is null
            || playbook.Steps.Count is < MinimumSteps or > MaximumSteps)
            return Fail("A playbook must contain between 1 and 32 ordered steps.",
                out error);

        var steps = new List<BatchPlaybookStep>(playbook.Steps.Count);
        for (var index = 0; index < playbook.Steps.Count; index++)
        {
            var step = playbook.Steps[index];
            if (step is null)
                return Fail($"Playbook step {index} is missing.", out error);

            var expect = NormalizeTerminalText(step.Expect);
            if (expect.Length == 0)
                return Fail($"Playbook step {index} must have a non-empty expected prompt.",
                    out error);
            if (step.Send is null || step.Send.Length > MaximumResponseCharacters
                || step.Send.Any(char.IsControl))
                return Fail($"Playbook step {index} has an invalid response.", out error);
            if (ContainsSensitiveResponse(step.Send))
                return Fail($"Playbook step {index} cannot contain credentials, tokens, or product keys.",
                    out error);

            var submit = (step.Submit ?? string.Empty).Trim().ToLowerInvariant();
            if (submit is not BatchPlaybookSubmitKinds.Key
                and not BatchPlaybookSubmitKinds.Line)
                return Fail($"Playbook step {index} submission must be 'key' or 'line'.",
                    out error);
            if (submit == BatchPlaybookSubmitKinds.Key && step.Send.Length != 1)
                return Fail($"Playbook step {index} key response must contain exactly one character.",
                    out error);
            if (step.TimeoutSeconds is < MinimumStepTimeoutSeconds
                or > MaximumStepTimeoutSeconds)
                return Fail($"Playbook step {index} timeout must be between 5 and 300 seconds.",
                    out error);

            steps.Add(new BatchPlaybookStep(expect, step.Send, submit,
                step.TimeoutSeconds));
        }

        normalized = new BatchPlaybook(CurrentVersion, steps);
        error = null;
        return true;
    }

    public static string ComputeSha256(BatchPlaybook playbook)
    {
        if (!TryNormalize(playbook, out var normalized, out var error))
            throw new ArgumentException(error, nameof(playbook));

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false),
                   leaveOpen: true))
        {
            writer.Write(normalized!.Version);
            writer.Write(normalized.Steps.Count);
            foreach (var step in normalized.Steps)
            {
                writer.Write(step.Expect);
                writer.Write(step.Send);
                writer.Write(step.Submit);
                writer.Write(step.TimeoutSeconds);
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
