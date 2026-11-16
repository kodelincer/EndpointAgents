using EndpointAgents.Shared.Office;
using System.Text;
using System.Text.RegularExpressions;

namespace ControlPlane.Api.Files.Builder;

/// <summary>One choice a menu offers, as read from the BAT text.</summary>
/// <param name="Value">What a person would type or press, such as <c>1</c> or <c>Y</c>.</param>
/// <param name="Label">The menu line's text, such as <c>Office repair</c>, when one was found.</param>
/// <param name="GoesTo">The <c>:label</c> the BAT jumps to for this choice, when one was found.</param>
public sealed record BatMenuOption(string Value, string? Label, string? GoesTo);

/// <summary>
/// A place where the BAT stops and waits for input, found by reading its text. It is only
/// a suggestion for the person who writes the playbook: nothing here is ever sent
/// to an endpoint until that person turns it into a step and saves the package.
/// </summary>
/// <param name="Line">1-based line of the waiting command.</param>
/// <param name="Kind"><c>menu</c> (set /p after a list), <c>question</c> (set /p), <c>choice</c> or <c>pause</c>.</param>
/// <param name="Section">The <c>:label</c> the line is in, or null at the top of the file.</param>
/// <param name="Expect">Suggested prompt text to wait for (a literal piece of what the screen shows).</param>
/// <param name="Submit"><c>line</c> (type and press Enter) or <c>key</c> (one key press).</param>
/// <param name="Options">The choices offered, when they could be read.</param>
/// <param name="Shown">The lines the BAT prints just before it waits (the screen), at most 16.</param>
/// <param name="Notes">Plain warnings about this screen, for example a prompt that contains a variable.</param>
public sealed record BatScreenSuggestion(
    int Line,
    string Kind,
    string? Section,
    string? Expect,
    string Submit,
    IReadOnlyList<BatMenuOption> Options,
    IReadOnlyList<string> Shown,
    IReadOnlyList<string> Notes);

/// <summary>What the builder page shows after a BAT is uploaded.</summary>
public sealed record BatScanResult(
    string FileName,
    string Sha256,
    long SizeBytes,
    int LineCount,
    IReadOnlyList<BatScreenSuggestion> Screens,
    IReadOnlyList<string> LintErrors,
    IReadOnlyList<string> LintWarnings,
    IReadOnlyList<string> Notes);

/// <summary>
/// Reads a BAT's text (it never runs it) and lists the menus, questions and pauses where
/// it waits for input. Matching is plain text: <c>set /p</c>, <c>choice</c> and
/// <c>pause</c>, the <c>echo</c> lines printed just before them, and the
/// <c>if ... goto</c> lines just after them. A BAT that builds its menus at run time
/// may show fewer screens than it really has; the person still checks every screen by
/// running the BAT on a test PC.
/// </summary>
public static partial class BatMenuScanner
{
    public const int MaximumScreens = 64;
    public const int MaximumShownLines = 16;
    private const int MaximumLines = 50_000;
    private const int FollowLines = 24;

    public static IReadOnlyList<BatScreenSuggestion> Scan(string text)
    {
        var lines = SplitLines(text);
        var screens = new List<BatScreenSuggestion>();
        var shown = new List<string>();
        string? section = null;
        for (var index = 0; index < lines.Count && screens.Count < MaximumScreens; index++)
        {
            var raw = lines[index];
            var line = StripAt(raw.Trim());
            if (line.Length == 0) continue;
            if (IsComment(line)) continue;
            if (line.StartsWith(':'))
            {
                section = LabelName(line);
                shown.Clear();
                continue;
            }
            if (IsCommand(line, "cls"))
            {
                shown.Clear();
                continue;
            }
            if (TryEcho(line, out var echoed))
            {
                if (echoed is not null && shown.Count < 200) shown.Add(echoed);
                continue;
            }

            BatScreenSuggestion? screen = null;
            if (TrySetPrompt(line, out var variable, out var prompt))
                screen = BuildSetPrompt(lines, index, section, shown, variable, prompt);
            else if (TryChoice(line, out var choiceKeys, out var choiceText, out var hideList))
                screen = BuildChoice(lines, index, section, shown, choiceKeys, choiceText, hideList);
            else if (TryPause(line, out var silent))
                screen = BuildPause(index, section, shown, silent);

            if (screen is not null)
            {
                screens.Add(screen);
                shown.Clear();
            }
        }
        return screens;
    }

