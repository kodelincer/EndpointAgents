import type { HermesQueueSettings } from "./types";

/** Allowed ranges; the control plane enforces the same limits. */
export const queueLimits: Record<keyof HermesQueueSettings, { min: number; max: number }> = {
  maxParallelRuns: { min: 1, max: 32 },
  maxQueueLength: { min: 1, max: 1000 },
  maxWaitMinutes: { min: 1, max: 240 },
};

/** Returns an error per field that is not a whole number inside its range. */
export function queueSettingsErrors(settings: Record<keyof HermesQueueSettings, string>) {
  const errors: Partial<Record<keyof HermesQueueSettings, string>> = {};
  for (const key of Object.keys(queueLimits) as (keyof HermesQueueSettings)[]) {
    const { min, max } = queueLimits[key];
    const value = Number(settings[key]);
    if (!/^\d+$/.test(settings[key].trim()) || value < min || value > max)
      errors[key] = `Enter a whole number from ${min} to ${max}.`;
  }
  return errors;
}

/** Describes how long the oldest request has waited, for the live queue line. */
export function waitedFor(oldestUtc: string | null, now = Date.now()) {
  if (!oldestUtc) return "";
  const minutes = Math.max(0, Math.floor((now - Date.parse(oldestUtc)) / 60_000));
  return minutes < 1 ? "Oldest request has waited under a minute." : `Oldest request has waited ${minutes} min.`;
}
