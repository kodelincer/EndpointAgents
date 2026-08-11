using EndpointAgents.Shared.Office;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Office;

[SupportedOSPlatform("windows")]
public sealed class OfficeRuntimeInspector
{
    private static readonly (string ProcessName, string DisplayName)[] Applications =
    [
        ("WINWORD", "Word"),
        ("EXCEL", "Excel"),
        ("POWERPNT", "PowerPoint"),
        ("OUTLOOK", "Outlook"),
        ("ONENOTE", "OneNote"),
        ("MSACCESS", "Access"),
        ("MSPUB", "Publisher"),
        ("VISIO", "Visio"),
        ("WINPROJ", "Project")
    ];

    public OfficeRuntimeStatus Inspect(int sessionId)
    {
        var observations = new List<(string ProcessName, int SessionId)>();
        try
        {
            foreach (var application in Applications)
            {
                var processes = Process.GetProcessesByName(application.ProcessName);
                try
                {
                    foreach (var process in processes)
                        observations.Add((application.ProcessName, process.SessionId));
                }
                finally
                {
                    foreach (var process in processes) process.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
                                   or Win32Exception
                                   or NotSupportedException)
        {
            return new OfficeRuntimeStatus(false, false, []);
        }

        return Evaluate(sessionId, observations);
    }

    internal static OfficeRuntimeStatus Evaluate(int sessionId,
        IEnumerable<(string ProcessName, int SessionId)> processes)
    {
        var running = Applications
            .Where(application => processes.Any(process =>
                process.SessionId == sessionId
                && process.ProcessName.Equals(application.ProcessName,
                    StringComparison.OrdinalIgnoreCase)))
            .Select(application => application.DisplayName)
            .ToArray();
        return new OfficeRuntimeStatus(true, running.Length > 0, running);
    }
}
