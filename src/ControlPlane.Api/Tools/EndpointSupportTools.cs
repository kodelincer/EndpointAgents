using ControlPlane.Api.Agents;
using ControlPlane.Api.Chat;
using ControlPlane.Api.Commands;
using EndpointAgents.Shared.Rpc;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace ControlPlane.Api.Tools;

[McpServerToolType]
public sealed class EndpointSupportTools
{
    private static readonly HashSet<string> MutatingMethods =
        new(StringComparer.Ordinal)
        {
            "software.office_remediation_run",
            "software.office_bugfix_run",
            "network.ibsng_ensure_dialer",
            "network.ibsng_launch"
        };

    [McpServerTool(Name = "endpoint_get_info"), Description(
        "Collect sanitized, read-only endpoint, network, proxy, and time information for the endpoint bound to this run.")]
    public static Task<string> GetEndpointInfo(
        ChatStore store,
        AgentRegistry registry,
        [Description("The random capability supplied in this run's instructions.")]
        string runCapability,
        CancellationToken cancellationToken) =>
        RunReadOnlyAsync(store, registry, runCapability, "endpoint.get_info",
            parameters: null, cancellationToken);

    [McpServerTool(Name = "ibsng_diagnose"), Description(
        "Run the approved read-only IBSng diagnosis on the endpoint bound to this run. It never requests credentials.")]
    public static Task<string> DiagnoseIbsng(
        ChatStore store,
        AgentRegistry registry,
        [Description("The random capability supplied in this run's instructions.")]
        string runCapability,
        CancellationToken cancellationToken) =>
        RunReadOnlyAsync(store, registry, runCapability, "network.ibsng_diagnose",
            parameters: null, cancellationToken);

    [McpServerTool(Name = "office_inventory"), Description(
        "Read installed Office products and versions on the endpoint bound to this run.")]
    public static Task<string> GetOfficeInventory(
        ChatStore store,
        AgentRegistry registry,
        [Description("The random capability supplied in this run's instructions.")]
        string runCapability,
        CancellationToken cancellationToken) =>
        RunReadOnlyAsync(store, registry, runCapability, "software.office_inventory",
            parameters: null, cancellationToken);

    [McpServerTool(Name = "office_activation_status"), Description(
        "Read sanitized Office activation state and compatible remediation options on the endpoint bound to this run.")]
    public static Task<string> GetOfficeActivationStatus(
        ChatStore store,
        AgentRegistry registry,
        [Description("The random capability supplied in this run's instructions.")]
        string runCapability,
        CancellationToken cancellationToken) =>
        RunReadOnlyAsync(store, registry, runCapability,
            "software.office_activation_status", parameters: null, cancellationToken);

    [McpServerTool(Name = "propose_endpoint_action"), Description(
        "Create an immutable action card for the endpoint user. This never executes the action. Use only after diagnosis.")]
    public static async Task<string> ProposeEndpointAction(
        ChatStore store,
        CommandCatalog catalog,
        [Description("The random capability supplied in this run's instructions.")]
        string runCapability,
        [Description("An approved catalog method. Arbitrary shell commands are prohibited.")]
        string method,
        [Description("A short user-facing explanation of why the action is needed.")]
        string reason,
        [Description("The exact catalog parameters, such as an approved artifactId.")]
        JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        var definition = catalog.GetAll().SingleOrDefault(item => item.Method == method);
        if (definition is null || !MutatingMethods.Contains(method))
            return """{"ok":false,"error":"The requested action is not allow-listed."}""";
        if (SensitiveCommandParameterGuard.ContainsSensitiveData(parameters))
            return """{"ok":false,"error":"Credentials and secrets are not accepted."}""";
        if (!TryValidateActionParameters(method, parameters, out var validatedParameters,
                out var validationError))
            return ToolResultSanitizer.Sanitize(new
            {
                ok = false,
                error = validationError
            });

        var proposal = await store.CreateActionProposalAsync(runCapability, method,
            definition.Name, reason, validatedParameters, TimeSpan.FromMinutes(10),
            cancellationToken);
        if (proposal is null)
            return """{"ok":false,"error":"The run capability is invalid or expired."}""";

        var scope = await store.ResolveCapabilityAsync(runCapability, cancellationToken);
        if (scope is not null)
            await store.WriteToolAuditAsync(scope, "propose_endpoint_action", "proposed",
                method, cancellationToken);
        return ToolResultSanitizer.Sanitize(new
        {
            ok = true,
            proposal.ActionId,
            proposal.DisplayName,
            proposal.Description,
            proposal.ExpiresAtUtc,
            state = proposal.State.ToString()
        });
    }

    private static async Task<string> RunReadOnlyAsync(ChatStore store,
        AgentRegistry registry, string capability, string method,
        JsonElement? parameters, CancellationToken cancellationToken)
    {
        var scope = await store.ResolveCapabilityAsync(capability, cancellationToken);
        if (scope is null)
            return """{"ok":false,"error":"The run capability is invalid or expired."}""";

        var scopedParameters = AddUserScope(parameters, scope.UserSid);
        var request = RpcRequest.Create(
            Guid.NewGuid().ToString("N"), method, scopedParameters);
        var response = await registry.SendCommandAsync(scope.AgentId, request, cancellationToken);
        var outcome = response.Error is null ? "success" : "error";
        await store.WriteToolAuditAsync(scope, method, outcome,
            response.Error?.Message, cancellationToken);
        return ToolResultSanitizer.Sanitize(response.Error is null
            ? new { ok = true, result = response.Result }
            : new { ok = false, error = response.Error.Message });
    }

    private static bool TryValidateActionParameters(string method, JsonElement? parameters,
        out JsonElement? validated, out string error)
    {
        validated = null;
        error = string.Empty;
        if (method is "network.ibsng_ensure_dialer" or "network.ibsng_launch")
        {
            if (parameters is null
                || parameters.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                || parameters.Value.ValueKind == JsonValueKind.Object
                && !parameters.Value.EnumerateObject().Any())
                return true;
            error = "This IBSng action does not accept model-supplied parameters.";
            return false;
        }

        if (parameters is not { ValueKind: JsonValueKind.Object } value)
        {
            error = "An approved artifactId is required.";
            return false;
        }
        var properties = value.EnumerateObject().ToArray();
        if (properties.Length != 1
            || !properties[0].NameEquals("artifactId")
            || properties[0].Value.ValueKind != JsonValueKind.String)
        {
            error = "Only one approved artifactId parameter is accepted.";
            return false;
        }
        var artifactId = properties[0].Value.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(artifactId) || artifactId.Length > 100
            || artifactId.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            error = "The artifactId format is invalid.";
            return false;
        }

        validated = JsonSerializer.SerializeToElement(new { artifactId });
        return true;
    }

    private static JsonElement AddUserScope(JsonElement? parameters, string userSid)
    {
        var scoped = new Dictionary<string, object?>
        {
            ["userSid"] = userSid
        };
        if (parameters is { ValueKind: JsonValueKind.Object } value)
            foreach (var property in value.EnumerateObject())
                scoped[property.Name] = property.Value.Clone();
        return JsonSerializer.SerializeToElement(scoped);
    }
}
