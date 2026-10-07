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

    /// <param name="keepIpAddresses">
    /// Only for network-configuration tools whose purpose is to show IP, subnet, gateway
    /// and DNS addresses to the agent. Identifiers and secrets are still redacted.
    /// </param>
    public static string Sanitize(object value, bool keepIpAddresses = false)
    {
        var node = JsonSerializer.SerializeToNode(value);
        RedactNode(node, keepIpAddresses);
        return node?.ToJsonString() ?? "null";
    }

    private static void RedactNode(JsonNode? node, bool keepIpAddresses)
    {
        if (node is JsonObject item)
        {
            foreach (var property in item.ToArray())
            {
                if (RedactedProperties.Contains(property.Key))
                    item[property.Key] = "[redacted]";
                else
                    RedactNode(property.Value, keepIpAddresses);
            }
            return;
        }

        if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
                RedactNode(array[index], keepIpAddresses);
            return;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            value.ReplaceWith(keepIpAddresses
                ? PromptRedactor.RedactSecrets(text)
                : PromptRedactor.Redact(text));
    }
}
