import { Copy, Minus, Square, X } from "lucide-react";
import { host } from "./bridge";
import { healthTone } from "./format";
import type { TrayStatus } from "./types";
import { RobotFace, faceFor } from "./RobotFace";

/** The window's own title bar: drag area, live connection state, and Windows caption buttons. */
export function TitleBar({ status, maximized }: { status: TrayStatus | null; maximized: boolean }) {
  const tone = status ? healthTone(status.health) : "idle";

  const onPointerDown = (event: React.PointerEvent) => {
    if (event.button !== 0 || (event.target as HTMLElement).closest("button")) return;
    if (event.detail === 2) void host.call("window.toggleMaximize");
    else void host.call("window.drag");
  };

  return (
    <header className="titlebar" onPointerDown={onPointerDown}>
      <div className="brand">
        <RobotFace face={faceFor(status)} size={26} />
        <span className="brand-name">دستیار پشتیبانی</span>
      </div>
      <div className="title-status" role="status" aria-live="polite" data-tone={tone}>
        <span className="dot" aria-hidden />
        <span>{status?.label ?? "در حال بررسی وضعیت"}</span>
      </div>
      <div className="captions">
        <button aria-label="کوچک کردن" onClick={() => host.call("window.minimize")}>
          <Minus size={15} strokeWidth={1.6} aria-hidden />
        </button>
        <button aria-label={maximized ? "بازگرداندن اندازه" : "بزرگ کردن"} onClick={() => host.call("window.toggleMaximize")}>
          {maximized ? <Copy size={13} strokeWidth={1.6} aria-hidden /> : <Square size={12} strokeWidth={1.6} aria-hidden />}
        </button>
        <button className="close" aria-label="بستن پنجره" onClick={() => host.call("window.close")}>
          <X size={16} strokeWidth={1.6} aria-hidden />
        </button>
      </div>
    </header>
  );
}
