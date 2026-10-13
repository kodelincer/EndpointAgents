import { apiFetch } from "./control-plane";

export type OperatorRole = "member" | "reviewer" | "admin";

export type OperatorSession = {
  userId: string; userName: string; displayName: string; role: OperatorRole;
  mustChangePassword: boolean; permissions: string[];
};

export type OperatorAccount = {
  userId: string; userName: string; displayName: string; role: OperatorRole;
  disabled: boolean; mustChangePassword: boolean; createdAtUtc: string; lastSignInAtUtc: string | null;
};

/** What each role can do, mirrored from OperatorRoles on the API (which enforces it). */
export const ROLES: { role: OperatorRole; name: string; persian: string; summary: string }[] = [
  { role: "member", name: "Member", persian: "کارشناس پشتیبانی",
    summary: "Answers helpdesk conversations, runs read-only diagnostics, drafts skills." },
  { role: "reviewer", name: "Reviewer", persian: "کارشناس ارشد",
    summary: "Everything a member does, plus triaging feature requests and publishing skills." },
  { role: "admin", name: "Admin", persian: "مدیر سیستم",
    summary: "Everything, plus endpoint commands, maintenance, devices and operator accounts." }
];

export const roleName = (role: OperatorRole) => ROLES.find(item => item.role === role)?.name ?? role;

export class AuthError extends Error {
  constructor(message: string, public status: number) { super(message); }
}

/** Persian messages for the sign-in screens; the API's own messages are English. */
export function signInMessage(status: number, apiMessage?: string): string {
  if (status === 401) return "نام کاربری یا رمز عبور درست نیست.";
  if (status === 429) return "چند بار پشت سر هم اشتباه وارد شد. پانزده دقیقه دیگر دوباره امتحان کنید.";
  if (status === 400 && apiMessage?.startsWith("Current password"))
    return "رمز فعلی درست نیست.";
  if (status === 400 && apiMessage?.startsWith("Password must have at least"))
    return "رمز تازه باید دست‌کم ۱۰ نویسه باشد.";
  if (status === 400 && apiMessage?.startsWith("Choose a password"))
    return "رمز تازه باید با رمز فعلی فرق داشته باشد.";
  if (status === 0) return "سرور در دسترس نیست. روشن بودن سرویس کنترل‌پلین را بررسی کنید.";
  return apiMessage ?? "کار انجام نشد. دوباره امتحان کنید.";
}

async function send<T>(path: string, method: string, body?: unknown): Promise<T> {
  let response: Response;
  try {
    response = await apiFetch(path, {
      method, headers: body === undefined ? undefined : { "Content-Type": "application/json" },
      body: body === undefined ? undefined : JSON.stringify(body)
    });
  } catch {
    throw new AuthError("The control plane is unreachable.", 0);
  }
  if (!response.ok) {
    const error = await response.json().catch(() => ({})) as { message?: string };
    throw new AuthError(error.message ?? `Control plane returned ${response.status}.`, response.status);
  }
  return response.status === 204 ? undefined as T : response.json() as Promise<T>;
}

export const getSession = () => send<OperatorSession>("/api/auth/session", "GET");
export const signIn = (userName: string, password: string) =>
  send<OperatorSession>("/api/auth/sign-in", "POST", { userName, password });
export const signOut = () => send<void>("/api/auth/sign-out", "POST");
export const changePassword = (currentPassword: string, newPassword: string) =>
  send<OperatorSession>("/api/auth/password", "POST", { currentPassword, newPassword });

export const listOperators = () => send<OperatorAccount[]>("/api/operators/", "GET");
export const createOperator = (userName: string, displayName: string, role: OperatorRole) =>
  send<{ user: OperatorAccount; temporaryPassword: string }>("/api/operators/", "POST",
    { userName, displayName, role });
export const updateOperator = (userId: string,
  patch: { displayName?: string; role?: OperatorRole; disabled?: boolean; resetPassword?: boolean }) =>
  send<{ user: OperatorAccount; temporaryPassword: string | null }>(
    `/api/operators/${encodeURIComponent(userId)}`, "PATCH", patch);
