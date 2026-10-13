import type { TrayStatus } from "./types";

export type Face = "happy" | "neutral" | "sleepy" | "sad" | "angry";

/** Same rules as the notification-area icon (TrayIconFactory.FaceFor). */
export function faceFor(status: TrayStatus | null): Face {
  if (!status) return "neutral";
  const serviceUp = status.service === "Running" || status.service === "Interactive";
  switch (status.health) {
    case "Ready": return "happy";
    case "Paused": return "sleepy";
    case "Warning": return "sad";
    case "Error": return status.runtime && serviceUp ? "sad" : "angry";
    case "Stopped": return "angry";
    default: return "neutral";
  }
}

const COLORS: Record<Face, string> = {
  happy: "#2f6bff", neutral: "#7f93bd", sleepy: "#7f93bd", sad: "#ffb400", angry: "#ef3b3b",
};

function mix(hex: string, other: [number, number, number], t: number) {
  const parts = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16));
  return "#" + parts.map((v, i) => Math.round(v + (other[i] - v) * t).toString(16).padStart(2, "0")).join("");
}

const NAVY = "#16213f";

/** The assistant's head; mood and colour follow the service state. */
export function RobotFace({ face, size = 24 }: { face: Face; size?: number }) {
  const accent = COLORS[face];
  const eye = mix(accent, [255, 255, 255], 0.12);
  const dark = mix(accent, [10, 18, 40], 0.25);
  const id = `rf-${face}`;
  const stroke = { fill: "none", stroke: eye, strokeLinecap: "round", strokeLinejoin: "round" } as const;
  const dot = (cx: number, rx: number, ry: number) => <ellipse cx={cx} cy={32} rx={rx} ry={ry} fill={eye} />;

  return (
    <svg width={size} height={size} viewBox="0 0 64 64" aria-hidden className="robot-face" data-face={face}>
      <defs>
        <linearGradient id={id} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor={mix(accent, [255, 255, 255], 0.88)} />
          <stop offset="1" stopColor={mix(accent, [255, 255, 255], 0.55)} />
        </linearGradient>
      </defs>
      <line x1="20" y1="14" x2="17" y2="6" stroke={dark} strokeWidth="2.6" strokeLinecap="round" />
      <line x1="44" y1="14" x2="47" y2="6" stroke={dark} strokeWidth="2.6" strokeLinecap="round" />
      <circle cx="16.5" cy="5" r="3.8" fill={accent} /><circle cx="47.5" cy="5" r="3.8" fill={accent} />
      <rect x="1.5" y="26" width="8.5" height="18" rx="4.2" fill={accent} />
      <rect x="54" y="26" width="8.5" height="18" rx="4.2" fill={accent} />
      <path d="M58 42c0 11-8 15-19 15" fill="none" stroke={accent} strokeWidth="3.6" strokeLinecap="round" />
      <circle cx="36" cy="57" r="3.8" fill={accent} />
      <rect x="7" y="13" width="50" height="42" rx="18" fill={`url(#${id})`} />
      <rect x="13" y="19" width="38" height="30" rx="13" fill={NAVY} />
      {face === "happy" && (<>
        <path d="M20.5 33.5q3.5-6 7 0M36.5 33.5q3.5-6 7 0" {...stroke} strokeWidth="3.2" />
        <path d="M24.5 40q7.5 8 15 0" {...stroke} strokeWidth="3.2" />
      </>)}
      {face === "neutral" && (<>
        {dot(24, 3.6, 4.2)}{dot(40, 3.6, 4.2)}
        <path d="M27 41.5h10" {...stroke} strokeWidth="3" />
      </>)}
      {face === "sleepy" && (<>
        <path d="M20.5 32q3.5 3.5 7 0M36.5 32q3.5 3.5 7 0" {...stroke} strokeWidth="3" />
        <path d="M28 41.5q4 1.8 8 0" {...stroke} strokeWidth="2.6" />
      </>)}
      {face === "sad" && (<>
        {dot(24.5, 3.6, 4.2)}{dot(39.5, 3.6, 4.2)}
        <path d="M19.5 27l9-3.5M44.5 27l-9-3.5" {...stroke} strokeWidth="2.8" />
        <path d="M24.5 44q7.5-8 15 0" {...stroke} strokeWidth="3.2" />
        <path d="M45 37c2 3 3 4.8 3 6.2a3 3 0 0 1-6 0c0-1.4 1-3.2 3-6.2z" fill="#8fd0ff" />
      </>)}
      {face === "angry" && (<>
        {dot(24.5, 3.6, 2.8)}{dot(39.5, 3.6, 2.8)}
        <path d="M19 24.5l10 4.5M45 24.5l-10 4.5" {...stroke} strokeWidth="3.2" />
        <path d="M24 44.5l4-2.5 4 2.5 4-2.5 4 2.5" {...stroke} strokeWidth="3" />
      </>)}
    </svg>
  );
}
