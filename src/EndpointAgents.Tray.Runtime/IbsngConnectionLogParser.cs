using EndpointAgents.Shared.Ibsng;
using System.Text.RegularExpressions;

namespace EndpointAgents.Tray;

/// <summary>Classifies and sanitizes the bounded text exposed by the IBSng log panel.</summary>
public static partial class IbsngConnectionLogParser
{
    private const int MaximumCharacters = 4 * 1024;
    private const int MaximumLines = 20;

    /// <summary>Returns the most recent connection state described by the log.</summary>
    public static IbsngConnectionState ParseState(IEnumerable<string> lines)
    {
        foreach (var line in lines.Reverse())
        {
            var value = line.Trim();
            if (value.Length == 0) continue;
            if (FailurePattern().IsMatch(value)) return IbsngConnectionState.Failed;
            if (DisconnectedPattern().IsMatch(value)) return IbsngConnectionState.Disconnected;
            if (AuthenticatingPattern().IsMatch(value)) return IbsngConnectionState.Authenticating;
            if (ConnectingPattern().IsMatch(value)) return IbsngConnectionState.Connecting;
            if (ConnectedPattern().IsMatch(value)) return IbsngConnectionState.Connected;
        }

        return IbsngConnectionState.Unknown;
    }

    /// <summary>Removes credential-like values and enforces the support evidence limits.</summary>
    public static IReadOnlyList<string> Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        var cleaned = new string(text
            .Where(character => character is '\r' or '\n' or '\t' || !char.IsControl(character))
            .Take(MaximumCharacters * 2)
            .ToArray());
        var lines = cleaned.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => CredentialPattern().Replace(line.Trim(), "$1[redacted]"))
            .Where(line => line.Length > 0)
            .TakeLast(MaximumLines)
            .ToList();

        while (lines.Sum(line => line.Length) > MaximumCharacters && lines.Count > 0)
            lines.RemoveAt(0);
        return lines;
    }

    /// <summary>Identifies text that plausibly belongs to an IBSng connection log.</summary>
    public static bool ContainsDiagnosticSignal(string text) =>
        ConnectedPattern().IsMatch(text) || DisconnectedPattern().IsMatch(text)
        || ConnectingPattern().IsMatch(text) || AuthenticatingPattern().IsMatch(text)
        || FailurePattern().IsMatch(text) || text.Contains("Ready", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"(?i)\b(authentication failed|invalid credentials?|bad password|access denied|timed?\s*out|unreachable|connection failed|error)\b")]
    private static partial Regex FailurePattern();

    [GeneratedRegex(@"(?i)^\s*disconnected\b")]
    private static partial Regex DisconnectedPattern();

    [GeneratedRegex(@"(?i)^\s*authenticating\b")]
    private static partial Regex AuthenticatingPattern();

    [GeneratedRegex(@"(?i)^\s*connecting\b")]
    private static partial Regex ConnectingPattern();

    [GeneratedRegex(@"(?i)^\s*connected(?:\.|\s|$)")]
    private static partial Regex ConnectedPattern();

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|username|user|credential|token)\s*[:=]\s*([^\s,;]+)")]
    private static partial Regex CredentialPattern();
}
