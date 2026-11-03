using System.Text.RegularExpressions;

namespace EndpointAgents.Platform.Windows.Actions.Infrastructure;

/// <summary>
/// Cuts personal data out of event-log text before it leaves the PC: user folders, account
/// names, e-mail addresses, SIDs and share paths. What remains is the program, module and
/// error code, which is what a diagnosis needs.
/// </summary>
public static partial class EventText
{
    public static string Scrub(string? text)
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

    /// <summary>The last part of a Windows path, on any OS.</summary>
    public static string FileName(string path)
    {
        var trimmed = path.Trim().Trim('"');
        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
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
}
