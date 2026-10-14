"use client";

import { Check, CircleAlert, Copy, KeyRound, RefreshCw, ShieldCheck, UserPlus, UserRoundX, UserRoundCheck, X } from "lucide-react";
import { useCallback, useEffect, useState } from "react";
import { useAuth } from "@/components/auth-gate";
import { addDirectoryOperator, createOperator, getAuthOptions, listOperators, ROLES, updateOperator,
  type OperatorAccount, type OperatorRole } from "@/lib/auth";

type Handover = { userName: string; displayName: string; password: string; reason: "created" | "reset" };

const formatter = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" });

/** Admin page: who can sign in to the dashboard, and with which role. */
export function Operators() {
  const { session, can } = useAuth();
  const [items, setItems] = useState<OperatorAccount[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState<string>();
  const [adding, setAdding] = useState(false);
  const [handover, setHandover] = useState<Handover>();
  const [directory, setDirectory] = useState(false);
  useEffect(() => { getAuthOptions().then(options => setDirectory(options.directory)).catch(() => undefined); }, []);

  const refresh = useCallback(async () => {
    setLoading(true);
    try {
      setItems(await listOperators());
      setError(undefined);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "Operators could not be loaded.");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    const initial = window.setTimeout(() => void refresh(), 0);
    return () => window.clearTimeout(initial);
  }, [refresh]);

  if (!can("users.manage"))
    return <div className="file-page"><div className="file-empty"><h2>Admins only</h2>
      <p>Ask an admin if you need an account created or a role changed.</p></div></div>;

  const change = async (item: OperatorAccount,
    patch: { role?: OperatorRole; disabled?: boolean; resetPassword?: boolean }) => {
    setBusy(item.userId);
    try {
      const result = await updateOperator(item.userId, patch);
      setItems(current => current.map(entry => entry.userId === item.userId ? result.user : entry));
      if (result.temporaryPassword)
        setHandover({ userName: item.userName, displayName: item.displayName,
          password: result.temporaryPassword, reason: "reset" });
      setError(undefined);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "The change was not saved.");
    } finally {
      setBusy(undefined);
    }
  };

  return <div className="file-page operators-page">
    <section className="file-heading" aria-labelledby="operators-title">
      <div><div className="file-heading-mark"><ShieldCheck size={17} /></div><div>
        <h1 id="operators-title">Operators</h1>
        <p>Who can sign in to this dashboard, and what their role lets them do.</p></div></div>
      <div className="operators-actions">
        <button className="secondary-button" onClick={() => void refresh()} disabled={loading}>
          <RefreshCw className={loading ? "spin" : ""} size={15} />Refresh</button>
        <button className="primary-button" onClick={() => setAdding(true)} disabled={adding}>
          <UserPlus size={15} />Add operator</button>
      </div>
    </section>

    <dl className="role-ledger">
      {ROLES.map(item => <div key={item.role}>
        <dt><RoleChip role={item.role} /><span lang="fa" dir="rtl">{item.persian}</span></dt>
        <dd>{item.summary}</dd>
      </div>)}
    </dl>

    {error && <div className="inline-error" role="alert"><CircleAlert size={17} />
      <div><strong>{error}</strong></div><button onClick={() => setError(undefined)}>Dismiss</button></div>}

    {handover && <HandoverNote handover={handover} onClose={() => setHandover(undefined)} />}

    {adding && <AddOperator directory={directory} onCancel={() => setAdding(false)} onAdded={user => {
      setItems(current => [...current, user]);
      setAdding(false);
    }} onCreated={(user, password) => {
      setItems(current => [...current, user]);
      setAdding(false);
      setHandover({ userName: user.userName, displayName: user.displayName, password, reason: "created" });
    }} />}

    {loading && items.length === 0 ? <div className="skeleton-list">
      {[0, 1, 2].map(index => <div className="skeleton-row" key={index} />)}</div> :
    <div className="agent-table-wrap">
      <table className="agent-table operators-table">
        <thead><tr><th>Operator</th><th>Account</th><th>Role</th><th>Status</th><th>Last sign-in</th><th><span className="sr-only">Actions</span></th></tr></thead>
        <tbody>{items.map(item => {
          const self = item.userId === session.userId;
          return <tr key={item.userId} className={item.disabled ? "is-disabled" : ""}>
            <td data-label="Operator"><span className="operator-identity">
              <strong dir="auto">{item.displayName}{self && <span className="you-tag">you</span>}</strong>
              <code>{item.userName}</code></span></td>
            <td data-label="Account">{item.source === "directory"
              ? <span className="table-status success">Active Directory</span> : <span className="muted">Local</span>}</td>
            <td data-label="Role">
              <select className="role-select" value={item.role} aria-label={`Role for ${item.userName}`}
                disabled={busy === item.userId || self || item.disabled}
                title={self ? "You can't change your own role." : undefined}
                onChange={event => void change(item, { role: event.target.value as OperatorRole })}>
                {ROLES.map(role => <option key={role.role} value={role.role}>{role.name}</option>)}
              </select></td>
            <td data-label="Status"><AccountStatus item={item} /></td>
            <td data-label="Last sign-in" className="muted">
              {item.lastSignInAtUtc ? <time dateTime={item.lastSignInAtUtc}>{formatter.format(new Date(item.lastSignInAtUtc))}</time> : "Never"}</td>
            <td className="operator-row-actions">
              {item.source !== "directory" && <button className="secondary-button" disabled={busy === item.userId || item.disabled}
                onClick={() => void change(item, { resetPassword: true })}>
                <KeyRound size={14} />Reset password</button>}
              {!self && <button className="secondary-button" disabled={busy === item.userId}
                onClick={() => void change(item, { disabled: !item.disabled })}>
                {item.disabled ? <><UserRoundCheck size={14} />Enable</> : <><UserRoundX size={14} />Disable</>}</button>}
            </td>
          </tr>;
        })}</tbody>
      </table>
    </div>}
  </div>;
}