    private static BatScreenSuggestion BuildSetPrompt(IReadOnlyList<string> lines, int index,
        string? section, List<string> shown, string variable, string prompt)
    {
        var notes = new List<string>();
        var options = ReadOptions(shown);
        var jumps = ReadJumps(lines, index, variable);
        var merged = MergeOptions(options, jumps);
        var expect = LiteralPiece(prompt, notes);
        if (expect is null && shown.Count > 0)
        {
            // set /p with no prompt text waits after the last line it printed.
            expect = LiteralPiece(shown[^1], notes);
            if (expect is not null)
                notes.Add("The prompt itself is empty, so the suggestion is the last line printed before it.");
        }
        if (expect is null)
            notes.Add("No fixed text to wait for was found. Run the BAT on a test PC and copy the text it shows.");
        return new BatScreenSuggestion(index + 1, merged.Count > 0 ? "menu" : "question",
            section, expect, BatchPlaybookSubmitKinds.Line, merged, Tail(shown), notes);
    }

    private static BatScreenSuggestion BuildChoice(IReadOnlyList<string> lines, int index,
        string? section, List<string> shown, string keys, string? text, bool hideList)
    {
        var notes = new List<string>();
        var labels = ReadOptions(shown).ToDictionary(option => option.Value,
            StringComparer.OrdinalIgnoreCase);
        var jumps = ReadErrorLevelJumps(lines, index, keys.Length);
        var options = keys.Select((key, position) =>
        {
            var value = key.ToString().ToUpperInvariant();
            labels.TryGetValue(value, out var option);
            jumps.TryGetValue(position + 1, out var target);
            return new BatMenuOption(value, option?.Label, target);
        }).ToArray();
        // choice prints "<text> [Y,N]?" unless /n hides the list.
        var list = "[" + string.Join(",", keys.Select(key => char.ToUpperInvariant(key))) + "]?";
        string? expect;
        if (text is not null)
        {
            expect = LiteralPiece(text, notes);
            if (expect is not null && !hideList && expect == Normalize(text))
                expect = Normalize($"{text} {list}");
        }
        else expect = hideList ? null : list;
        if (expect is null && shown.Count > 0)
        {
            expect = LiteralPiece(shown[^1], notes);
            if (expect is not null)
                notes.Add("choice shows no text here, so the suggestion is the last line printed before it.");
        }
        if (expect is null)
            notes.Add("No fixed text to wait for was found. Run the BAT on a test PC and copy the text it shows.");
        return new BatScreenSuggestion(index + 1, "choice", section, expect,
            BatchPlaybookSubmitKinds.Key, options, Tail(shown), notes);
    }

    private static BatScreenSuggestion BuildPause(int index, string? section,
        List<string> shown, bool silent)
    {
        var notes = new List<string>();
        string? expect = "Press any key to continue";
        if (silent)
        {
            expect = shown.Count > 0 ? LiteralPiece(shown[^1], notes) : null;
            notes.Add(expect is null
                ? "This pause waits for a key without showing any text. Run the BAT on a test PC to see what is on screen."
                : "This pause hides its own text, so the suggestion is the last line printed before it.");
        }
        notes.Add("Any single key works here; the builder suggests x.");
        return new BatScreenSuggestion(index + 1, "pause", section, expect,
            BatchPlaybookSubmitKinds.Key, [new BatMenuOption("x", "any key", null)],
            Tail(shown), notes);
    }

