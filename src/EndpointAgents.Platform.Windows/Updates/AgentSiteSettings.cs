using System.Text.Json;
using System.Text.Json.Nodes;

namespace EndpointAgents.Platform.Windows.Updates;

/// <summary>
/// Picks the values that belong to this PC and this organisation out of an appsettings.json:
/// the control-plane address, agent id, credential path, package signing keys and the
/// self-update settings. Everything else keeps the new version's defaults.
/// </summary>
public static class AgentSiteSettings
{
    private static readonly string[] ControlPlaneKeys = ["HubUrl", "AgentId", "DeviceCredentialPath"];
    private static readonly string[] WholeSections = ["PackageSigning", "AgentUpdate", "Site"];

    public static string Extract(string appsettingsJson)
    {
        var source = JsonNode.Parse(appsettingsJson, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) as JsonObject ?? new JsonObject();
        var result = new JsonObject();
        if (source["ControlPlane"] is JsonObject controlPlane)
        {
            var copy = new JsonObject();
            foreach (var key in ControlPlaneKeys)
                if (controlPlane[key] is { } value) copy[key] = value.DeepClone();
            result["ControlPlane"] = copy;
        }
        foreach (var section in WholeSections)
            if (source[section] is { } value) result[section] = value.DeepClone();
        return result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }
}
