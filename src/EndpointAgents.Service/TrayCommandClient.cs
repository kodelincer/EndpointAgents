using EndpointAgents.Platform.Windows.Ibsng;
using EndpointAgents.Shared.LocalIpc;
using Microsoft.Extensions.Options;
using System.IO.Pipes;
using System.Text.Json;

namespace EndpointAgents.Service;

public sealed class TrayCommandClient : ITrayCommandClient
{
    private readonly IbsngOptions _options;

    public TrayCommandClient(IOptions<IbsngOptions> options)
    {
        _options = options.Value;
    }

    public async Task<TrayIpcResponse> SendAsync(int sessionId, TrayIpcRequest request,
        CancellationToken cancellationToken)
    {
        var pipeName = TrayIpcProtocol.GetPipeName(_options.TrayPipePrefix, sessionId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TrayConnectTimeoutSeconds));

        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);

            using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            responseTimeout.CancelAfter(TimeSpan.FromSeconds(_options.TrayResponseTimeoutSeconds));
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(),
                responseTimeout.Token);
            var line = await ReadBoundedLineAsync(reader, responseTimeout.Token);
            var response = JsonSerializer.Deserialize<TrayIpcResponse>(line);
            if (response is null || response.Id != request.Id)
                return new TrayIpcResponse(request.Id, false, "InvalidTrayResponse",
                    Message: "The Tray returned an empty or mismatched response.");
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new TrayIpcResponse(request.Id, false, "TrayUnavailable",
                Message: "The active user's Tray did not respond before the timeout.");
        }
        catch (IOException ex)
        {
            return new TrayIpcResponse(request.Id, false, "TrayUnavailable", Message: ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new TrayIpcResponse(request.Id, false, "TrayAccessDenied", Message: ex.Message);
        }
    }

    private static async Task<string> ReadBoundedLineAsync(StreamReader reader,
        CancellationToken cancellationToken)
    {
        var buffer = new char[1];
        var value = new System.Text.StringBuilder();
        while (value.Length <= TrayIpcProtocol.MaxMessageCharacters)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0 || buffer[0] == '\n')
                return value.ToString().TrimEnd('\r');
            value.Append(buffer[0]);
        }
        throw new InvalidDataException("The Tray response exceeded the allowed size.");
    }
}
