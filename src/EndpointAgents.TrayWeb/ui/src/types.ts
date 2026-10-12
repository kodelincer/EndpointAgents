// Shapes the .NET host returns. Field names follow the host's camelCase JSON.

export type Health = "Checking" | "Ready" | "Paused" | "Stopped" | "Warning" | "Error";
export type ServiceState =
  | "Unknown" | "NotInstalled" | "Stopped" | "StartPending" | "StopPending" | "Running" | "Interactive";
export type ConnectionStatus =
  | "Starting" | "Connecting" | "Connected" | "Reconnecting" | "Disconnected" | "Faulted";

export interface RuntimeSnapshot {
  agentId: string;
  machineName: string;
  version: string;
  connectionStatus: ConnectionStatus;
  functionMode: "Active" | "Paused";
  startedAtUtc: string;
  lastConnectedAtUtc?: string;
  lastCommandAtUtc?: string;
  lastCommandMethod?: string;
  lastCommandSucceeded?: boolean;
  activeCommands: number;
  lastProblemCode?: string;
  lastProblemMessage?: string;
  lastProblemAtUtc?: string;
}

export interface TrayStatus {
  health: Health;
  service: ServiceState;
  label: string;
  detail: string;
  runtime?: RuntimeSnapshot;
}

export type ConversationState =
  | "Open" | "Working" | "AwaitingApproval" | "Answered" | "Resolved" | "Closed";

export interface ConversationSummary {
  conversationId: string;
  title: string;
  preview: string;
  textDirection: string;
  createdAtUtc: string;
  updatedAtUtc: string;
  lastSequence: number;
  state: ConversationState;
  isActive: boolean;
}

export type MessageRole = "User" | "Assistant" | "System" | "Operator";
export type MessageState = "Queued" | "Working" | "Completed" | "Failed";

export interface ChatMessage {
  messageId: string;
  conversationId: string;
  sequence: number;
  role: MessageRole;
  body: string;
  textDirection: string;
  state: MessageState;
  createdAtUtc: string;
  runId?: string;
}

export type ActionState = "Pending" | "Approved" | "Rejected" | "Expired" | "Executed" | "Failed";

export interface ActionProposal {
  actionId: string;
  conversationId: string;
  sequence: number;
  method: string;
  displayName: string;
  description: string;
  parameters?: Record<string, unknown> | null;
  createdAtUtc: string;
  expiresAtUtc: string;
  state: ActionState;
}

export type ProgressStage =
  | "Queued" | "AssistantStarted" | "WaitingForModel" | "EndpointCheckStarted"
  | "EndpointCheckCompleted" | "Retrying" | "Completed" | "Failed" | "AwaitingApproval"
  | "ActionStarted" | "ActionCompleted" | "ActionRejected";

export type ProgressActivity =
  | "None" | "OfficeInventory" | "OfficeActivationStatus" | "OfficeRuntimeStatus"
  | "OfficeActivationRepair" | "WindowsActivationStatus" | "WindowsActivationRepair"
  | "ProposedAction" | "EndpointCheck";

export interface ProgressEvent {
  eventId: string;
  conversationId: string;
  runId: string;
  sequence: number;
  stage: ProgressStage;
  activity: ProgressActivity;
  outcome: "None" | "Success" | "Failure";
  createdAtUtc: string;
}

export interface ChatDelta {
  conversation: { conversationId: string; lastSequence: number };
  messages: ChatMessage[];
  actions: ActionProposal[];
  progressEvents?: ProgressEvent[] | null;
  visibleItemLimit: number;
}

export interface MessageReceipt {
  conversationId: string;
  messageId: string;
  sequence: number;
  accepted: boolean;
  message: string;
}

export interface ActionDecisionReceipt {
  accepted: boolean;
  proposal?: ActionProposal | null;
  message: string;
  decisionMessage?: ChatMessage | null;
}

export interface LogEntry {
  timestampUtc: string;
  level: "Error" | "Warning" | "Information" | string;
  source: string;
  eventId: number;
  message: string;
}

export interface ConsentRequest {
  id: string;
  kind: string;
  title: string;
  body: string;
  detail?: string;
  approveText: string;
  declineText: string;
  caution: boolean;
}

export interface ServiceActionResult {
  success: boolean;
  message: string;
  cancelled: boolean;
}

export interface ReadyInfo {
  preview: boolean;
  userName: string;
  machineName: string;
  section?: Section | null;
  status?: TrayStatus | null;
  maximized: boolean;
  consents: ConsentRequest[];
  messageLimit: number;
}

export type Section = "chat" | "status" | "logs";
