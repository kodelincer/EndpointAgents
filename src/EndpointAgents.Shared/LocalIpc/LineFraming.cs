using System.Text;

namespace EndpointAgents.Shared.LocalIpc;

/// <summary>Newline-delimited JSON framing used on the local pipes, with a hard size cap.</summary>
public static class LineFraming
{
    /// <summary>Reads one line, or returns null when the other side closed the stream.</summary>
    public static async Task<string?> ReadLineAsync(StreamReader reader, int maxCharacters,
        CancellationToken cancellationToken)
    {
        var buffer = new char[1];
        var value = new StringBuilder();
        while (value.Length <= maxCharacters)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0) return value.Length == 0 ? null : value.ToString().TrimEnd('\r');
            if (buffer[0] == '\n') return value.ToString().TrimEnd('\r');
            value.Append(buffer[0]);
        }
        throw new InvalidDataException("A local message exceeded the allowed size.");
    }
}
