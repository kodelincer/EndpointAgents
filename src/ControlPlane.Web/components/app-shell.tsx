"use client";

import { BookMarked, BookOpen, ChevronDown, CircleAlert, ClipboardCheck, KeyRound, LayoutDashboard, LifeBuoy, Lightbulb, LogOut, Map as MapIcon, Menu, Package, PanelLeftClose, PanelLeftOpen, RefreshCw, Server, Settings, ShieldCheck, Sparkles, Waypoints, X, type LucideIcon } from "lucide-react";
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

type NavItem = { href: string; label: string; icon: LucideIcon; count?: number; exact?: boolean };
type NavGroup = { title: string; items: NavItem[] };

const collapsedKey = "cp.sidebar.collapsed";

function readCollapsed() {
  try { return window.localStorage.getItem(collapsedKey) === "1"; } catch { return false; }
}

function writeCollapsed(value: boolean) {
  try { window.localStorage.setItem(collapsedKey, value ? "1" : "0"); } catch { /* storage blocked */ }
}

function isActive(path: string, item: NavItem) {
  return item.exact ? path === item.href : path.startsWith(item.href.replace(/\/$/, ""));
}

/** Provides the left-hand sidebar, the slim top bar and connection feedback across pages. */
export function AppShell({ children }: { children: React.ReactNode }) {
  const path = usePathname();
  const { agents, supportRequests, connection, refresh } = useControlPlane();
  const { can } = useAuth();
  const [collapsed, setCollapsed] = useState(false);
  const [drawerOpen, setDrawerOpen] = useState(false);
  const sidebar = useRef<HTMLElement>(null);
  const menuButton = useRef<HTMLButtonElement>(null);

  useEffect(() => { setCollapsed(readCollapsed()); }, []);
  useEffect(() => { setDrawerOpen(false); }, [path]);
  useEffect(() => {
    if (!drawerOpen) return;
    sidebar.current?.querySelector<HTMLElement>("a, button")?.focus();
    const close = (event: KeyboardEvent) => {
      if (event.key !== "Escape") return;
      setDrawerOpen(false);
      menuButton.current?.focus();
    };
    window.addEventListener("keydown", close);
    return () => window.removeEventListener("keydown", close);
  }, [drawerOpen]);

  const toggleCollapsed = () => setCollapsed(value => { writeCollapsed(!value); return !value; });

  const groups: NavGroup[] = [
    { title: "Monitor", items: [
      { href: "/", label: "Overview", icon: LayoutDashboard, exact: true },
      { href: "/agents/", label: "Endpoints", icon: Waypoints }
    ] },
    { title: "Support", items: [
      { href: "/support/", label: "Helpdesk", icon: LifeBuoy, count: supportRequests.length },
      { href: "/approvals/", label: "Approvals", icon: ClipboardCheck },
      { href: "/feature-requests/", label: "Feature requests", icon: Lightbulb }
    ] },
    { title: "Agent setup", items: [
      { href: "/skills/", label: "Skills", icon: Sparkles },
      { href: "/capabilities/", label: "Capabilities", icon: BookOpen },
      { href: "/environment/", label: "Environment", icon: MapIcon },
      { href: "/settings/", label: "Settings", icon: Settings },
      { href: "/files/", label: "Packages", icon: Package }
    ] },
    ...(can("users.manage") ? [{ title: "Administration", items: [
      { href: "/operators/", label: "Operators", icon: ShieldCheck }
    ] }] : []),
    { title: "Help", items: [
      { href: "/docs/", label: "User manual", icon: BookMarked }
    ] }
  ];
  const current = groups.flatMap(group => group.items).find(item => isActive(path, item));

  return <div className={`app-shell${collapsed ? " collapsed" : ""}${drawerOpen ? " drawer-open" : ""}`}>
    <aside className="sidebar" ref={sidebar} id="sidebar" aria-label="Main menu">
      <div className="sidebar-head">
        <Link className="brand" href="/" aria-label="EndpointAgents overview" title="EndpointAgents">
          <span className="brand-mark"><Server size={17} /></span>
          <span className="brand-copy"><strong>EndpointAgents</strong><span>Control plane</span></span>
        </Link>
        <button className="sidebar-toggle rail-toggle" onClick={toggleCollapsed} aria-expanded={!collapsed} aria-controls="sidebar"
          aria-label={collapsed ? "Expand menu" : "Collapse menu"} title={collapsed ? "Expand menu" : "Collapse menu"}>
          {collapsed ? <PanelLeftOpen size={17} /> : <PanelLeftClose size={17} />}
        </button>
        <button className="sidebar-toggle drawer-close" onClick={() => { setDrawerOpen(false); menuButton.current?.focus(); }} aria-label="Close menu"><X size={17} /></button>
      </div>
      <nav className="sidebar-nav" aria-label="Control plane">
        {groups.map(group => <div className="nav-group" key={group.title}>
          <p className="nav-group-title">{group.title}</p>
          <ul>{group.items.map(item => {
            const active = isActive(path, item);
            const Icon = item.icon;
            return <li key={item.href}><Link className="nav-link" href={item.href} aria-current={active ? "page" : undefined}
              title={collapsed ? item.label : undefined}>
              <Icon size={17} /><span>{item.label}</span>
              {!!item.count && <span className="nav-count" aria-label={`${item.count} waiting`}>{item.count}</span>}
            </Link></li>;
          })}</ul>
        </div>)}
      </nav>
      <div className="sidebar-foot"><AccountMenu /></div>
    </aside>
    <button className="sidebar-backdrop" tabIndex={-1} aria-hidden="true" onClick={() => setDrawerOpen(false)} />

    <main className="app-main">
      <header className="topbar">
        <div className="topbar-title">
          <span className="brand-mark"><Server size={15} /></span>
          <span>{current?.label ?? "Control plane"}</span>
        </div>
        <div className="topbar-status">
          <span className={`connection-state ${connection}`}><span className="state-dot" />{statusLabel(connection)}</span>
          <span className="topbar-divider" /><span>{agents.length} endpoint{agents.length === 1 ? "" : "s"} online</span>
          <button className="icon-button" onClick={() => void refresh()} aria-label="Refresh control-plane data" title="Refresh"><RefreshCw size={16} /></button>
          <button ref={menuButton} className="menu-button" onClick={() => setDrawerOpen(true)} aria-label="Open menu"
            aria-expanded={drawerOpen} aria-controls="sidebar"><Menu size={18} /></button>
        </div>
      </header>

      {connection !== "live" && <div className="connection-banner" role="status"><CircleAlert size={16} />
        {connection === "reconnecting" ? "Live updates paused. Reconnecting and preserving the current view…" :
         connection === "connecting" ? "Connecting to live control-plane updates…" :
         "Live updates are unavailable. Service controls remain available from the overview."}</div>}
      {children}
    </main>
  </div>;
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
