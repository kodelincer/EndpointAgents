namespace ControlPlane.Api.Files;

/// <summary>
/// Publish-time checks for BAT artifacts run unattended through a playbook. Errors
/// keep the artifact out of the catalog; warnings are reported but allowed.
/// Matching is literal and case-insensitive on non-comment lines.
/// </summary>
public static class BatScriptLint
{
    // Each of these either fetches or unpacks code that the pinned hash does not
    // cover, or needs a UAC prompt that the hidden runner cannot answer.
    private static readonly (string Pattern, string Reason)[] Errors =
    [
        ("runas ", "requests elevation through runas"),
        ("-verb runas", "requests elevation through a UAC prompt"),
        ("-encodedcommand", "runs an encoded PowerShell payload that cannot be reviewed"),
        (" -enc ", "runs an encoded PowerShell payload that cannot be reviewed"),
        ("certutil -decode", "decodes an embedded payload"),
        ("certutil -urlcache", "downloads code at run time"),
        ("bitsadmin /transfer", "downloads code at run time"),
        ("invoke-webrequest", "downloads code at run time"),
        ("downloadstring(", "downloads code at run time"),
        ("downloadfile(", "downloads code at run time")
    ];

    private static readonly (string Pattern, string Reason)[] Warnings =
    [
        ("timeout /t", "timeout fails with redirected input; use ping -n for delays"),
        ("start ", "start can open a new window or detach a process the runner cannot answer"),
        ("mshta", "mshta shows a GUI the runner cannot answer"),
        ("msg ", "msg shows a GUI message the runner cannot answer"),
        ("%temp%", "running or writing scripts under %TEMP% is outside the pinned bundle")
    ];

    public static BatLintResult Inspect(string script)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var lines = script.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = " " + lines[index].Trim().ToLowerInvariant() + " ";
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("rem ", StringComparison.Ordinal)
                || trimmed.StartsWith("::", StringComparison.Ordinal)
                || trimmed.StartsWith("@rem ", StringComparison.Ordinal))
                continue;
            Collect(line, index + 1, Errors, errors);
            Collect(line, index + 1, Warnings, warnings);
        }
        return new BatLintResult(errors, warnings);
    }

    private static void Collect(string line, int lineNumber,
        (string Pattern, string Reason)[] rules, List<string> findings)
    {
        foreach (var (pattern, reason) in rules)
        {
            var matched = CommandWords.Contains(pattern)
                ? IsCommand(line, pattern)
                : line.Contains(pattern, StringComparison.Ordinal);
            if (matched) findings.Add($"Line {lineNumber}: {reason}.");
        }
    }

    // These only count where a command starts, so labels such as "goto start"
    // or text such as "echo restart " do not match.
    private static readonly HashSet<string> CommandWords =
        ["runas ", "start ", "msg ", "timeout /t"];

    private static bool IsCommand(string line, string word)
    {
        var text = line.TrimStart().TrimStart('@');
        if (text.StartsWith(word, StringComparison.Ordinal)) return true;
        foreach (var separator in new[] { "&", "|", "(", " do " })
        {
            var index = text.IndexOf(separator + word, StringComparison.Ordinal);
            var spaced = text.IndexOf(separator + " " + word, StringComparison.Ordinal);
            if (index >= 0 || spaced >= 0) return true;
        }
        return false;
    }
}

public sealed record BatLintResult(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);
