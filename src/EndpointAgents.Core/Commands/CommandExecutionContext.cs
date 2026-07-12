using static System.Runtime.InteropServices.JavaScript.JSType;

namespace EndpointAgents.Core.Commands;

public sealed record CommandExecutionContext(
    string AgentId,
    string? RequestedBy,
    string? TicketId,
    DateTimeOffset ReceivedAtUtc);

/*
 * Later this context can include:
 *  helpdesk user id
 *  ticket number
 *  approval id
 *  risk level
 *  tenant/organization

- That is important for audit and security.
*/