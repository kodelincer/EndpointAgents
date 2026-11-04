using EndpointAgents.Shared.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EndpointAgents.Shared.Problems;

/// <summary>
/// Cleans problem text before it leaves the PC and builds the fingerprint that makes the same
/// problem count up instead of being sent again.
/// </summary>
public static partial class ProblemText
{
    /// <summary>
    /// Cuts user folders, account names, e-mail addresses, SIDs and share paths. What remains is
    /// the program, module and error code, which is what a diagnosis needs.
    /// </summary>
    public static string ScrubPersonal(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var value = UserFolder().Replace(text, @"$1\<user>");
        value = Unc().Replace(value, @"\\<server>\<share>");
        value = Email().Replace(value, "<email>");
        value = Sid().Replace(value, "<sid>");
        value = AccountLabel().Replace(value, "$1 <account>");
        value = DomainAccount().Replace(value, "<account>");
        return value;
    }

    /// <summary><see cref="ScrubPersonal"/> plus credentials, product keys and long tokens.</summary>
    public static string Scrub(string? text) =>
        SensitiveTextRedactor.Redact(ScrubPersonal(text));

    /// <summary>Keeps method names, cuts source paths and everything personal, and limits the size.</summary>
    public static string ScrubStack(string? stack)
    {
        if (string.IsNullOrWhiteSpace(stack)) return string.Empty;
        var lines = stack.Split('\n')
            .Select(line => SourceLocation().Replace(line.TrimEnd('\r'), ""))
            .Select(Scrub)
            .Take(ProblemLimits.MaxStackLines);
        return Limit(string.Join('\n', lines), ProblemLimits.MaxStackCharacters);
    }

    /// <summary>"Namespace.Type.Method" of the first frame in our own code, without arguments.</summary>
    public static string? FirstOwnFrame(string? stack)
    {
        if (string.IsNullOrEmpty(stack)) return null;
        foreach (var line in stack.Split('\n'))
        {
            var match = OwnFrame().Match(line);
            if (match.Success) return match.Groups[1].Value;
        }
        return null;
    }

    /// <summary>
    /// kind + source + exception type + first frame in our code. Without a frame the message
    /// counts, with numbers and ids taken out so "failed after 31 s" and "after 32 s" are one.
    /// </summary>
    public static string Fingerprint(string kind, string source, string? exceptionType,
        string? firstFrame, string? message)
    {
        var where = firstFrame ?? Normalize(message);
        var bytes = Encoding.UTF8.GetBytes($"{kind}|{source}|{exceptionType}|{where}");
        return Convert.ToHexStringLower(SHA256.HashData(bytes))[..32];
    }

    public static string Normalize(string? message) =>
        string.IsNullOrEmpty(message) ? string.Empty
            : Digits().Replace(Guid().Replace(message, "#"), "#").ToLowerInvariant();

    public static string Limit(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    /// <summary>Numeric comparison of "1.2.3" versions; missing parts count as 0.</summary>
    public static int CompareVersions(string? left, string? right)
    {
        static int[] Parts(string? value) => (value ?? "").Split('-', '+')[0].Split('.')
            .Select(part => int.TryParse(part, out var number) ? number : 0).ToArray();
        var a = Parts(left);
        var b = Parts(right);
        for (var index = 0; index < Math.Max(a.Length, b.Length); index++)
        {
            var difference = (index < a.Length ? a[index] : 0) - (index < b.Length ? b[index] : 0);
            if (difference != 0) return difference;
        }
        return 0;
    }

    /// <summary>A report for an exception: cleaned, limited and fingerprinted.</summary>
    public static ProblemReport FromException(string kind, string source, Exception exception,
        string? agentVersion, DateTimeOffset now, string? context = null)
    {
        var type = exception.GetType().FullName ?? exception.GetType().Name;
        var stack = exception.ToString();
        var message = FirstLine(Scrub(exception.Message));
        var title = string.IsNullOrWhiteSpace(context)
            ? $"{exception.GetType().Name}: {message}"
            : $"{FirstLine(Scrub(context))} ({exception.GetType().Name}: {message})";
        return new ProblemReport(
            Fingerprint(kind, source, type, FirstOwnFrame(stack), message),
            kind, source, Limit(title, ProblemLimits.MaxTitleCharacters), type, ScrubStack(stack),
            agentVersion, now, now, 1);
    }

    /// <summary>A report for a message without an exception.</summary>
    public static ProblemReport FromMessage(string kind, string source, string message,
        string? agentVersion, DateTimeOffset now, string? identity = null)
    {
        var clean = FirstLine(Scrub(message));
        return new ProblemReport(
            Fingerprint(kind, source, identity, null, identity is null ? clean : null),
            kind, source, Limit(clean, ProblemLimits.MaxTitleCharacters), null, null,
            agentVersion, now, now, 1);
    }

    private static string FirstLine(string value)
    {
        var cut = value.IndexOfAny(['\r', '\n']);
        return (cut < 0 ? value : value[..cut]).Trim();
    }

    [GeneratedRegex(@"([A-Za-z]:\\(?:Users|Documents and Settings))\\[^\\\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UserFolder();

    [GeneratedRegex(@"\\\\[A-Za-z0-9._-]+\\[^\s""']+")]
    private static partial Regex Unc();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"S-1-5-21(?:-\d+){3,}")]
    private static partial Regex Sid();

    /// <summary>"User: X", "Account Name: X", "on behalf of user X" and the like.</summary>
    [GeneratedRegex(@"((?:User(?: ?Name)?|Account(?: ?Name)?)\s*:|on behalf of user)\s+[^\s,;]*[^\s,;.]", RegexOptions.IgnoreCase)]
    private static partial Regex AccountLabel();

    /// <summary>DOMAIN\name, but not drive paths such as C:\Windows.</summary>
    [GeneratedRegex(@"(?<![\\/:.\w])(?![A-Za-z]:)[A-Za-z][A-Za-z0-9-]{1,14}\\[A-Za-z0-9._$-]{1,64}\b(?!\\)")]
    private static partial Regex DomainAccount();

    /// <summary>" in C:\src\File.cs:line 12" at the end of a stack frame.</summary>
    [GeneratedRegex(@"\s+in\s+\S.*:line\s+\d+\s*$")]
    private static partial Regex SourceLocation();

    [GeneratedRegex(@"^\s*at\s+((?:EndpointAgents|ControlPlane)\.[^\s(]+)")]
    private static partial Regex OwnFrame();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}\b")]
    private static partial Regex Guid();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}
