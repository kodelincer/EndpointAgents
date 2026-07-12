namespace EndpointAgents.Core.Commands;

public sealed record CommandExecutionContext(
    string AgentId,
    string? RequestedBy,
    string? TicketId,
    DateTimeOffset ReceivedAtUtc);

/*
 * Later this context can include helpdesk user id, ticket number, approval id,
 * risk level, and tenant/organization information for auditing and security.
 */
