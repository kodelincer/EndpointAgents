namespace ControlPlane.Api.Chat;

public sealed class ChatOptions
{
    public string DatabasePath { get; set; } =
        @"C:\ProgramData\EndpointAgents\ControlPlane\control-plane.db";

    public int RetentionDays { get; set; } = 30;

    public int MaxVisibleItems { get; set; } = 30;
}

public sealed class AgentSecurityOptions
{
    /// <summary>
    /// Only enrolled PCs (with a device token) may connect. On by default: a PC gets its token
    /// at install from a one-time enrollment code (dashboard page Endpoint (PC) inventory, New enrollment code).
    /// </summary>
    public bool RequireAuthentication { get; set; } = true;

    public bool RequireHttps { get; set; } = true;

    public int EnrollmentLifetimeMinutes { get; set; } = 30;
}

public sealed class HermesOptions
{
    public bool Enabled { get; set; }

    public string BaseUrl { get; set; } = "http://127.0.0.1:8642";

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public string McpApiKey { get; set; } = string.Empty;

    public int MaxConcurrentRuns { get; set; } = 4;

    public int RunTimeoutSeconds { get; set; } = 900;

    public int PollIntervalMilliseconds { get; set; } = 1_000;

    public int MaxHistoryMessages { get; set; } = 5;
}
