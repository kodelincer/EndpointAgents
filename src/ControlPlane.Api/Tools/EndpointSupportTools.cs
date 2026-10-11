using ControlPlane.Api.Agents;
using ControlPlane.Api.Chat;
using ControlPlane.Api.Commands;
using ControlPlane.Api.Files;
using ControlPlane.Api.Hermes;
using EndpointAgents.Shared.Chat;
using EndpointAgents.Shared.Packages;
using EndpointAgents.Shared.Rpc;
using EndpointAgents.Shared.Support;
using EndpointAgents.Shared.Windows;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlPlane.Api.Tools;

public sealed record EndpointActionParameters(
    [property: JsonPropertyName("artifactId")]
    [property: Description("The exact approved artifact ID returned by endpoint diagnostics.")]
    string ArtifactId);

[McpServerToolType]
public sealed class EndpointSupportTools
{
    private static readonly HashSet<string> MutatingMethods =
        new(StringComparer.Ordinal)
        {
            "software.office_remediation_run",
            "software.office_bugfix_run",
            "system.windows_hello_sample_run"
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

    [McpServerTool(Name = "endpoint_system_info"), Description(
        "Read the endpoint's configuration: network adapters with IP address, subnet mask, default gateway, DNS servers, DHCP server and lease, " +
        "DNS suffix and firewall profiles; Windows version, edition, uptime and pending restart; domain, workgroup or Entra ID membership and " +
        "domain controller reachability; hardware and memory; and disk free space. Read-only.")]
    public static Task<string> GetEndpointSystemInfo(
        ChatStore store,
        AgentRegistry registry,
        [Description("The random capability supplied in this run's instructions.")]
        string runCapability,
        [Description("Optional subset of sections: network, windows, domain, hardware, storage. Omit for all.")]
        string[]? sections = null,
        CancellationToken cancellationToken = default)
    {
        if (!SystemInfoContracts.TryNormalizeSections(sections, out var normalized))
            return Task.FromResult(ToolResultSanitizer.Sanitize(new
            {
                ok = false,
                error = $"Unknown section. Use only: {string.Join(", ", SystemInfoContracts.Sections)}."
            }));
        return RunReadOnlyAsync(store, registry, runCapability, SystemInfoContracts.Method,
            JsonSerializer.SerializeToElement(new { sections = normalized }),
            cancellationToken, keepIpAddresses: true);
    }

    [McpServerTool(Name = "windows_activation_status"), Description(
        "Read sanitized Windows version, edition, license, and activation state for the endpoint bound to this run.")]
    public static Task<string> GetWindowsActivationStatus(
        ChatStore store,
        AgentRegistry registry,
        [Description("The random capability supplied in this run's instructions.")]
        string runCapability,
        CancellationToken cancellationToken) =>
        RunReadOnlyAsync(store, registry, runCapability,
            "system.windows_activation_status", parameters: null, cancellationToken);

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

    [McpServerTool(Name = "office_runtime_status"), Description(
        "Report Office applications currently open in the Windows session bound to this run.")]
    public static Task<string> GetOfficeRuntimeStatus(
        ChatStore store,
        AgentRegistry registry,
        [Description("The random capability supplied in this run's instructions.")]
        string runCapability,
        CancellationToken cancellationToken) =>
        RunReadOnlyAsync(store, registry, runCapability,
            "software.office_runtime_status", parameters: null, cancellationToken);

    [McpServerTool(Name = "run_windows_activation_repair"), Description(
        "Run a compatible administrator-approved Windows activation repair by artifact ID after an explicit activation request.")]
    public static Task<string> RunWindowsActivationRepair(
        ChatStore store,
        AgentRegistry registry,
        CommandCatalog catalog,
        FileArtifactRepository files,
        [Description("The random capability supplied in this run's instructions.")]
        string runCapability,
        [Description("The exact approved artifact ID returned by Windows activation status.")]
        string artifactId,
        CancellationToken cancellationToken) =>
        RunAutomaticRepairAsync(store, registry, catalog, files, runCapability,
            "system.windows_activation_bugfix_run", artifactId,
            "Windows activation repair", "run_windows_activation_repair",
            ChatProgressActivity.WindowsActivationRepair, cancellationToken);

    [McpServerTool(Name = "run_office_activation_repair"), Description(
        "Run one compatible administrator-approved Office activation FixManager without user approval. Office applications must already be closed.")]
    public static Task<string> RunOfficeActivationRepair(
        ChatStore store,
        AgentRegistry registry,
        CommandCatalog catalog,
        FileArtifactRepository files,
        [Description("The random capability supplied in this run's instructions.")]
        string runCapability,
        [Description("The exact approved artifact ID returned by Office activation status.")]
        string artifactId,
        CancellationToken cancellationToken) =>
        RunAutomaticRepairAsync(store, registry, catalog, files, runCapability,
            "software.office_bugfix_run", artifactId, "Office repair",
            "run_office_activation_repair", ChatProgressActivity.OfficeActivationRepair,
            cancellationToken);

    [McpServerTool(Name = "find_repair_packages"), Description(
        "List the administrator-approved repair packages, grouped by problem in the order to try them, with the symptoms each fixes and whether it already ran in this conversation.")]
    public static async Task<string> FindRepairPackages(
        ChatStore store,
        FileArtifactRepository files,
        [Description("The random capability supplied in this run's instructions.")]
        string runCapability,
        CancellationToken cancellationToken)
    {
        var scope = await store.ResolveCapabilityAsync(runCapability, cancellationToken);
        if (scope is null)
            return """{"ok":false,"error":"The run capability is invalid or expired."}""";
        var packages = await files.GetRepairPackagesAsync(cancellationToken);
        var tried = (await store.GetRepairAttemptsAsync(scope.ConversationId,
                cancellationToken))
            .Select(attempt => attempt.ArtifactId)
            .ToHashSet(StringComparer.Ordinal);
        await store.WriteToolAuditAsync(scope, "find_repair_packages", "success",
            null, cancellationToken);
        return ToolResultSanitizer.Sanitize(new
        {
            ok = true,
            packages = packages.Select(item => new
            {
                packageId = item.Descriptor.PackageId,
                problem = item.Problem,
                order = item.Order,
                alreadyTried = tried.Contains(item.Descriptor.PackageId),
                displayName = item.Descriptor.DisplayName,
                description = item.Descriptor.Description,
                symptoms = item.Descriptor.Symptoms,
                minWindowsVersion = item.Descriptor.MinWindowsVersion,
                maxWindowsVersionExclusive = item.Descriptor.MaxWindowsVersionExclusive
            })
        });
    }

    [McpServerTool(Name = "list_repair_attempts"), Description(
        "List the repairs already run in this conversation, oldest first, with whether each finished. Use it before choosing a repair so the same one is not run twice.")]
    public static async Task<string> ListRepairAttempts(
        ChatStore store,
        [Description("The random capability supplied in this run's instructions.")]
        string runCapability,
        CancellationToken cancellationToken)
    {
        var scope = await store.ResolveCapabilityAsync(runCapability, cancellationToken);
        if (scope is null)
            return """{"ok":false,"error":"The run capability is invalid or expired."}""";
        var attempts = await store.GetRepairAttemptsAsync(scope.ConversationId,
            cancellationToken);
        return ToolResultSanitizer.Sanitize(new
        {
            ok = true,
            attempts = attempts.Select(attempt => new
            {
                artifactId = attempt.ArtifactId,
                tool = attempt.ToolName,
                outcome = attempt.Outcome
            })
        });
    }

    [McpServerTool(Name = "record_feature_request"), Description(
        "Save a request for something the support agent cannot do yet, for the development team to review. Call it only after the user agreed to save it. It changes nothing on the endpoint.")]
    public static async Task<string> RecordFeatureRequest(
        ChatStore store,
        [Description("The random capability supplied in this run's instructions.")]
        string runCapability,
        [Description("One short English line naming the ability the user wants, e.g. 'Install a printer driver'.")]
        string summary,
        [Description("The user's request in their own words, without passwords or personal details.")]
        string userRequest,
        CancellationToken cancellationToken)
    {
        var scope = await store.ResolveCapabilityAsync(runCapability, cancellationToken);
        if (scope is null)
            return """{"ok":false,"error":"The run capability is invalid or expired."}""";
        if (string.IsNullOrWhiteSpace(summary))
            return """{"ok":false,"error":"A summary is required."}""";
        var saved = await store.RecordFeatureRequestAsync(scope,
            PromptRedactor.RedactSecrets(summary),
            PromptRedactor.RedactSecrets(userRequest ?? ""), cancellationToken);
        await store.WriteToolAuditAsync(scope, "record_feature_request",
            saved is null ? "error" : "recorded", null, cancellationToken);
        return saved is null
            ? """{"ok":false,"error":"The conversation is not available."}"""
            : """{"ok":true,"state":"Recorded"}""";
    }

    [McpServerTool(Name = "escalate_to_helpdesk"), Description(
        "Hand this conversation to the human helpdesk team when no approved repair fits, every matching repair was tried and the user says the problem remains, or the user asks for a person. It changes nothing on the endpoint.")]
    public static async Task<string> EscalateToHelpdesk(
        ChatStore store,
        [Description("The random capability supplied in this run's instructions.")]
        string runCapability,
        [Description("One short English line: why the agent could not solve it.")]
        string reason,
        [Description("A short English summary for the helpdesk: the problem, what was checked and tried, and what the user said.")]
        string summary,
        CancellationToken cancellationToken)
    {
        var scope = await store.ResolveCapabilityAsync(runCapability, cancellationToken);
        if (scope is null)
            return """{"ok":false,"error":"The run capability is invalid or expired."}""";
        if (string.IsNullOrWhiteSpace(reason))
            return """{"ok":false,"error":"A reason is required."}""";
        // The text is stored for operators; keep secrets and identifiers out of it.
        var escalated = await store.EscalateConversationAsync(scope,
            PromptRedactor.RedactSecrets(reason), PromptRedactor.RedactSecrets(summary ?? ""),
            cancellationToken);
        await store.WriteToolAuditAsync(scope, "escalate_to_helpdesk",
            escalated ? "escalated" : "error", null, cancellationToken);
        return escalated ? """{"ok":true,"state":"Escalated"}"""
            : """{"ok":false,"error":"The conversation is not available."}""";
    }

    [McpServerTool(Name = "run_repair_package"), Description(
        "Run one administrator-approved repair package on the user's PC by the exact packageId from find_repair_packages. It runs unattended with the administrator's fixed arguments and answers; nothing else can be passed.")]
    public static Task<string> RunRepairPackage(
        ChatStore store,
        AgentRegistry registry,
        CommandCatalog catalog,
        FileArtifactRepository files,
        [Description("The random capability supplied in this run's instructions.")]
        string runCapability,
        [Description("The exact packageId returned by find_repair_packages.")]
        string packageId,
        CancellationToken cancellationToken) =>
        RunAutomaticRepairAsync(store, registry, catalog, files, runCapability,
            PackageCommands.Run, packageId, "repair package", "run_repair_package",
            ChatProgressActivity.EndpointCheck, cancellationToken);

    private static async Task<string> RunAutomaticRepairAsync(
        ChatStore store,
        AgentRegistry registry,
        CommandCatalog catalog,
        FileArtifactRepository files,
        string runCapability,
        string method,
        string artifactId,
        string repairName,
        string toolName,
        ChatProgressActivity activity,
        CancellationToken cancellationToken)
    {
        var scope = await store.ResolveCapabilityAsync(runCapability, cancellationToken);
        if (scope is null)
            return """{"ok":false,"error":"The run capability is invalid or expired."}""";
        if (!catalog.Contains(method))
            return ToolResultSanitizer.Sanitize(new
            {
                ok = false,
                error = $"The {repairName} is not allow-listed."
            });

        var parameters = new EndpointActionParameters(artifactId);
        if (!TryValidateActionParameters(method, parameters, out var validated,
                out var validationError))
            return ToolResultSanitizer.Sanitize(new { ok = false, error = validationError });
        var pinned = await PinCataloguedActionAsync(files, method, validated,
            cancellationToken);
        if (pinned is null)
            return """{"ok":false,"error":"The requested artifact is not in the approved catalog."}""";

        var requestId = Guid.NewGuid().ToString("N");
        var request = RpcRequest.Create(requestId, method,
            AddUserScope(pinned, scope.UserSid, requestId));
        await store.WriteProgressAsync(scope.RunId, ChatProgressStage.ActionStarted,
            activity, ChatProgressOutcome.None, cancellationToken);

        var response = await registry.SendCommandAsync(scope.AgentId, request,
            cancellationToken);
        var succeeded = response.Error is null;
        await store.WriteToolAuditAsync(scope, toolName,
            succeeded ? "success" : "error", response.Error?.Message, cancellationToken);
        await store.RecordRepairAttemptAsync(scope, toolName, artifactId, succeeded,
            response.Error?.Message, cancellationToken);
        await store.WriteProgressAsync(scope.RunId, ChatProgressStage.ActionCompleted,
            activity,
            succeeded ? ChatProgressOutcome.Success : ChatProgressOutcome.Failure,
            cancellationToken);

        return ToolResultSanitizer.Sanitize(succeeded
            ? new { ok = true, result = response.Result }
            : new
            {
                ok = false,
                error = response.Error!.Message,
                details = response.Error.Data
            });
    }

    [McpServerTool(Name = "propose_endpoint_action"), Description(
        "Create an immutable approved support action card for the endpoint user. This never executes the action and requires user consent.")]
    public static async Task<string> ProposeEndpointAction(
        ChatStore store,
        CommandCatalog catalog,
        FileArtifactRepository files,
        [Description("The random capability supplied in this run's instructions.")]
        string runCapability,
        [Description("An approved catalog method. Arbitrary shell commands are prohibited.")]
        string method,
        [Description("A short user-facing explanation of why the action is needed.")]
        string reason,
        [Description("The exact catalog action parameters.")]
        EndpointActionParameters parameters,
        CancellationToken cancellationToken)
    {
        var definition = catalog.GetAll().SingleOrDefault(item => item.Method == method);
        if (definition is null || !MutatingMethods.Contains(method))
            return """{"ok":false,"error":"The requested action is not allow-listed."}""";
        JsonElement? suppliedParameters = parameters is null ? null
            : JsonSerializer.SerializeToElement(parameters);
        if (SensitiveCommandParameterGuard.ContainsSensitiveData(suppliedParameters))
            return """{"ok":false,"error":"Credentials and secrets are not accepted."}""";
        if (!TryValidateActionParameters(method, parameters, out var validatedParameters,
                out var validationError))
            return ToolResultSanitizer.Sanitize(new
            {
                ok = false,
                error = validationError
            });
        var pinnedParameters = await PinCataloguedActionAsync(files, method,
            validatedParameters, cancellationToken);
        if (pinnedParameters is null)
            return """{"ok":false,"error":"The requested artifact is not in the approved catalog."}""";

        var progressScope = await store.ResolveCapabilityAsync(
            runCapability, cancellationToken);
        if (progressScope is not null)
            await store.WriteProgressAsync(progressScope.RunId,
                ChatProgressStage.EndpointCheckStarted,
                ChatProgressActivity.ProposedAction, ChatProgressOutcome.None,
                cancellationToken);

        var proposal = await store.CreateActionProposalAsync(runCapability, method,
            definition.Name, PersianUserTextPolicy.ActionReason(reason),
            pinnedParameters, TimeSpan.FromMinutes(10), cancellationToken);
        if (proposal is null)
        {
            if (progressScope is not null)
                await store.WriteProgressAsync(progressScope.RunId,
                    ChatProgressStage.EndpointCheckCompleted,
                    ChatProgressActivity.ProposedAction, ChatProgressOutcome.Failure,
                    cancellationToken);
            return """{"ok":false,"error":"The run capability is invalid or expired."}""";

        }

        var scope = await store.ResolveCapabilityAsync(runCapability, cancellationToken);
        if (scope is not null)
        {
            await store.WriteToolAuditAsync(scope, "propose_endpoint_action", "proposed",
                method, cancellationToken);
            await store.WriteProgressAsync(scope.RunId,
                ChatProgressStage.EndpointCheckCompleted,
                ChatProgressActivity.ProposedAction, ChatProgressOutcome.Success,
                cancellationToken);
            await store.WriteProgressAsync(scope.RunId,
                ChatProgressStage.AwaitingApproval,
                ChatProgressActivity.ProposedAction, ChatProgressOutcome.None,
                cancellationToken);
        }
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

    [McpServerTool(Name = "mark_problem_resolved"), Description(
        "Mark this run's support conversation as resolved. Call it only after the endpoint user clearly confirms that their problem is solved. It changes nothing on the endpoint.")]
    public static async Task<string> MarkProblemResolved(
        ChatStore store,
        [Description("The random capability supplied in this run's instructions.")]
        string runCapability,
        CancellationToken cancellationToken)
    {
        var scope = await store.ResolveCapabilityAsync(runCapability, cancellationToken);
        if (scope is null)
            return """{"ok":false,"error":"The run capability is invalid or expired."}""";
        var updated = await store.PatchHelpdeskConversationAsync(scope.ConversationId,
            new HelpdeskConversationPatch(HelpdeskStatus.Resolved, null, null),
            cancellationToken);
        if (!updated)
            return """{"ok":false,"error":"The conversation is not available."}""";
        await store.WriteToolAuditAsync(scope, "mark_problem_resolved", "resolved",
            null, cancellationToken);
        return """{"ok":true,"state":"Resolved"}""";
    }
    private static async Task<string> RunReadOnlyAsync(ChatStore store,
        AgentRegistry registry, string capability, string method,
        JsonElement? parameters, CancellationToken cancellationToken,
        bool keepIpAddresses = false)
    {
        var scope = await store.ResolveCapabilityAsync(capability, cancellationToken);
        if (scope is null)
            return """{"ok":false,"error":"The run capability is invalid or expired."}""";

        var scopedParameters = AddUserScope(parameters, scope.UserSid);
        var request = RpcRequest.Create(
            Guid.NewGuid().ToString("N"), method, scopedParameters);
        var activity = GetProgressActivity(method);
        await store.WriteProgressAsync(scope.RunId,
            ChatProgressStage.EndpointCheckStarted, activity,
            ChatProgressOutcome.None, cancellationToken);
        var response = await registry.SendCommandAsync(scope.AgentId, request, cancellationToken);
        var outcome = response.Error is null ? "success" : "error";
        await store.WriteToolAuditAsync(scope, method, outcome,
            response.Error?.Message, cancellationToken);
        await store.WriteProgressAsync(scope.RunId,
            ChatProgressStage.EndpointCheckCompleted, activity,
            response.Error is null
                ? ChatProgressOutcome.Success
                : ChatProgressOutcome.Failure,
            cancellationToken);
        return ToolResultSanitizer.Sanitize(response.Error is null
            ? new { ok = true, result = response.Result }
            : new { ok = false, error = response.Error.Message }, keepIpAddresses);
    }

    private static ChatProgressActivity GetProgressActivity(string method) => method switch
    {
        "software.office_inventory" => ChatProgressActivity.OfficeInventory,
        "software.office_activation_status" => ChatProgressActivity.OfficeActivationStatus,
        "software.office_runtime_status" => ChatProgressActivity.OfficeRuntimeStatus,
        "system.windows_activation_status" => ChatProgressActivity.WindowsActivationStatus,
        _ => ChatProgressActivity.EndpointCheck
    };

    private static bool TryValidateActionParameters(string method,
        EndpointActionParameters? parameters,
        out JsonElement? validated, out string error)
    {
        validated = null;
        error = string.Empty;
        if (parameters is null)
        {
            error = "An approved artifactId is required.";
            return false;
        }
        var artifactId = parameters.ArtifactId?.Trim();
        if (string.IsNullOrWhiteSpace(artifactId) || artifactId.Length > 100
            || artifactId.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            error = "The artifactId format is invalid.";
            return false;
        }

        var requiredArtifact = method switch
        {
            "system.windows_hello_sample_run" => "windows-hello-sample",
            _ => null
        };
        if (requiredArtifact is not null && artifactId != requiredArtifact)
        {
            error = "The fixed approved artifactId is required for this action.";
            return false;
        }

        validated = JsonSerializer.SerializeToElement(new { artifactId });
        return true;
    }

    private static JsonElement AddUserScope(JsonElement? parameters, string userSid,
        string? authorizedRequestId = null)
    {
        var scoped = new Dictionary<string, object?>
        {
            ["userSid"] = userSid
        };
        if (parameters is { ValueKind: JsonValueKind.Object } value)
            foreach (var property in value.EnumerateObject())
                scoped[property.Name] = property.Value.Clone();
        if (authorizedRequestId is not null)
            scoped[ControlPlaneActionAuthorization.ParameterName] = authorizedRequestId;
        return JsonSerializer.SerializeToElement(scoped);
    }

    private static async Task<JsonElement?> PinCataloguedActionAsync(
        FileArtifactRepository files, string method, JsonElement? parameters,
        CancellationToken cancellationToken)
    {
        var artifactId = parameters!.Value.GetProperty("artifactId").GetString()!;
        if (method == "software.office_bugfix_run")
        {
            var artifact = await files.GetOfficeBugfixArtifactAsync(
                artifactId, cancellationToken);
            return artifact is null ? null : PinArtifact(artifactId,
                artifact.Descriptor.FileName, artifact.Descriptor.Sha256,
                artifact.Descriptor.PlaybookSha256,
                artifact.Descriptor.BundleSha256);
        }
        if (method == PackageCommands.Run)
        {
            var package = await files.GetRepairPackageAsync(artifactId,
                cancellationToken);
            return package is null ? null : PinArtifact(artifactId,
                package.Descriptor.FileName, package.Descriptor.Sha256,
                package.Descriptor.PlaybookSha256, package.Descriptor.BundleSha256);
        }
        if (method == "software.office_remediation_run")
        {
            var artifact = await files.GetOfficeArtifactAsync(
                artifactId, cancellationToken);
            return artifact is null ? null : PinArtifact(artifactId,
                artifact.Descriptor.FileName, artifact.Descriptor.Sha256);
        }
        if (method is "system.windows_activation_bugfix_run"
            or "system.windows_hello_sample_run")
        {
            var artifact = await files.GetWindowsBugfixArtifactAsync(
                artifactId, cancellationToken);
            return artifact is null ? null : PinArtifact(artifactId,
                artifact.Descriptor.FileName, artifact.Descriptor.Sha256,
                artifact.Descriptor.PlaybookSha256,
                artifact.Descriptor.BundleSha256);
        }
        return null;
    }

    private static JsonElement PinArtifact(string artifactId, string fileName,
        string sha256, string? playbookSha256 = null,
        string? bundleSha256 = null)
    {
        var pinned = new Dictionary<string, string>
        {
            ["artifactId"] = artifactId,
            [ChatActionApproval.ArtifactFileNameParameterName] = fileName,
            [ChatActionApproval.ArtifactSha256ParameterName] = sha256
        };
        if (playbookSha256 is not null)
            pinned[ChatActionApproval.PlaybookSha256ParameterName] = playbookSha256;
        if (bundleSha256 is not null)
            pinned[ChatActionApproval.BundleSha256ParameterName] = bundleSha256;
        return JsonSerializer.SerializeToElement(pinned);
    }
}
