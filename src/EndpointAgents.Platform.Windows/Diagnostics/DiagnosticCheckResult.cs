namespace EndpointAgents.Platform.Windows.Diagnostics;

public sealed record DiagnosticCheckResult(
    string Name,
    string Status,
    object? Details,
    long DurationMs,
    object? Error = null);
