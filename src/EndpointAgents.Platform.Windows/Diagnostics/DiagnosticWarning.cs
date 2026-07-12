namespace EndpointAgents.Platform.Windows.Diagnostics;

public sealed record DiagnosticWarning(
    string Source,
    string Message,
    string? Exception = null);
