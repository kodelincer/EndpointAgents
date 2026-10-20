"use client";

import { CircleAlert, Eye, EyeOff, KeyRound, LoaderCircle, LockKeyhole, Server, ShieldCheck, UserRoundX } from "lucide-react";
import { createContext, useCallback, useContext, useEffect, useMemo, useState } from "react";
import { AuthError, changePassword, getAuthOptions, getSession, signIn, signInMessage, signOut,
  type OperatorSession } from "@/lib/auth";
import { SIGNED_OUT_EVENT } from "@/lib/control-plane";
import { SignInArt } from "./sign-in-art";

type AuthContextValue = {
  session: OperatorSession;
  can: (permission: string) => boolean;
  signOut: () => Promise<void>;
  changePassword: () => void;
};

const AuthContext = createContext<AuthContextValue | undefined>(undefined);

/** Shows the sign-in screen until the API confirms an operator session, then the dashboard. */
export function AuthGate({ children }: { children: React.ReactNode }) {
  const [session, setSession] = useState<OperatorSession | null>();
  const [changing, setChanging] = useState(false);

  useEffect(() => {
    let active = true;
    getSession().then(value => { if (active) setSession(value); })
      .catch(() => { if (active) setSession(null); });
    const expired = () => setSession(null);
    window.addEventListener(SIGNED_OUT_EVENT, expired);
    return () => { active = false; window.removeEventListener(SIGNED_OUT_EVENT, expired); };
  }, []);

  const leave = useCallback(async () => {
    await signOut().catch(() => undefined);
    setSession(null);
  }, []);

  const value = useMemo(() => session ? {
    session,
    can: (permission: string) => session.permissions.includes(permission),
    signOut: leave,
    changePassword: () => setChanging(true)
  } : undefined, [session, leave]);

  if (session === undefined)
    return <div className="auth-loading" role="status"><LoaderCircle className="spin" size={20} />
      <span className="sr-only">Checking your session…</span></div>;
  if (session === null) return <SignInScreen onSignedIn={setSession} />;
  if (session.mustChangePassword || changing)
    return <PasswordScreen session={session} forced={session.mustChangePassword}
      onDone={next => { setSession(next); setChanging(false); }}
      onCancel={() => setChanging(false)} onSignOut={() => void leave()} />;
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const value = useContext(AuthContext);
  if (!value) throw new Error("useAuth must be used inside AuthGate.");
  return value;
}

/** The left panel: where helpdesk staff sit on the support route. */
function SupportRoute() {
  return <aside className="auth-route" aria-label="مسیر پشتیبانی">
    <div className="auth-route-brand" dir="ltr"><span className="brand-mark"><Server size={17} /></span>
      <span>EndpointAgents</span></div>
    <SignInArt />
    <ol>
      <li><strong>رایانهٔ کاربر</strong><span>مشکل را در برنامهٔ کنار ساعت ویندوز می‌نویسد.</span></li>
      <li><strong>دستیار پشتیبانی</strong><span>بررسی می‌کند و در حدی که مجاز است رفع می‌کند.</span></li>
      <li className="you"><strong>شما، در میز پشتیبانی</strong><span>گفتگوهای ارجاع‌شده را پیگیری می‌کنید و دانش دستیار را بهتر می‌کنید.</span></li>
    </ol>
    <ul className="auth-guarantees">
      <li><ShieldCheck size={15} />هر کس فقط کارهایی را می‌بیند که نقشش اجازه می‌دهد.</li>
      <li><LockKeyhole size={15} />پنج رمز اشتباه، ورود را پانزده دقیقه می‌بندد.</li>
      <li><UserRoundX size={15} />با غیرفعال شدن حساب، نشست همان لحظه بسته می‌شود.</li>
    </ul>
  </aside>;
}

function SignInScreen({ onSignedIn }: { onSignedIn: (session: OperatorSession) => void }) {
  const [userName, setUserName] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [directory, setDirectory] = useState<{ directory: boolean; domain: string }>();
  useEffect(() => { getAuthOptions().then(setDirectory).catch(() => undefined); }, []);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    if (!userName.trim() || !password) { setError("نام کاربری و رمز عبور را بنویسید."); return; }
    setBusy(true); setError(undefined);
    try {
      onSignedIn(await signIn(userName.trim(), password));
    } catch (reason) {
      const status = reason instanceof AuthError ? reason.status : 0;
      setError(signInMessage(status, reason instanceof Error ? reason.message : undefined));
      setPassword("");
    } finally {
      setBusy(false);
    }
  };

  return <main className="auth-page" dir="rtl" lang="fa">
    <section className="auth-panel" aria-labelledby="sign-in-title">
      <form className="auth-form" onSubmit={event => void submit(event)} noValidate>
        <h1 id="sign-in-title">ورود به میز پشتیبانی</h1>
        <p className="auth-lede">{directory?.directory
          ? "با حساب دامنهٔ خودتان یا حسابی که مدیر سیستم ساخته وارد شوید."
          : "با حسابی که مدیر سیستم برایتان ساخته وارد شوید."}</p>
        {error && <p className="auth-error" role="alert"><CircleAlert size={16} />{error}</p>}
        <label className="auth-field"><span>نام کاربری</span>
          <input dir="ltr" autoComplete="username" autoCapitalize="none" spellCheck={false}
            autoFocus value={userName} onChange={event => setUserName(event.target.value)}
            aria-invalid={Boolean(error)} /></label>
        <PasswordField label="رمز عبور" value={password} onChange={setPassword}
          autoComplete="current-password" invalid={Boolean(error)} />
        <button className="auth-submit" type="submit" disabled={busy}>
          {busy ? <><LoaderCircle className="spin" size={16} />در حال ورود…</> : "ورود"}</button>
        <p className="auth-note">حساب ندارید؟ از مدیر سیستم بخواهید برایتان بسازد. اولین بار که سرویس اجرا می‌شود،
          رمز یک‌بارمصرف حساب <bdi dir="ltr">admin</bdi> در فایل <bdi dir="ltr" className="auth-file">initial-admin-password.txt</bdi> کنار پایگاه داده نوشته می‌شود.</p>
      </form>
    </section>
    <SupportRoute />
  </main>;
}

