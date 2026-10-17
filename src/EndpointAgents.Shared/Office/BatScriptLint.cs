namespace EndpointAgents.Shared.Office;

/// <summary>
/// Categories of blocking lint finding. A reviewer may accept the first two for one
/// package (<c>review.acceptedRisks</c>); the code they cover still lives inside the
/// reviewed, hash-pinned and signed file. <see cref="NetworkDownload"/> can never be
/// accepted, because the signature cannot cover content fetched at run time.
/// </summary>
public static class BatRisks
{
    /// <summary>The BAT relaunches itself with administrator rights.</summary>
    public const string SelfElevation = "self-elevation";

    /// <summary>The BAT runs code carried inside its own (hash-pinned) bytes.</summary>
    public const string EmbeddedPayload = "embedded-payload";

    /// <summary>The BAT fetches content over the network at run time.</summary>
    public const string NetworkDownload = "network-download";

    /// <summary>Risks a reviewer is allowed to accept for a package.</summary>
    public static readonly IReadOnlyList<string> Acceptable = [SelfElevation, EmbeddedPayload];

    public static bool IsAcceptable(string? risk) =>
        risk is SelfElevation or EmbeddedPayload;
}

/// <summary>
/// Publish-time checks for BAT artifacts run unattended through a playbook. Errors keep
/// the artifact out of the catalog; warnings are reported but allowed. A blocking rule
/// whose risk the reviewer accepted becomes a warning instead. Matching is literal and
/// case-insensitive on non-comment lines.
/// </summary>
public static class BatScriptLint
{
    // The endpoint service already runs elevated, so self-elevation only matters for a
    // vendor BAT also used outside this system; an embedded payload is covered by the
    // file hash, so a reviewer can accept either. Network downloads stay blocked.
    private static readonly (string Pattern, string Reason, string Risk)[] Errors =
    [
        ("runas ", "requests administrator rights through runas", BatRisks.SelfElevation),
        ("-verb runas", "requests administrator rights through a UAC prompt", BatRisks.SelfElevation),
        ("-encodedcommand", "runs a base64 PowerShell command carried in the file", BatRisks.EmbeddedPayload),
        (" -enc ", "runs a base64 PowerShell command carried in the file", BatRisks.EmbeddedPayload),
        ("certutil -decode", "decodes a payload carried in the file", BatRisks.EmbeddedPayload),
        ("certutil -urlcache", "downloads content at run time", BatRisks.NetworkDownload),
        ("bitsadmin /transfer", "downloads content at run time", BatRisks.NetworkDownload),
        ("invoke-webrequest", "downloads content at run time", BatRisks.NetworkDownload),
        ("downloadstring(", "downloads content at run time", BatRisks.NetworkDownload),
        ("downloadfile(", "downloads content at run time", BatRisks.NetworkDownload)
    ];

    private static readonly (string Pattern, string Reason, string? Risk)[] Warnings =
    [
        ("timeout /t", "timeout fails with redirected input; use ping -n for delays", null),
        ("start ", "start can open a new window or detach a process the runner cannot answer", null),
        ("mshta", "mshta shows a GUI the runner cannot answer", null),
        ("msg ", "msg shows a GUI message the runner cannot answer", null),
        ("%temp%", "running or writing scripts under %TEMP% is outside the pinned bundle", null),
        ("\\shell\\runas\\command", "registers a runas verb to relaunch itself elevated",
            BatRisks.SelfElevation),
        // Self-extraction: code unpacked at run time is still inside the hashed file.
        ("invoke-expression", "runs PowerShell code assembled at run time", BatRisks.EmbeddedPayload),
        ("iex(", "runs PowerShell code assembled at run time", BatRisks.EmbeddedPayload),
        ("iex ", "runs PowerShell code assembled at run time", BatRisks.EmbeddedPayload),
        ("expand -r", "unpacks cabinet files at run time; ship them as supportFiles instead", null),
        ("expand -f:", "unpacks cabinet files at run time; ship them as supportFiles instead", null)
    ];

    private static void CollectSelfRead(string line, int lineNumber, List<string> findings)
    {
        if (line.Contains("powershell", StringComparison.Ordinal)
            && line.Contains("%~f0", StringComparison.Ordinal))
            findings.Add($"Line {lineNumber}: PowerShell reads this BAT's own file to run "
                + "code carried inside it (embedded-payload).");
    }

    /// <param name="acceptedRisks">
    /// Risks the reviewer accepted for this package. A blocking rule whose risk is here
    /// and is acceptable is reported as a warning instead of an error.
    /// </param>
    public static BatLintResult Inspect(string script,
        IReadOnlyCollection<string>? acceptedRisks = null)
    {
        var accepted = acceptedRisks is null ? null
            : new HashSet<string>(acceptedRisks, StringComparer.OrdinalIgnoreCase);
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
            foreach (var (pattern, reason, risk) in Errors)
            {
                if (!Matches(line, pattern)) continue;
                var text = $"Line {index + 1}: {reason}.";
                if (BatRisks.IsAcceptable(risk) && accepted?.Contains(risk) == true)
                    warnings.Add($"Accepted risk ({risk}): {text}");
                else
                    errors.Add(risk == BatRisks.NetworkDownload
                        ? $"{text} This cannot be accepted; the signature does not cover downloaded content."
                        : text);
            }
            foreach (var (pattern, reason, risk) in Warnings)
                if (Matches(line, pattern))
                    warnings.Add(risk is not null
                        ? $"({risk}) Line {index + 1}: {reason}."
                        : $"Line {index + 1}: {reason}.");
            CollectSelfRead(line, index + 1, warnings);
        }
        return new BatLintResult(errors, warnings);
    }

    private static bool Matches(string line, string pattern) =>
        CommandWords.Contains(pattern) ? IsCommand(line, pattern)
            : line.Contains(pattern, StringComparison.Ordinal);

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
