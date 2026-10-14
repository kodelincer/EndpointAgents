using EndpointAgents.Shared.Actions;
using System.Text.Json;

namespace EndpointAgents.Platform.Windows.Actions;

/// <summary>
/// Turns an <see cref="EndpointActionRequest"/> into a validated <see cref="ActionInvocation"/>.
/// Used by the service (system actions) and again by the Tray (user actions), so neither
/// side trusts the other's checks.
/// </summary>
public static class EndpointActionRequestParser
{
    public static bool TryParse(JsonElement? parameters, out EndpointActionRequest? request,
        out string error)
    {
        request = null;
        error = "params must be an endpoint action request.";
        if (parameters is not { ValueKind: JsonValueKind.Object } value) return false;
        try
        {
            request = value.Deserialize<EndpointActionRequest>(EndpointActionProtocol.Json);
        }
        catch (JsonException)
        {
            return false;
        }
        if (request is null || string.IsNullOrWhiteSpace(request.ActionId)) return false;
        error = string.Empty;
        return true;
    }

    public static bool TryCreateInvocation(EndpointActionRequest request,
        ActionRunAs expectedRunAs, out ActionInvocation? invocation, out string error)
    {
        invocation = null;
        var definition = EndpointActionCatalog.Find(request.ActionId);
        if (definition is null)
        {
            error = "Unknown action.";
            return false;
        }
        if (definition.RunAs != expectedRunAs)
        {
            error = "The action runs in a different context.";
            return false;
        }
        var arguments = (request.Arguments ?? new Dictionary<string, string>())
            .ToDictionary(item => item.Key, item => (string?)item.Value, StringComparer.Ordinal);
        if (!EndpointActionValidator.TryValidate(definition, arguments, out var normalized,
                out error))
            return false;

        var targets = new Dictionary<string, ResolvedTarget>(StringComparer.Ordinal);
        foreach (var parameter in definition.Parameters
                     .Where(item => item.Kind == ActionParameterKind.Target))
        {
            if (!normalized.TryGetValue(parameter.Name, out var targetId)) continue;
            if (request.Targets is null
                || !request.Targets.TryGetValue(parameter.Name, out var target)
                || target.Id != targetId
                || !EndpointActionValidator.IsValidTarget(target)
                || parameter.TargetKinds?.Contains(target.Kind) == false)
            {
                error = $"Target '{parameter.Name}' is missing or invalid.";
                return false;
            }
            targets[parameter.Name] = target;
        }

        var settings = (request.Settings ?? new Dictionary<string, string>())
            .Where(item => ActionSettings.All.Contains(item.Key)
                && item.Value is { Length: <= 1000 } && !item.Value.Any(char.IsControl))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        invocation = new ActionInvocation(definition, normalized, targets, settings,
            request.UserSid);
        error = string.Empty;
        return true;
    }

    /// <summary>Runs an action with the catalogue timeout and turns exceptions into a Failed result.</summary>
    public static async Task<EndpointActionResult> RunAsync(IEndpointAction action,
        ActionInvocation invocation, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5,
            invocation.Definition.TimeoutSeconds)));
        try
        {
            return await action.RunAsync(invocation, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return EndpointActionResult.Failed("The action timed out.");
        }
        catch (UnauthorizedAccessException)
        {
            return EndpointActionResult.Failed("Windows denied access.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return EndpointActionResult.Failed($"The action failed ({ex.GetType().Name}).",
                new { Error = Trim(ex.Message, 300) });
        }
    }

    internal static string Trim(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty
            : value.Length <= max ? value : value[..max];
}
