using EndpointAgents.Core.Commands;
using System.Net;

namespace EndpointAgents.Platform.Windows.Diagnostics;

public static class DiagnosticResponse
{
    public static object Create(
        CommandExecutionContext context,
        object? result,
        IReadOnlyCollection<DiagnosticWarning> warnings,
        IReadOnlyCollection<DiagnosticCheckResult>? checks = null)
    {
        return new
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Computer = GetComputer(context),
            Request = new
            {
                context.AgentId,
                context.RequestedBy,
                context.TicketId,
                context.ReceivedAtUtc
            },
            Result = result,
            Warnings = warnings,
            Checks = checks ?? Array.Empty<DiagnosticCheckResult>()
        };
    }

    public static object GetComputer(CommandExecutionContext context)
    {
        return new
        {
            context.AgentId,
            Hostname = SafeValue("hostname", Dns.GetHostName, Environment.MachineName),
            MachineName = Environment.MachineName,
            UserName = Environment.UserName,
            DomainName = Environment.UserDomainName,
            OSVersion = Environment.OSVersion.ToString(),
            Environment.Is64BitOperatingSystem,
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64).ToString()
        };
    }

    public static object Sanitize(Exception ex)
    {
        return new
        {
            Exception = ex.GetType().Name,
            ex.Message
        };
    }

    private static T SafeValue<T>(string source, Func<T> getValue, T fallback)
    {
        try
        {
            return getValue();
        }
        catch
        {
            _ = source;
            return fallback;
        }
    }
}
