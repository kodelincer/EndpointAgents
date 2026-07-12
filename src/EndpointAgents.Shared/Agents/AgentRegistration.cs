namespace EndpointAgents.Shared.Agents;

public sealed record AgentRegistration(
    string AgentId,
    string Hostname,
    string MachineName,
    string UserName,
    string Version,
    DateTimeOffset StartedAtUtc);