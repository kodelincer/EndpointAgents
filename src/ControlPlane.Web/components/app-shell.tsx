"use client";

import { BookMarked, BookOpen, ChevronDown, CircleAlert, ClipboardCheck, KeyRound, LayoutDashboard, LifeBuoy, Lightbulb, LogOut, Map as MapIcon, Package, RefreshCw, Server, ShieldCheck, Sparkles, Waypoints } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { useAuth } from "@/components/auth-gate";
import { roleName } from "@/lib/auth";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useControlPlane } from "@/lib/control-plane-context";
import type { ConnectionState } from "@/lib/types";

function statusLabel(state: ConnectionState) {
  return { connecting: "Connecting", live: "Live", reconnecting: "Reconnecting", offline: "Unavailable" }[state];
}

/** Provides persistent navigation and connection feedback across operations pages. */
export function AppShell({ children }: { children: React.ReactNode }) {
  const path = usePathname();
  const { agents, supportRequests, connection, refresh } = useControlPlane();
  const { can } = useAuth();

  return <main className="app-shell">
    <header className="topbar">
      <Link className="brand" href="/" aria-label="EndpointAgents operations overview">
        <span className="brand-mark"><Server size={17} /></span>
        <span className="brand-copy"><strong>EndpointAgents</strong><span>Control plane</span></span>
      </Link>
      <nav className="primary-nav" aria-label="Control plane">
        <Link className={path === "/" ? "active" : ""} href="/"><LayoutDashboard size={15} />Overview</Link>
        <Link className={path.startsWith("/agents") ? "active" : ""} href="/agents/"><Waypoints size={15} />Endpoints</Link>
        <Link className={path.startsWith("/files") ? "active" : ""} href="/files/"><Package size={15} />Packages</Link>
        <Link className={path.startsWith("/support") ? "active" : ""} href="/support/"><LifeBuoy size={15} />Helpdesk{supportRequests.length > 0 && <span className="nav-count">{supportRequests.length}</span>}</Link>
        <Link className={path.startsWith("/approvals") ? "active" : ""} href="/approvals/"><ClipboardCheck size={15} />Approvals</Link>
        <Link className={path.startsWith("/feature-requests") ? "active" : ""} href="/feature-requests/"><Lightbulb size={15} />Feature requests</Link>
        <Link className={path.startsWith("/capabilities") ? "active" : ""} href="/capabilities/"><BookOpen size={15} />Capabilities</Link>
        <Link className={path.startsWith("/skills") ? "active" : ""} href="/skills/"><Sparkles size={15} />Skills</Link>
        <Link className={path.startsWith("/environment") ? "active" : ""} href="/environment/"><MapIcon size={15} />Environment</Link>
        {can("users.manage") && <Link className={path.startsWith("/operators") ? "active" : ""} href="/operators/"><ShieldCheck size={15} />Operators</Link>}
      </nav>
      <div className="topbar-status">
        <span className={`connection-state ${connection}`}><span className="state-dot" />{statusLabel(connection)}</span>
        <span className="topbar-divider" /><span>{agents.length} agent{agents.length === 1 ? "" : "s"} online</span>
        <button className="icon-button" onClick={() => void refresh()} aria-label="Refresh control-plane data"><RefreshCw size={16} /></button>
        <Link className={`icon-button docs-link${path.startsWith("/docs") ? " active" : ""}`} href="/docs/" aria-label="Documentation"
          title="Documentation / راهنما" aria-current={path.startsWith("/docs") ? "page" : undefined}><BookMarked size={16} /><span>Docs</span></Link>
        <span className="topbar-divider" /><AccountMenu />
      </div>
    </header>

    {connection !== "live" && <div className="connection-banner" role="status"><CircleAlert size={16} />
      {connection === "reconnecting" ? "Live updates paused. Reconnecting and preserving the current view…" :
       connection === "connecting" ? "Connecting to live control-plane updates…" :
       "Live updates are unavailable. Service controls remain available from the overview."}</div>}
    {children}
  </main>;
}

/** Who is signed in, with password change and sign-out. */
function AccountMenu() {
  const { session, signOut, changePassword } = useAuth();
  const [open, setOpen] = useState(false);
  const root = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const close = (event: MouseEvent | KeyboardEvent) => {
      if (event instanceof KeyboardEvent ? event.key === "Escape"
        : !root.current?.contains(event.target as Node)) setOpen(false);
    };
    window.addEventListener("mousedown", close);
    window.addEventListener("keydown", close);
    return () => { window.removeEventListener("mousedown", close); window.removeEventListener("keydown", close); };
  }, [open]);

  return <div className="account" ref={root}>
    <button className="account-button" aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen(value => !value)}>
      <span className="account-avatar" aria-hidden="true">{[...session.displayName.trim()][0]?.toUpperCase() ?? "?"}</span>
      <span className="account-copy"><strong dir="auto">{session.displayName}</strong><span>{roleName(session.role)}</span></span>
      <ChevronDown size={14} />
    </button>
    {open && <div className="account-menu" role="menu">
      <span className="menu-label">Signed in as <code>{session.userName}</code></span>
      {session.source !== "directory" && <button role="menuitem" onClick={() => { setOpen(false); changePassword(); }}><KeyRound size={15} /><strong>Change password</strong></button>}
      <button role="menuitem" onClick={() => void signOut()}><LogOut size={15} /><strong>Sign out</strong></button>
    </div>}
  </div>;
}
