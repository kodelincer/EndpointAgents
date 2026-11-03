/** The Lessons queue: what solved chats teach, waiting for a person to decide. */
import { apiFetch } from "./control-plane";

export type LessonStatus = "New" | "Kept" | "Drafted" | "Dropped";
export type Lesson = {
  lessonId: string; conversationId: string; agentId: string; machineName: string; symptom: string;
  tried: { actionId: string; name: string; status: string; summary: string }[]; fixedBy?: string | null;
  skills: string[]; resolvedAtUtc: string; status: LessonStatus; note: string; decidedBy?: string | null;
};
export type LessonPage = { lessons: Lesson[]; fixes: { fixedBy: string; solved: number }[] };

export const statusText: Record<LessonStatus, string> = {
  New: "New", Kept: "Kept for analysis", Drafted: "Skill draft started", Dropped: "Dropped"
};

/** "Spooler restarted after 2 checks", or "Solved without a fix" when nothing changed the PC. */
export function outcomeLine(lesson: Pick<Lesson, "tried" | "fixedBy">) {
  if (!lesson.fixedBy) return lesson.tried.length ? `Solved without a fix after ${lesson.tried.length} check${lesson.tried.length === 1 ? "" : "s"}` : "Solved in the chat, nothing ran on the PC";
  const step = lesson.tried.find(item => item.actionId === lesson.fixedBy);
  const before = lesson.tried.findIndex(item => item.actionId === lesson.fixedBy);
  return `Fixed by ${step?.name ?? lesson.fixedBy}${before > 0 ? ` after ${before} other step${before === 1 ? "" : "s"}` : ""}`;
}

/** The Skills page starts a reviewed draft from the chat; nothing reaches the agent before a reviewer publishes it. */
export const skillDraftHref = (conversationId: string) => `/skills/?conversation=${encodeURIComponent(conversationId)}`;

export async function getLessons(status?: LessonStatus): Promise<LessonPage> {
  const response = await apiFetch(`/api/lessons${status ? `?status=${status}` : ""}`);
  if (!response.ok) throw new Error(`Control plane returned ${response.status}.`);
  return response.json() as Promise<LessonPage>;
}

export async function patchLesson(lessonId: string, patch: { status?: LessonStatus; note?: string }) {
  const response = await apiFetch(`/api/lessons/${encodeURIComponent(lessonId)}`, {
    method: "PATCH", headers: { "Content-Type": "application/json" }, body: JSON.stringify(patch)
  });
  if (!response.ok) throw new Error(response.status === 403 ? "Only reviewers can decide about lessons." : `Control plane returned ${response.status}.`);
}
