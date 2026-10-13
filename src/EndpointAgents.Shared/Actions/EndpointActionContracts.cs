using System.Text.Json;
using System.Text.Json.Serialization;

namespace EndpointAgents.Shared.Actions;

/// <summary>
/// How much an action is allowed to change. The tier decides the rules the control plane
/// applies: Look runs freely, SafeFix runs without asking but is rate-limited and verified,
/// Disruptive needs the user's go-ahead in the chat, OperatorOnly is never offered to the agent.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ActionTier>))]
public enum ActionTier
{
    Look,
    SafeFix,
    Disruptive,
    OperatorOnly
}

/// <summary>Where the action runs: in the LocalSystem service, or in the signed-in user's Tray.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ActionRunAs>))]
public enum ActionRunAs
{
    System,
    User
}

[JsonConverter(typeof(JsonStringEnumConverter<ActionParameterKind>))]
public enum ActionParameterKind
{
    /// <summary>One of a fixed list of values.</summary>
    Choice,
    /// <summary>An environment-map target id; the control plane resolves it to the real address.</summary>
    Target,
    /// <summary>A name the PC itself reported in an earlier Look result (e.g. an installed printer).</summary>
    LocalItem,
    Integer,
    /// <summary>Short search text; never used as a command, path or URL.</summary>
    SearchText
}

/// <summary>Kinds of environment-map entries an administrator maintains in the dashboard.</summary>
public static class EnvironmentTargetKinds
{
    public const string Host = "host";
    public const string WebApp = "webapp";
    public const string Printer = "printer";
    public const string Share = "share";

    public static IReadOnlyList<string> All { get; } = [Host, WebApp, Printer, Share];
}

public sealed record ActionChoice(string Value, string Label);

public sealed record ActionParameter(
    string Name,
    ActionParameterKind Kind,
    string Description,
    bool Required = true,
    IReadOnlyList<ActionChoice>? Choices = null,
    IReadOnlyList<string>? TargetKinds = null,
    int Min = 0,
    int Max = 0);

/// <summary>A Look action the control plane runs before and after a fix to prove it worked.</summary>
public sealed record ActionVerification(string ActionId,
    IReadOnlyDictionary<string, string>? FixedArguments = null,
    bool PassSameArguments = false);

/// <summary>One catalogued endpoint action. The single source for the agent's tools,
/// the dashboard's capabilities page, kill switches and endpoint validation.</summary>
public sealed record EndpointActionDefinition(
    string Id,
    string Area,
    ActionTier Tier,
    ActionRunAs RunAs,
    string Name,
    string NameFa,
    string Description,
    string Returns,
    IReadOnlyList<ActionParameter> Parameters,
    int TimeoutSeconds = 30,
    int MaxPerHour = 0,
    ActionVerification? Verify = null,
    string? UndoActionId = null,
    string? ExampleResult = null,
    IReadOnlyList<string>? UsesSettings = null)
{
    /// <summary>Default per-device hourly limit when the definition sets none.</summary>
    public int EffectiveMaxPerHour => MaxPerHour > 0 ? MaxPerHour : Tier switch
    {
        ActionTier.Look => 30,
        ActionTier.SafeFix => 3,
        ActionTier.Disruptive => 2,
        _ => 5
    };
}

/// <summary>An environment-map entry after the control plane resolved it for the endpoint.</summary>
public sealed record ResolvedTarget(
    string Id,
    string Kind,
    string Label,
    string Address,
    IReadOnlyList<int>? Ports = null,
    IReadOnlyList<string>? ExpectedAddresses = null,
    string? DriveLetter = null);

/// <summary>Params of the <see cref="EndpointActionProtocol.Method"/> RPC.</summary>
public sealed record EndpointActionRequest(
    string ActionId,
    IReadOnlyDictionary<string, string> Arguments,
    IReadOnlyDictionary<string, ResolvedTarget>? Targets = null,
    IReadOnlyDictionary<string, string>? Settings = null,
    string? UserSid = null);

[JsonConverter(typeof(JsonStringEnumConverter<ActionStatus>))]
public enum ActionStatus
{
    /// <summary>A Look action read its facts.</summary>
    Ok,
    /// <summary>A fix changed what it should.</summary>
    Done,
    /// <summary>A fix found nothing to change.</summary>
    NotNeeded,
    /// <summary>Something prevents the action right now (e.g. the app must be closed first).</summary>
    Blocked,
    Failed
}

/// <summary>What every endpoint action returns.</summary>
public sealed record EndpointActionResult(
    ActionStatus Status,
    string Summary,
    object? Data = null,
    IReadOnlyDictionary<string, string>? Undo = null)
{
    public static EndpointActionResult Ok(string summary, object? data = null) =>
        new(ActionStatus.Ok, summary, data);
    public static EndpointActionResult Done(string summary, object? data = null,
        IReadOnlyDictionary<string, string>? undo = null) =>
        new(ActionStatus.Done, summary, data, undo);
    public static EndpointActionResult NotNeeded(string summary, object? data = null) =>
        new(ActionStatus.NotNeeded, summary, data);
    public static EndpointActionResult Blocked(string summary, object? data = null) =>
        new(ActionStatus.Blocked, summary, data);
    public static EndpointActionResult Failed(string summary, object? data = null) =>
        new(ActionStatus.Failed, summary, data);
}

public static class EndpointActionProtocol
{
    /// <summary>The one RPC method that runs catalogued actions on the endpoint.</summary>
    public const string Method = "endpoint.action";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
