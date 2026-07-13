"use client";

import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from "react";
import { mergeActivity } from "./activity";
import { CONTROL_PLANE_URL, getSnapshot } from "./control-plane";
import type { Activity, Agent, CommandDefinition, ConnectionState } from "./types";

const EVENT_TYPES: Activity["type"][] =
  ["agent.connected", "agent.disconnected", "command.started", "command.completed"];

type ControlPlaneContextValue = {
  agents: Agent[]; activity: Activity[]; commands: CommandDefinition[];
  connection: ConnectionState; loading: boolean; loadError?: string;
  refresh: () => Promise<void>;
};

const ControlPlaneContext = createContext<ControlPlaneContextValue | undefined>(undefined);

/** Owns the single browser SignalR connection and snapshot reconciliation lifecycle. */
export function ControlPlaneProvider({ children }: { children: React.ReactNode }) {
  const [agents, setAgents] = useState<Agent[]>([]);
  const [activity, setActivity] = useState<Activity[]>([]);
  const [commands, setCommands] = useState<CommandDefinition[]>([]);
  const [connection, setConnection] = useState<ConnectionState>("connecting");
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string>();
  const mounted = useRef(true);

  const refresh = useCallback(async () => {
    try {
      const snapshot = await getSnapshot();
      if (!mounted.current) return;
      setAgents(snapshot.agents);
      setActivity(current => mergeActivity(current, snapshot.activity));
      setCommands(snapshot.commands);
      setLoadError(undefined);
    } catch (error) {
      if (mounted.current)
        setLoadError(error instanceof Error ? error.message : "Could not reach the control plane.");
    } finally {
      if (mounted.current) setLoading(false);
    }
  }, []);

  useEffect(() => {
    mounted.current = true;
    const initialLoad = window.setTimeout(() => void refresh(), 0);
    const hub = new HubConnectionBuilder().withUrl(`${CONTROL_PLANE_URL}/dashboard-hub`)
      .withAutomaticReconnect([0, 2000, 5000, 10000]).configureLogging(LogLevel.Warning).build();

    const receive = (event: Activity) => {
      setActivity(current => mergeActivity(current, [event]));
      if (event.type === "agent.connected" && event.agent)
        setAgents(current => [...current.filter(agent => agent.agentId !== event.agentId), event.agent!]);
      if (event.type === "agent.disconnected")
        setAgents(current => current.filter(agent => agent.agentId !== event.agentId));
    };

    EVENT_TYPES.forEach(type => hub.on(type, receive));
    hub.onreconnecting(() => setConnection("reconnecting"));
    hub.onreconnected(() => { setConnection("live"); void refresh(); });
    hub.onclose(() => setConnection("offline"));
    hub.start().then(() => setConnection("live")).catch(() => setConnection("offline"));

    return () => {
      mounted.current = false;
      window.clearTimeout(initialLoad);
      void hub.stop();
    };
  }, [refresh]);

  const value = useMemo(() => ({ agents, activity, commands, connection, loading, loadError, refresh }),
    [agents, activity, commands, connection, loading, loadError, refresh]);
  return <ControlPlaneContext.Provider value={value}>{children}</ControlPlaneContext.Provider>;
}

/** Returns the shared live control-plane snapshot for any application route. */
export function useControlPlane() {
  const value = useContext(ControlPlaneContext);
  if (!value) throw new Error("useControlPlane must be used inside ControlPlaneProvider.");
  return value;
}
