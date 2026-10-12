import { useState } from "react";
import { ChevronDown } from "lucide-react";
import { isTerminalStage, progressLabel, time } from "./format";
import type { ProgressEvent } from "./types";

/**
 * One support run drawn as a metro line: each progress event is a station, the current one a
 * ringed stop. Finished runs fold to their terminus and can be reopened.
 */
export function RunLine({ events }: { events: ProgressEvent[] }) {
  const last = events[events.length - 1];
  const done = last ? isTerminalStage(last) : false;
  const failed = last?.outcome === "Failure" || last?.stage === "Failed";
  const waiting = last?.stage === "AwaitingApproval";
  const [open, setOpen] = useState<boolean | null>(null);
  const expanded = open ?? !done;
  if (!last) return null;

  const tone = failed ? "bad" : done ? "ok" : waiting ? "warn" : "live";

  return (
    <section className="run" data-tone={tone} aria-label="روند بررسی">
      <button className="run-head" aria-expanded={expanded} onClick={() => setOpen(!expanded)}>
        <span className="run-badge" aria-hidden />
        <span className="run-title">{progressLabel(last)}</span>
        <span className="run-count">{events.length.toLocaleString("fa-IR")} ایستگاه</span>
        <ChevronDown className="run-chevron" size={16} aria-hidden />
      </button>
      {expanded && (
        <ol className="run-stations">
          {events.map((event, index) => {
            const current = index === events.length - 1;
            const state = event.outcome === "Failure" ? "failed" : current && !done ? "current" : "passed";
            return (
              <li key={event.eventId} className="station" data-state={state} aria-current={current ? "step" : undefined}>
                <span className="station-stop" aria-hidden />
                <span className="station-label">{progressLabel(event)}</span>
                <time className="station-time" dateTime={event.createdAtUtc}>{time(event.createdAtUtc)}</time>
              </li>
            );
          })}
        </ol>
      )}
    </section>
  );
}