function RoleChip({ role }: { role: OperatorRole }) {
  return <span className={`role-chip ${role}`}>{ROLES.find(item => item.role === role)?.name}</span>;
}

function AccountStatus({ item }: { item: OperatorAccount }) {
  if (item.disabled) return <span className="table-status error">Disabled</span>;
  if (item.mustChangePassword) return <span className="table-status pending">Waiting for first sign-in</span>;
  return <span className="table-status success">Active</span>;
}

function AddOperator({ directory, onCancel, onCreated, onAdded }: {
  directory: boolean; onCancel: () => void; onCreated: (user: OperatorAccount, password: string) => void;
  onAdded: (user: OperatorAccount) => void;
}) {
  const [fromDirectory, setFromDirectory] = useState(false);
  const [userName, setUserName] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [role, setRole] = useState<OperatorRole>("member");
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setBusy(true);
    try {
      if (fromDirectory) onAdded((await addDirectoryOperator(userName, role)).user);
      else {
        const result = await createOperator(userName, displayName, role);
        onCreated(result.user, result.temporaryPassword);
      }
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "The operator was not created.");
    } finally {
      setBusy(false);
    }
  };

  return <form className="operator-form" onSubmit={event => void submit(event)} aria-labelledby="add-operator-title">
    <header><h2 id="add-operator-title">Add operator</h2>
      <button type="button" className="icon-button" onClick={onCancel} aria-label="Close"><X size={16} /></button></header>
    <div className="operator-form-fields">
      {directory && <label><span>Account type</span>
        <select value={fromDirectory ? "directory" : "local"} onChange={event => setFromDirectory(event.target.value === "directory")}>
          <option value="local">Local account (password made here)</option>
          <option value="directory">Active Directory user (signs in with their domain password)</option>
        </select></label>}
      <label><span>User name</span>
        <input required dir="ltr" autoCapitalize="none" spellCheck={false} autoFocus value={userName}
          pattern={fromDirectory ? undefined : "[A-Za-z0-9._\\-]{3,32}"} placeholder={fromDirectory ? "domain user name, e.g. s.karimi" : "e.g. s.karimi"}
          onChange={event => setUserName(event.target.value)} />
        <small>3-32 letters, digits, dot, dash or underscore.</small></label>
      {!fromDirectory && <label><span>Display name</span>
        <input required dir="auto" maxLength={60} value={displayName} placeholder="سارا کریمی"
          onChange={event => setDisplayName(event.target.value)} />
        <small>Shown to other operators. Persian is fine.</small></label>}
      <label><span>Role</span>
        <select value={role} onChange={event => setRole(event.target.value as OperatorRole)}>
          {ROLES.map(item => <option key={item.role} value={item.role}>{item.name}</option>)}
        </select>
        <small>{ROLES.find(item => item.role === role)?.summary}</small></label>
    </div>
    {error && <p className="command-error" role="alert"><CircleAlert size={14} />{error}</p>}
    <footer>
      <span>{fromDirectory ? "The name is checked in Active Directory. No password is stored here."
        : "A one-time password is created. They choose their own at first sign-in."}</span>
      <button type="button" className="secondary-button" onClick={onCancel}>Cancel</button>
      <button type="submit" className="primary-button" disabled={busy}>
        {busy ? <RefreshCw className="spin" size={15} /> : <UserPlus size={15} />}Create operator</button>
    </footer>
  </form>;
}

function HandoverNote({ handover, onClose }: { handover: Handover; onClose: () => void }) {
  const [copied, setCopied] = useState(false);
  const copy = async () => {
    await navigator.clipboard.writeText(`${handover.userName}\n${handover.password}`);
    setCopied(true);
  };

  return <section className="handover" role="status" aria-labelledby="handover-title">
    <div>
      <h2 id="handover-title">{handover.reason === "created" ? "Operator created" : "Password reset"}
        {" for "}<bdi>{handover.displayName}</bdi></h2>
      <p>Give them this one-time password in person or over a trusted channel. It is shown only now;
        they replace it the first time they sign in.</p>
    </div>
    <dl>
      <div><dt>User name</dt><dd><code>{handover.userName}</code></dd></div>
      <div><dt>One-time password</dt><dd><code className="handover-secret">{handover.password}</code></dd></div>
    </dl>
    <div className="handover-actions">
      <button className="secondary-button" onClick={() => void copy()}>
        {copied ? <><Check size={14} />Copied</> : <><Copy size={14} />Copy both</>}</button>
      <button className="secondary-button" onClick={onClose}>Done</button>
    </div>
  </section>;
}
