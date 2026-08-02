using ControlPlane.Api.Hermes;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ControlPlane.Api.Tools;

public static class ToolResultSanitizer
{
    private static readonly HashSet<string> RedactedProperties =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "agentId", "machineName", "hostName", "userName", "userSid",
            "domainName", "deviceId", "serialNumber", "macAddress",
            "physicalAddress", "sessionId", "desktopPath", "dialerPath",
            "filePath", "installationPath", "systemDirectory", "path",
            "password", "token", "secret", "apiKey"
        };

    public static string Sanitize(object value)
    {
        var node = JsonSerializer.SerializeToNode(value);
        RedactNode(node);
        return node?.ToJsonString() ?? "null";
    }

    private static void RedactNode(JsonNode? node)
    {
        if (node is JsonObject item)
        {
            foreach (var property in item.ToArray())
            {
                if (RedactedProperties.Contains(property.Key))
                    item[property.Key] = "[redacted]";
                else
                    RedactNode(property.Value);
            }
            return;
        }

        if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
                RedactNode(array[index]);
            return;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            value.ReplaceWith(PromptRedactor.Redact(text));
    }
}
