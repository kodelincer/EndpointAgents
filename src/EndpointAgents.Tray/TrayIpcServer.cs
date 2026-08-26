using EndpointAgents.Shared.LocalIpc;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace EndpointAgents.Tray;

public sealed class TrayIpcServer
{
    private readonly string _pipeName;
    private readonly IbsngDialerLauncher _launcher = new();
    private readonly OfficeRemediationLauncher _officeRemediation = new();
    private readonly WindowsRemediationConsent _windowsRemediation = new();
    private readonly WindowsHelloLauncher _windowsHello = new();

    public TrayIpcServer(int sessionId, string pipePrefix = TrayIpcProtocol.DefaultPipePrefix)
    {
        _pipeName = TrayIpcProtocol.GetPipeName(pipePrefix, sessionId);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(cancellationToken);
                await HandleConnectionAsync(pipe, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                await Task.Delay(500, cancellationToken);
            }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User
            ?? throw new InvalidOperationException("The Tray process has no Windows user SID.");
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(userSid);
        security.AddAccessRule(new PipeAccessRule(userSid, PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(systemSid, PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
        TrayIpcResponse response;
        TrayIpcRequest? request = null;
        try
        {
            var line = await ReadBoundedLineAsync(reader, cancellationToken);
            request = JsonSerializer.Deserialize<TrayIpcRequest>(line)
                ?? throw new InvalidDataException("The Tray IPC request was empty.");
            TrayEventLogger.Information(
                $"درخواست محلی '{request.Method}' با شناسه {request.Id} دریافت شد.");
            response = request.Method switch
            {
                TrayIpcProtocol.OfficeRemediationLaunchMethod =>
                    await _officeRemediation.LaunchAsync(request, cancellationToken),
                TrayIpcProtocol.OfficeBugfixConsentMethod =>
                    _officeRemediation.ConfirmBugfix(request),
                TrayIpcProtocol.WindowsBugfixConsentMethod =>
                    _windowsRemediation.Confirm(request),
                TrayIpcProtocol.WindowsHelloLaunchMethod =>
                    await _windowsHello.LaunchAsync(request, cancellationToken),
                _ => await _launcher.LaunchAsync(request, cancellationToken)
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TrayEventLogger.Error("اجرای درخواست محلی برنامه ناحیه اعلان ناموفق بود: " +
                $"{ex.GetType().Name}: {ex.Message}");
            response = new TrayIpcResponse(string.Empty, false, "InvalidRequest",
                Message: ex.GetType().Name);
        }

        if (request is not null)
        {
            var message = $"درخواست محلی '{request.Method}' با شناسه {request.Id} " +
                $"با وضعیت {response.State} پایان یافت.";
            if (response.Success) TrayEventLogger.Information(message);
            else TrayEventLogger.Warning(message);
        }

        await writer.WriteLineAsync(JsonSerializer.Serialize(response).AsMemory(), cancellationToken);
    }

    private static async Task<string> ReadBoundedLineAsync(StreamReader reader,
        CancellationToken cancellationToken)
    {
        var buffer = new char[1];
        var value = new StringBuilder();
        while (value.Length <= TrayIpcProtocol.MaxMessageCharacters)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0 || buffer[0] == '\n')
                return value.ToString().TrimEnd('\r');
            value.Append(buffer[0]);
        }
        throw new InvalidDataException("The Tray request exceeded the allowed size.");
    }
}
