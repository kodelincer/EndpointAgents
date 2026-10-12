import { useEffect, useRef } from "react";
import { ShieldAlert, ShieldQuestion } from "lucide-react";
import type { ConsentRequest } from "./types";

/**
 * A request from the service to do something on this PC. It holds focus until answered; Escape and
 * the default button both mean "no".
 */
export function ConsentSheet({ request, onAnswer }: {
  request: ConsentRequest;
  onAnswer: (id: string, approved: boolean) => void;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  const decline = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    const element = dialog.current;
    if (element && !element.open) element.showModal();
    decline.current?.focus();
  }, [request.id]);

  return (
    <dialog ref={dialog} className="consent" data-caution={request.caution || undefined}
      aria-labelledby="consent-title" aria-describedby="consent-body"
      onCancel={(e) => { e.preventDefault(); onAnswer(request.id, false); }}>
      <div className="consent-icon" aria-hidden>
        {request.caution ? <ShieldAlert size={26} /> : <ShieldQuestion size={26} />}
      </div>
      <h2 id="consent-title">{request.title}</h2>
      <p id="consent-body">{request.body}</p>
      {request.detail && <p className="consent-detail" dir="auto">{request.detail}</p>}
      <div className="consent-buttons">
        <button className="button primary" onClick={() => onAnswer(request.id, true)}>{request.approveText}</button>
        <button ref={decline} className="button" onClick={() => onAnswer(request.id, false)}>{request.declineText}</button>
      </div>
    </dialog>
  );
}
