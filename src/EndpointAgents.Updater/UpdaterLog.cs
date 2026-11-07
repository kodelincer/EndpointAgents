namespace EndpointAgents.Updater;

/// <summary>A plain text log next to the state file (C:\ProgramData\EndpointAgents\Updates\updater.log).</summary>
internal sealed class UpdaterLog : IDisposable
{
    private readonly StreamWriter? _writer;

    public UpdaterLog(string path)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                File.Move(path, path + ".old", overwrite: true);
            _writer = new StreamWriter(path, append: true) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Write(string message)
    {
        try { _writer?.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}"); }
        catch (IOException) { }
    }

    public void Dispose() => _writer?.Dispose();
}
