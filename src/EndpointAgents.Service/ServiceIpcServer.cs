using EndpointAgents.Core.Commands;
using EndpointAgents.Platform.Windows.Ibsng;
using EndpointAgents.Shared.Ibsng;
using EndpointAgents.Shared.LocalIpc;
using EndpointAgents.Shared.Rpc;
using EndpointAgents.Shared.Support;
using Microsoft.Extensions.Options;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace EndpointAgents.Service;

public sealed class ServiceIpcServer(
    AgentRuntimeState runtime,
    AgentWorker worker,
    CommandDispatcher dispatcher,
    IbsngDiagnosticService ibsngDiagnostics,
    IOptions<IbsngOptions> ibsngOptions,
    ILogger<ServiceIpcServer> logger) : BackgroundService
{
    private readonly object _submissionGate = new();
    private DateTimeOffset _lastSubmissionUtc = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(stoppingToken);
                await HandleConnectionAsync(pipe, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The local Tray status pipe failed. Retrying.");
                await Task.Delay(500, stoppingToken);
            }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(ServiceIpcProtocol.PipeName,
            PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            16 * 1024, 16 * 1024, security);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
        ServiceIpcResponse response;
        try
        {
            var line = await ReadBoundedLineAsync(reader, cancellationToken);
            var request = JsonSerializer.Deserialize<ServiceIpcRequest>(line)
                ?? throw new InvalidDataException("The local IPC request was empty.");
            response = await DispatchAsync(pipe, request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Rejected an invalid local Tray request.");
            response = new ServiceIpcResponse(string.Empty, false, "InvalidRequest",
                Message: "The local request was invalid.");
        }

        await writer.WriteLineAsync(JsonSerializer.Serialize(response).AsMemory(), cancellationToken);
    }

    private async Task<ServiceIpcResponse> DispatchAsync(NamedPipeServerStream pipe,
        ServiceIpcRequest request, CancellationToken cancellationToken)
    {
        switch (request.Method)
        {
            case ServiceIpcProtocol.StatusMethod:
                return Success(request.Id, "Status", runtime.GetSnapshot());

            case ServiceIpcProtocol.PauseMethod:
                if (!IsCallerAdministrator(pipe)) return AccessDenied(request.Id);
                runtime.SetPaused(true);
                logger.LogWarning("Endpoint functions were paused by local administrator {UserName}.",
                    GetCallerName(pipe));
                return Success(request.Id, "Paused", runtime.GetSnapshot());

            case ServiceIpcProtocol.ResumeMethod:
                if (!IsCallerAdministrator(pipe)) return AccessDenied(request.Id);
                runtime.SetPaused(false);
                logger.LogInformation("Endpoint functions were resumed by local administrator {UserName}.",
                    GetCallerName(pipe));
                return Success(request.Id, "Active", runtime.GetSnapshot());

            case ServiceIpcProtocol.SubmitSupportMethod:
                return await SubmitSupportAsync(pipe, request, cancellationToken);

            case ServiceIpcProtocol.DiagnoseIbsngMethod:
                return await DiagnoseIbsngAsync(request, cancellationToken);

            case ServiceIpcProtocol.OpenIbsngMethod:
                return await OpenIbsngAsync(request, cancellationToken);

            default:
                return new ServiceIpcResponse(request.Id, false, "UnknownMethod",
                    Message: "The requested local operation is not supported.");
        }
    }

    private async Task<ServiceIpcResponse> DiagnoseIbsngAsync(ServiceIpcRequest request,
        CancellationToken cancellationToken)
    {
        if (!IbsngDiagnoseCommandHandler.TryParse(request.Payload,
            ibsngOptions.Value.ServerHost, out var diagnosticRequest, out var error))
            return new ServiceIpcResponse(request.Id, false, "InvalidIbsngRequest",
                Message: error);

        var report = await ibsngDiagnostics.DiagnoseAsync(request.Id, diagnosticRequest!,
            cancellationToken);
        return Success(request.Id, "Diagnosed", report);
    }

    private async Task<ServiceIpcResponse> OpenIbsngAsync(ServiceIpcRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = runtime.GetSnapshot();
        var rpc = RpcRequest.Create(request.Id, "network.ibsng_launch");
        var context = new CommandExecutionContext(snapshot.AgentId, "Local Tray", null,
            DateTimeOffset.UtcNow);
        var response = await dispatcher.DispatchAsync(rpc, context, cancellationToken);
        return response.Error is null
            ? Success(request.Id, "Opened", response.Result)
            : new ServiceIpcResponse(request.Id, false, "OpenFailed",
                response.Error.Data is null ? null : JsonSerializer.SerializeToElement(response.Error.Data),
                response.Error.Message);
    }

    private async Task<ServiceIpcResponse> SubmitSupportAsync(NamedPipeServerStream pipe,
        ServiceIpcRequest request, CancellationToken cancellationToken)
    {
        if (request.Payload is not { } payload)
            return new ServiceIpcResponse(request.Id, false, "InvalidSupportRequest",
                Message: "The support request body is missing.");

        var draft = payload.Deserialize<SupportRequestDraft>();
        if (draft is null || string.IsNullOrWhiteSpace(draft.Subject)
            || string.IsNullOrWhiteSpace(draft.MessageText))
            return new ServiceIpcResponse(request.Id, false, "InvalidSupportRequest",
                Message: "Subject and problem description are required.");
        if (draft.Subject.Length > 120 || draft.MessageText.Length > 8_000)
            return new ServiceIpcResponse(request.Id, false, "SupportRequestTooLarge",
                Message: "The support request exceeds the allowed size.");

        lock (_submissionGate)
        {
            if (DateTimeOffset.UtcNow - _lastSubmissionUtc < TimeSpan.FromSeconds(5))
                return new ServiceIpcResponse(request.Id, false, "RateLimited",
                    Message: "Wait a few seconds before sending another support request.");
            _lastSubmissionUtc = DateTimeOffset.UtcNow;
        }

        var normalized = draft with
        {
            Subject = Normalize(draft.Subject, 120),
            MessageText = Normalize(draft.MessageText, 8_000),
            TextDirection = draft.TextDirection is "rtl" or "ltr" ? draft.TextDirection : "auto",
            LogExcerpts = draft.LogExcerpts.Take(20).Select(item => item with
            {
                Message = Normalize(item.Message, 2_000)
            }).ToArray(),
            IbsngDiagnostics = NormalizeEvidence(draft.IbsngDiagnostics)
        };

        var receipt = await worker.SubmitSupportRequestAsync(normalized, GetCallerName(pipe),
            cancellationToken);
        return receipt.Accepted
            ? Success(request.Id, "Submitted", receipt)
            : new ServiceIpcResponse(request.Id, false, "SupportUnavailable",
                JsonSerializer.SerializeToElement(receipt), receipt.Message);
    }

    private static ServiceIpcResponse Success<T>(string id, string state, T payload) =>
        new(id, true, state, JsonSerializer.SerializeToElement(payload));

    private static ServiceIpcResponse AccessDenied(string id) =>
        new(id, false, "AdministratorRequired",
            Message: "Administrator approval is required for this action.");

    private static string Normalize(string value, int maximum) =>
        new string(value.Where(character => character is '\r' or '\n' or '\t' || !char.IsControl(character))
            .Take(maximum).ToArray()).Trim();

    private static IbsngSupportEvidence? NormalizeEvidence(IbsngSupportEvidence? evidence)
    {
        if (evidence is null) return null;
        return evidence with
        {
            ServerHost = Normalize(evidence.ServerHost, 253),
            Summary = Normalize(evidence.Summary, 500),
            Checks = evidence.Checks.Take(20).Select(check => check with
            {
                Name = Normalize(check.Name, 100),
                Status = Normalize(check.Status, 32),
                Summary = Normalize(check.Summary, 500),
                Detail = check.Detail is null ? null : Normalize(check.Detail, 500)
            }).ToArray(),
            SanitizedLogLines = evidence.SanitizedLogLines.TakeLast(20)
                .Select(line => Normalize(line, 500)).ToArray()
        };
    }

    private static string GetCallerName(NamedPipeServerStream pipe)
    {
        var name = "Local user";
        pipe.RunAsClient(() => name = WindowsIdentity.GetCurrent().Name);
        return name;
    }

    private static bool IsCallerAdministrator(NamedPipeServerStream pipe)
    {
        var isAdministrator = false;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent();
            isAdministrator = new WindowsPrincipal(identity)
                .IsInRole(WindowsBuiltInRole.Administrator);
        });
        return isAdministrator;
    }

    private static async Task<string> ReadBoundedLineAsync(StreamReader reader,
        CancellationToken cancellationToken)
    {
        var buffer = new char[1];
        var value = new StringBuilder();
        while (value.Length <= ServiceIpcProtocol.MaxMessageCharacters)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0 || buffer[0] == '\n') return value.ToString().TrimEnd('\r');
            value.Append(buffer[0]);
        }
        throw new InvalidDataException("The local IPC request exceeded the allowed size.");
    }
}
