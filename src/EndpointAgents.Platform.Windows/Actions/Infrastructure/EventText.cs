using EndpointAgents.Shared.Problems;

namespace EndpointAgents.Platform.Windows.Actions.Infrastructure;

/// <summary>
/// Cuts personal data out of event-log text before it leaves the PC. The same cleaner cleans
/// problem reports; see <see cref="ProblemText.ScrubPersonal"/>.
/// </summary>
public static class EventText
{
    public static string Scrub(string? text) => ProblemText.ScrubPersonal(text);

    /// <summary>The last part of a Windows path, on any OS.</summary>
    public static string FileName(string path)
    {
        var trimmed = path.Trim().Trim('"');
        var cut = trimmed.LastIndexOfAny(['\\', '/']);
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }
}