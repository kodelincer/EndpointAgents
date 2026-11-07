using EndpointAgents.Shared.Problems;
using EndpointAgents.Shared.Support;

namespace EndpointAgents.Service;

public sealed class EndpointActivityLoggerProvider(ProblemOutbox? problems = null, string? agentVersion = null) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ActivityLogger(categoryName, problems, agentVersion);

    public void Dispose() { }

    private sealed class ActivityLogger(string categoryName, ProblemOutbox? problems, string? agentVersion) : ILogger
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
            // An error our code logged: the Problems page counts it. Text is cleaned first.
            if (logLevel >= LogLevel.Error && problems is not null && exception is not OperationCanceledException)
                problems.Add(exception is not null
                    ? ProblemText.FromException(ProblemKinds.LoggedError, _source, exception, agentVersion,
                        DateTimeOffset.UtcNow, formatter(state, null))
                    : ProblemText.FromMessage(ProblemKinds.LoggedError, _source, formatter(state, null), agentVersion,
                        DateTimeOffset.UtcNow));
        }

        private static string LevelName(LogLevel level) => level switch
        {
            LogLevel.Warning => "Warning",
            LogLevel.Error or LogLevel.Critical => "Error",
            _ => "Information"
        };
    }
}