    // ----- line parsing ---------------------------------------------------------------

    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line && lines.Count < MaximumLines)
            lines.Add(line);
        return lines;
    }

    private static string StripAt(string line) => line.TrimStart('@').TrimStart();

    private static bool IsComment(string line) =>
        line.StartsWith("::", StringComparison.Ordinal)
        || IsCommand(line, "rem");

    private static bool IsCommand(string line, string name) =>
        line.Equals(name, StringComparison.OrdinalIgnoreCase)
        || line.Length > name.Length
            && line.StartsWith(name, StringComparison.OrdinalIgnoreCase)
            && (char.IsWhiteSpace(line[name.Length]) || line[name.Length] is '>' or '&' or '|');

    private static string LabelName(string line)
    {
        var name = line.TrimStart(':').Trim();
        var end = name.IndexOfAny([' ', '\t']);
        return end > 0 ? name[..end] : name;
    }

    /// <summary><c>echo text</c> prints text; <c>echo.</c> and <c>echo(</c> print a blank line.</summary>
    private static bool TryEcho(string line, out string? text)
    {
        text = null;
        if (!line.StartsWith("echo", StringComparison.OrdinalIgnoreCase)) return false;
        if (line.Length == 4) return true;
        var next = line[4];
        if (next is '.' or '(' or ':' or ';' or '/' or '\\' or '+' or '[' or ']')
        {
            text = CleanEcho(line[5..]);
            return true;
        }
        if (!char.IsWhiteSpace(next)) return false;
        var rest = line[5..];
        if (rest.Trim() is var word && (word.Equals("off", StringComparison.OrdinalIgnoreCase)
            || word.Equals("on", StringComparison.OrdinalIgnoreCase)))
            return true;
        text = CleanEcho(rest);
        return true;
    }

    private static string? CleanEcho(string value)
    {
        // Output redirected to nul or a file is not on the screen.
        var redirect = RedirectPattern().Match(value);
        if (redirect.Success)
        {
            if (redirect.Value.Contains("nul", StringComparison.OrdinalIgnoreCase)
                || !redirect.Value.Contains("&", StringComparison.Ordinal))
                return null;
            value = value[..redirect.Index];
        }
        var cleaned = Normalize(Unescape(value));
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>
    /// <c>set /p VAR=Prompt</c>, <c>set /p "VAR=Prompt"</c>, <c>set /p VAR="Prompt"</c>.
    /// <c>set /p =text &lt;nul</c> only prints text and is skipped.
    /// </summary>
    private static bool TrySetPrompt(string line, out string variable, out string prompt)
    {
        variable = prompt = string.Empty;
        var match = SetPromptPattern().Match(line);
        if (!match.Success) return false;
        var body = match.Groups["body"].Value.Trim();
        if (InputFromNul().IsMatch(body)) return false;
        body = StripTrailingRedirect(body);
        var quotedWhole = body.StartsWith('"');
        if (quotedWhole)
        {
            var close = body.LastIndexOf('"');
            body = close > 0 ? body[1..close] : body[1..];
        }
        var equals = body.IndexOf('=');
        if (equals <= 0) return false;
        variable = body[..equals].Trim();
        prompt = body[(equals + 1)..];
        if (!quotedWhole && prompt.Length >= 2 && prompt.StartsWith('"') && prompt.EndsWith('"'))
            prompt = prompt[1..^1];
        prompt = Unescape(prompt);
        return variable.Length > 0;
    }

    private static bool TryChoice(string line, out string keys, out string? text, out bool hideList)
    {
        keys = "YN";
        text = null;
        hideList = false;
        if (!IsCommand(line, "choice") && !IsCommand(line, "choice.exe")) return false;
        var arguments = line[(line.IndexOf(' ') is var space and > 0 ? space : line.Length)..];
        foreach (Match option in ChoiceOptionPattern().Matches(arguments))
        {
            var name = option.Groups["name"].Value.ToLowerInvariant();
            var value = option.Groups["value"].Value.Trim();
            if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
                value = value[1..^1];
            switch (name)
            {
                case "c" when value.Length > 0: keys = value; break;
                case "m" when value.Length > 0: text = Unescape(value); break;
                case "n": hideList = true; break;
            }
        }
        return true;
    }

    private static bool TryPause(string line, out bool silent)
    {
        silent = false;
        if (!IsCommand(line, "pause")) return false;
        silent = line.Contains("nul", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    /// <summary>Menu lines such as <c>[1] Office repair</c>, <c>1. Activate</c>, <c>2) Exit</c>, <c>3 - Help</c>.</summary>
    private static List<BatMenuOption> ReadOptions(IEnumerable<string> shown)
    {
        var options = new List<BatMenuOption>();
        foreach (var line in shown)
        {
            var match = MenuLinePattern().Match(line);
            if (!match.Success) continue;
            var value = match.Groups["value"].Value;
            if (options.Any(option => option.Value.Equals(value, StringComparison.OrdinalIgnoreCase)))
                continue;
            options.Add(new BatMenuOption(value, Normalize(match.Groups["label"].Value), null));
        }
        return options;
    }

    /// <summary><c>if "%VAR%"=="1" goto label</c> and its variants, in the lines after a prompt.</summary>
    private static List<BatMenuOption> ReadJumps(IReadOnlyList<string> lines, int index, string variable)
    {
        var jumps = new List<BatMenuOption>();
        var name = Regex.Escape(variable);
        var pattern = new Regex(
            $@"^if\s+(?:/i\s+)?(?:not\s+)?""?[%!]{name}[%!]""?\s*(?:==|\s+equ\s+)\s*""?(?<value>[^""\s]+)""?\s+(?:goto|call)\s+:?(?<target>[^\s&|]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        for (var next = index + 1; next < lines.Count && next <= index + FollowLines; next++)
        {
            var line = StripAt(lines[next].Trim());
            if (line.StartsWith(':')) break;
            var match = pattern.Match(line);
            if (!match.Success || line.Contains(" not ", StringComparison.OrdinalIgnoreCase)) continue;
            var value = match.Groups["value"].Value;
            if (jumps.Any(jump => jump.Value.Equals(value, StringComparison.OrdinalIgnoreCase))) continue;
            jumps.Add(new BatMenuOption(value, null, match.Groups["target"].Value));
        }
        return jumps;
    }

    /// <summary><c>if errorlevel N goto label</c> after <c>choice</c>, keyed by the key's position (1-based).</summary>
    private static Dictionary<int, string> ReadErrorLevelJumps(IReadOnlyList<string> lines, int index,
        int keyCount)
    {
        var jumps = new Dictionary<int, string>();
        for (var next = index + 1; next < lines.Count && next <= index + FollowLines; next++)
        {
            var line = StripAt(lines[next].Trim());
            if (line.StartsWith(':')) break;
            var match = ErrorLevelPattern().Match(line);
            if (!match.Success) continue;
            var level = int.Parse(match.Groups["level"].Value,
                global::System.Globalization.CultureInfo.InvariantCulture);
            if (level >= 1 && level <= keyCount)
                jumps.TryAdd(level, match.Groups["target"].Value);
        }
        return jumps;
    }

    private static List<BatMenuOption> MergeOptions(List<BatMenuOption> listed, List<BatMenuOption> jumps)
    {
        var merged = listed.Select(option => option with
        {
            GoesTo = jumps.FirstOrDefault(jump =>
                jump.Value.Equals(option.Value, StringComparison.OrdinalIgnoreCase))?.GoesTo
        }).ToList();
        foreach (var jump in jumps)
            if (!merged.Any(option => option.Value.Equals(jump.Value, StringComparison.OrdinalIgnoreCase)))
                merged.Add(jump);
        return merged;
    }

    // ----- text helpers ---------------------------------------------------------------

    /// <summary>
    /// The longest piece of the prompt without a <c>%variable%</c>, so the step keeps
    /// matching when the variable's value changes. Null when nothing useful is left.
    /// </summary>
    private static string? LiteralPiece(string text, List<string> notes)
    {
        var normalized = Normalize(text);
        if (normalized.Length == 0) return null;
        if (!VariablePattern().IsMatch(normalized))
            return Shorten(normalized);
        var piece = VariablePattern().Split(normalized)
            .Select(part => part.Trim())
            .OrderByDescending(part => part.Length)
            .FirstOrDefault() ?? string.Empty;
        if (piece.Length < 4) return null;
        notes.Add("The prompt contains a variable, so only its fixed part is suggested.");
        return Shorten(piece);
    }

    /// <summary>Keeps the end of a long prompt, where the cursor waits.</summary>
    private static string Shorten(string value) =>
        value.Length <= BatchPlaybookPolicy.MaximumMarkerCharacters ? value
            : value[^BatchPlaybookPolicy.MaximumMarkerCharacters..].TrimStart();

    private static string Normalize(string value) =>
        BatchPlaybookPolicy.NormalizeTerminalText(value);

    /// <summary>Removes cmd escape carets (<c>^&gt;</c> prints <c>&gt;</c>) and doubled percent signs.</summary>
    private static string Unescape(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '^' && index + 1 < value.Length)
            {
                builder.Append(value[++index]);
                continue;
            }
            if (value[index] == '%' && index + 1 < value.Length && value[index + 1] == '%')
            {
                builder.Append('%');
                index++;
                continue;
            }
            builder.Append(value[index]);
        }
        return builder.ToString();
    }

    private static string StripTrailingRedirect(string body)
    {
        var match = TrailingRedirectPattern().Match(body);
        return match.Success ? body[..match.Index].TrimEnd() : body;
    }

    private static IReadOnlyList<string> Tail(List<string> shown) =>
        shown.Count <= MaximumShownLines ? [.. shown] : shown[^MaximumShownLines..];

    [GeneratedRegex(@"^set\s+/p\s*(?<body>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SetPromptPattern();

    [GeneratedRegex(@"<\s*nul\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InputFromNul();

    [GeneratedRegex(@"\s*(?:2?>>?\s*\S+|&.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingRedirectPattern();

    [GeneratedRegex(@"(?<!\^)(?:\d?>>?\s*\S+|&|\|)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RedirectPattern();

    [GeneratedRegex(@"/(?<name>[cmntd])(?::|\s+|(?=/)|$)(?<value>""[^""]*""|[^/\s]*)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChoiceOptionPattern();

    [GeneratedRegex(@"^[\[\(\{<]?\s*(?<value>[0-9]{1,2}|[A-Za-z])\s*(?:[\]\)\}>]\s*[\.\-:]?|[\.\-:\)])\s+(?<label>\S.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex MenuLinePattern();

    [GeneratedRegex(@"^if\s+errorlevel\s+(?<level>\d{1,3})\s+(?:goto|call)\s+:?(?<target>[^\s&|]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ErrorLevelPattern();

    [GeneratedRegex(@"%[^%\s]+%|![^!\s]+!|%~?[0-9*]", RegexOptions.CultureInvariant)]
    private static partial Regex VariablePattern();
}
