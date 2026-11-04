using EndpointAgents.Shared.Problems;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ControlPlane.Api.Problems.Forwarding;

/// <summary>One copy of a report for the outside log server. Already cleaned on the PC and again here.</summary>
public sealed record LogForwardEvent(
    string Fingerprint, string Kind, string Source, string Title, string? ExceptionType, string? Stack,
    string AgentId, string? MachineName, string? AgentVersion, string? OsName, string? OsEdition,
    string? OsVersion, string? OsBuild, string? Architecture, string? Manufacturer, string? Model,
    int Count, DateTimeOffset FirstSeenUtc, DateTimeOffset LastSeenUtc)
{
    public static LogForwardEvent From(ProblemReport report, string agentId, ProblemDeviceFacts facts) => new(
        report.Fingerprint, report.Kind, report.Source, report.Title, report.ExceptionType, report.Stack,
        agentId, facts.MachineName, report.AgentVersion ?? facts.AgentVersion, facts.OsName, facts.OsEdition,
        facts.OsVersion, facts.OsBuild, facts.Architecture, facts.Manufacturer, facts.Model,
        report.Count, report.FirstSeenUtc, report.LastSeenUtc);

    /// <summary>Crashes count as errors, a user's "did not fix it" as a notice.</summary>
    public int SyslogSeverity => Kind switch
    {
        ProblemKinds.Crash => 2,
        ProblemKinds.UserReported => 5,
        _ => 3
    };

    public string Host => string.IsNullOrWhiteSpace(MachineName) ? AgentId : MachineName!;

    internal JsonObject Fields() => new()
    {
        ["fingerprint"] = Fingerprint,
        ["kind"] = Kind,
        ["source"] = Source,
        ["exceptionType"] = ExceptionType,
        ["agentId"] = AgentId,
        ["machineName"] = MachineName,
        ["agentVersion"] = AgentVersion,
        ["osName"] = OsName,
        ["osEdition"] = OsEdition,
        ["osVersion"] = OsVersion,
        ["osBuild"] = OsBuild,
        ["architecture"] = Architecture,
        ["manufacturer"] = Manufacturer,
        ["model"] = Model,
        ["count"] = Count,
        ["firstSeenUtc"] = FirstSeenUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        ["lastSeenUtc"] = LastSeenUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
    };
}

/// <summary>Splunk HTTP Event Collector: JSON objects one after another in one POST.</summary>
public static class SplunkFormat
{
    public const string Path = "/services/collector/event";
    public const string SourceType = "endpointagents:problem";

    public static string AuthorizationValue(string token) => "Splunk " + token;

    public static string Body(IEnumerable<LogForwardEvent> events, string? index)
    {
        var body = new StringBuilder();
        foreach (var item in events)
        {
            var fields = item.Fields();
            fields["title"] = item.Title;
            fields["stack"] = item.Stack;
            var entry = new JsonObject
            {
                ["time"] = item.LastSeenUtc.ToUnixTimeMilliseconds() / 1000.0,
                ["host"] = item.Host,
                ["source"] = "endpointagents",
                ["sourcetype"] = SourceType,
                ["event"] = fields
            };
            if (!string.IsNullOrWhiteSpace(index)) entry["index"] = index;
            body.Append(entry.ToJsonString());
        }
        return body.ToString();
    }
}

/// <summary>Graylog GELF 1.1 over HTTP: one message per POST; extra fields start with an underscore.</summary>
public static class GelfFormat
{
    public const string Path = "/gelf";

    public static string Body(LogForwardEvent item)
    {
        var message = new JsonObject
        {
            ["version"] = "1.1",
            ["host"] = item.Host,
            ["short_message"] = item.Title.Length == 0 ? item.Kind : item.Title,
            ["timestamp"] = item.LastSeenUtc.ToUnixTimeMilliseconds() / 1000.0,
            ["level"] = item.SyslogSeverity
        };
        if (!string.IsNullOrEmpty(item.Stack)) message["full_message"] = item.Stack;
        foreach (var (name, value) in item.Fields())
            if (value is not null) message["_" + Snake(name)] = value.DeepClone();
        return message.ToJsonString();
    }

    private static string Snake(string name) =>
        Regex.Replace(name, "[A-Z]", match => "_" + match.Value.ToLowerInvariant());
}

/// <summary>
/// Syslog RFC 5424. The message is JSON, so ELK, Wazuh and rsyslog can read the fields. Over TCP
/// and TLS each message starts with its length (RFC 6587 octet counting).
/// </summary>
public static partial class SyslogFormat
{
    /// <summary>A UDP packet this size passes any network without being split.</summary>
    public const int MaxUdpBytes = 1400;
    private const int LocalUse0 = 16;

    public static string Message(LogForwardEvent item)
    {
        var fields = item.Fields();
        fields["title"] = item.Title;
        var header =
            $"<{LocalUse0 * 8 + item.SyslogSeverity}>1 {item.LastSeenUtc.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ} " +
            $"{HeaderValue(item.Host, 255)} endpointagents - {HeaderValue(item.Kind, 32)} - ";
        return header + fields.ToJsonString();
    }

    public static byte[] TcpFrame(LogForwardEvent item)
    {
        var message = Encoding.UTF8.GetBytes(Message(item));
        var prefix = Encoding.ASCII.GetBytes(message.Length.ToString(CultureInfo.InvariantCulture) + " ");
        return [.. prefix, .. message];
    }

    public static byte[] UdpPacket(LogForwardEvent item)
    {
        var bytes = Encoding.UTF8.GetBytes(Message(item));
        return bytes.Length <= MaxUdpBytes ? bytes : Encoding.UTF8.GetBytes(Short(item));
    }

    /// <summary>Over UDP a long message is cut to the fields that identify it, and stays valid JSON.</summary>
    private static string Short(LogForwardEvent item)
    {
        var header = Message(item with { Title = "" });
        var cut = header.IndexOf('{');
        var fields = new JsonObject
        {
            ["fingerprint"] = item.Fingerprint,
            ["kind"] = item.Kind,
            ["source"] = item.Source,
            ["machineName"] = item.MachineName,
            ["agentVersion"] = item.AgentVersion,
            ["count"] = item.Count,
            ["title"] = ProblemText.Limit(item.Title, 200)
        };
        return header[..cut] + fields.ToJsonString();
    }

    /// <summary>RFC 5424 header values are printable ASCII without spaces.</summary>
    private static string HeaderValue(string value, int max)
    {
        var clean = NotPrintable().Replace(value, "-");
        return clean.Length == 0 ? "-" : clean.Length <= max ? clean : clean[..max];
    }

    [GeneratedRegex(@"[^\x21-\x7E]")]
    private static partial Regex NotPrintable();
}