function PasswordScreen({ session, forced, onDone, onCancel, onSignOut }: {
  session: OperatorSession; forced: boolean; onDone: (session: OperatorSession) => void;
  onCancel: () => void; onSignOut: () => void;
}) {
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const [repeat, setRepeat] = useState("");
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    if (next.length < 10) { setError("رمز تازه باید دست‌کم ۱۰ نویسه باشد."); return; }
    if (next !== repeat) { setError("تکرار رمز تازه با خودش یکی نیست."); return; }
    setBusy(true); setError(undefined);
    try {
      onDone(await changePassword(current, next));
    } catch (reason) {
      setError(signInMessage(reason instanceof AuthError ? reason.status : 0,
        reason instanceof Error ? reason.message : undefined));
    } finally {
      setBusy(false);
    }
  };

  return <main className="auth-page" dir="rtl" lang="fa">
    <section className="auth-panel" aria-labelledby="password-title">
      <form className="auth-form" onSubmit={event => void submit(event)} noValidate>
        <span className="auth-key"><KeyRound size={18} /></span>
        <h1 id="password-title">{forced ? "یک رمز تازه انتخاب کنید" : "تغییر رمز عبور"}</h1>
        <p className="auth-lede">{forced
          ? <>رمزی که با آن وارد شدید یک‌بارمصرف بود. برای <bdi dir="auto">{session.displayName}</bdi> رمزی بسازید که فقط خودتان بدانید.</>
          : "بعد از تغییر، نشست‌های دیگر شما بسته می‌شوند."}</p>
        {error && <p className="auth-error" role="alert"><CircleAlert size={16} />{error}</p>}
        <PasswordField label={forced ? "رمز یک‌بارمصرف" : "رمز فعلی"} value={current}
          onChange={setCurrent} autoComplete="current-password" autoFocus />
        <PasswordField label="رمز تازه" hint="دست‌کم ۱۰ نویسه؛ یک جملهٔ کوتاه به یاد ماندنی‌تر است."
          value={next} onChange={setNext} autoComplete="new-password" />
        <PasswordField label="تکرار رمز تازه" value={repeat} onChange={setRepeat}
          autoComplete="new-password" />
        <button className="auth-submit" type="submit" disabled={busy}>
          {busy ? <><LoaderCircle className="spin" size={16} />در حال ذخیره…</> : "ذخیرهٔ رمز تازه"}</button>
        <button className="auth-link" type="button" onClick={forced ? onSignOut : onCancel}>
          {forced ? "خروج" : "انصراف"}</button>
      </form>
    </section>
    <SupportRoute />
  </main>;
}

function PasswordField({ label, hint, value, onChange, autoComplete, autoFocus, invalid }: {
  label: string; hint?: string; value: string; onChange: (value: string) => void;
  autoComplete: string; autoFocus?: boolean; invalid?: boolean;
}) {
  const [visible, setVisible] = useState(false);
  const [capsLock, setCapsLock] = useState(false);
  const detectCaps = (event: React.KeyboardEvent<HTMLInputElement>) =>
    setCapsLock(event.getModifierState?.("CapsLock") ?? false);

  return <label className="auth-field"><span>{label}</span>
    <span className="auth-password">
      <input dir="ltr" type={visible ? "text" : "password"} autoComplete={autoComplete}
        autoFocus={autoFocus} value={value} onChange={event => onChange(event.target.value)}
        onKeyUp={detectCaps} onKeyDown={detectCaps} onBlur={() => setCapsLock(false)}
        aria-invalid={invalid} />
      <button type="button" onClick={() => setVisible(current => !current)}
        aria-label={visible ? "پنهان کردن رمز" : "نمایش رمز"} aria-pressed={visible}>
        {visible ? <EyeOff size={16} /> : <Eye size={16} />}</button>
    </span>
    {capsLock && <small className="auth-caps" role="status">Caps Lock روشن است.</small>}
    {hint && !capsLock && <small>{hint}</small>}
  </label>;
}
