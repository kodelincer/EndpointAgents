"use client";

import { CircleAlert, FileArchive, LayoutDashboard, LifeBuoy, RefreshCw, Server, Waypoints } from "lucide-react";
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

  return <main className="app-shell">
    <header className="topbar">
      <Link className="brand" href="/" aria-label="EndpointAgents operations overview">
        <span className="brand-mark"><Server size={17} /></span>
        <span className="brand-copy"><strong>EndpointAgents</strong><span>Control plane</span></span>
      </Link>
      <nav className="primary-nav" aria-label="Control plane">
        <Link className={path === "/" ? "active" : ""} href="/"><LayoutDashboard size={15} />Overview</Link>
        <Link className={path.startsWith("/agents") ? "active" : ""} href="/agents/"><Waypoints size={15} />Agents</Link>
        <Link className={path.startsWith("/files") ? "active" : ""} href="/files/"><FileArchive size={15} />Files</Link>
        <Link className={path.startsWith("/support") ? "active" : ""} href="/support/"><LifeBuoy size={15} />Helpdesk{supportRequests.length > 0 && <span className="nav-count">{supportRequests.length}</span>}</Link>
      </nav>
      <div className="topbar-status">
        <span className={`connection-state ${connection}`}><span className="state-dot" />{statusLabel(connection)}</span>
        <span className="topbar-divider" /><span>{agents.length} agent{agents.length === 1 ? "" : "s"} online</span>
        <button className="icon-button" onClick={() => void refresh()} aria-label="Refresh control-plane data"><RefreshCw size={16} /></button>
      </div>
    </header>

    {connection !== "live" && <div className="connection-banner" role="status"><CircleAlert size={16} />
      {connection === "reconnecting" ? "Live updates paused. Reconnecting and preserving the current view…" :
       connection === "connecting" ? "Connecting to live control-plane updates…" :
       "Live updates are unavailable. Service controls remain available from the overview."}</div>}
    {children}
  </main>;
}
