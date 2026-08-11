import type { HelpdeskConversationDetail, HelpdeskConversationSummary } from "./types";

const statusOrder = { New: 0, Open: 1, Resolved: 2 } as const;

export function mergeHelpdeskConversations(current: HelpdeskConversationSummary[],
  incoming: HelpdeskConversationSummary[]) {
  void current; // The REST snapshot is authoritative; live changes only trigger its refresh.
  const byId = new Map<string, HelpdeskConversationSummary>();
  incoming.forEach(item => byId.set(item.conversationId, item));
  return [...byId.values()].sort((a, b) => statusOrder[a.status] - statusOrder[b.status]
    || b.updatedAtUtc.localeCompare(a.updatedAtUtc)).slice(0, 200);
}

export function mergeHelpdeskDetail(current: HelpdeskConversationDetail | undefined,
  incoming: HelpdeskConversationDetail) {
  if (!current || current.conversation.conversationId !== incoming.conversation.conversationId)
    return incoming;
  const messages = new Map(current.messages.map(item => [item.messageId, item]));
  incoming.messages.forEach(item => messages.set(item.messageId, item));
  const trace = new Map(current.trace.map(item => [item.eventId, item]));
  incoming.trace.forEach(item => trace.set(item.eventId, item));
  return {
    ...incoming,
    messages: [...messages.values()].sort((a, b) => a.sequence - b.sequence),
    trace: [...trace.values()].sort((a, b) => a.timestampUtc.localeCompare(b.timestampUtc))
  };
}
