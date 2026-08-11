using EndpointAgents.Shared.Support;

namespace EndpointAgents.Service;

public sealed class EndpointActivityLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ActivityLogger(categoryName);

    public void Dispose() { }

    private sealed class ActivityLogger(string categoryName) : ILogger
    {
        private readonly string _source = categoryName[(categoryName.LastIndexOf('.') + 1)..];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            if (exception is not null)
                message += $" ({exception.GetType().Name}: {exception.Message})";
            if (message.Length > 8_000) message = message[..8_000];
            EndpointActivityJournal.TryAppend(EndpointActivityJournal.ServicePath,
                new EndpointActivityEntry(DateTimeOffset.UtcNow, LevelName(logLevel),
                    _source, eventId.Id, message));
        }

        private static string LevelName(LogLevel level) => level switch
        {
            LogLevel.Warning => "Warning",
            LogLevel.Error or LogLevel.Critical => "Error",
            _ => "Information"
        };
    }
}
