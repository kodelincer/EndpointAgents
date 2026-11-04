using System.Text.Json;

namespace EndpointAgents.Shared.LocalIpc;

public static class ServiceIpcProtocol
{
    public const string PipeName = "EndpointAgents.Service.Status.v1";
    public const string StatusMethod = "status.get";
    public const string PauseMethod = "agent.pause";
    public const string ResumeMethod = "agent.resume";
    public const string SubmitSupportMethod = "support.submit";
    public const string OpenChatMethod = "chat.open";
    public const string ListChatsMethod = "chat.list";
    public const string SendChatMessageMethod = "chat.send";
    public const string GetChatDeltaMethod = "chat.sync";
    public const string CloseChatMethod = "chat.close";
    public const string StopChatRunMethod = "chat.stop";
    public const string RespondToChatActionMethod = "chat.action.respond";
    public const string DiagnoseIbsngMethod = "ibsng.diagnose";
    public const string OpenIbsngMethod = "ibsng.open";
    public const string ReportProblemsMethod = "problems.report";
    public const string ChatNotFixedMethod = "chat.not_fixed";

    /// <summary>
    /// Opens the long-lived user-session channel. After the "Attached" reply the service writes
    /// <see cref="TrayIpcRequest"/> lines and the Tray answers each with a <see cref="TrayIpcResponse"/>
    /// line carrying the same id. The service binds the channel to the caller's Windows session and
    /// user as reported by Windows, never to anything the Tray claims.
    /// </summary>
    public const string AttachTrayMethod = "tray.attach";
    public const int MaxMessageCharacters = 64 * 1024;
}

public sealed record ServiceIpcRequest(string Id, string Method, JsonElement? Payload = null);

public sealed record ServiceIpcResponse(
    string Id,
    bool Success,
    string State,
    JsonElement? Payload = null,
    string? Message = null);
