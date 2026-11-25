"use client";

import { BadgeCheck, ChevronDown, ChevronRight, CircleAlert, CircleCheck, Hammer, KeyRound, Package, RefreshCw, TriangleAlert } from "lucide-react";
import Link from "next/link";
import { useCallback, useEffect, useState } from "react";
import { getPackageInventory } from "@/lib/control-plane";
import { useAuth } from "@/components/auth-gate";
import { approvalState, approvePackage, getApprovalInfo, isBuilderPackage, type ApprovalInfo } from "@/lib/package-builder";
import { acceptedRiskLines, describeRun, describeStep, finishRules, packageCompatibility, packageStatus } from "@/lib/packages";
import { cannotSkipApproval, getNoApproval, installLines, isInstallPackage, requirementLines, setNoApproval } from "@/lib/software";
import type { BatPackage, PackageInventory } from "@/lib/types";
import { PageIntro } from "@/components/page-intro";
import { pageName } from "@/lib/pages";

/** Read-only operator view of FileRepository\packages and why each package is or is not offered. */
export function PackageCatalog() {
  const [inventory, setInventory] = useState<PackageInventory>();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string>();
  const [open, setOpen] = useState<string>();
  const [noApproval, setNoApprovalIds] = useState<Set<string>>(new Set());
  const [switchError, setSwitchError] = useState<string>();
  const [approval, setApproval] = useState<ApprovalInfo>();
  const [approving, setApproving] = useState<string>();
  const [approveMessage, setApproveMessage] = useState<{ ok: boolean; text: string }>();
  const { can } = useAuth();
  const isAdmin = can("packages.approve");

  const refresh = useCallback(async () => {
    setLoading(true);
    try {
      setInventory(await getPackageInventory());
      setNoApprovalIds(new Set((await getNoApproval().catch(() => [])).map(entry => entry.packageId)));
      setApproval(await getApprovalInfo().catch(() => undefined));
      setError(undefined);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "The package list could not be loaded.");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    const initial = window.setTimeout(() => void refresh(), 0);
    return () => window.clearTimeout(initial);
  }, [refresh]);

  const packages = inventory?.packages ?? [];

  const toggleApproval = async (item: BatPackage, skip: boolean) => {
    if (skip && !window.confirm(`Install "${item.displayName || item.id}" on any PC without a helpdesk approval?`)) return;
    try {
      setNoApprovalIds(new Set((await setNoApproval(item.id, skip)).map(entry => entry.packageId)));
      setSwitchError(undefined);
    } catch (reason) { setSwitchError(reason instanceof Error ? reason.message : "The list was not changed."); }
  };

  const approve = async (item: BatPackage) => {
    if (!window.confirm(`Approve and sign "${item.displayName || item.id}"?\n\nThe control plane signs exactly this revision. PCs that trust its key may then run it without asking the user. Check the steps under "Show details" first.`)) return;
    setApproving(item.id); setApproveMessage(undefined);
    try {
      const result = await approvePackage(item);
      setApproveMessage({ ok: true, text: `${item.displayName || item.id} is approved and signed${result.status ? `: ${packageStatus(result.status.status).label}` : ""}.` });
      await refresh();
    } catch (reason) { setApproveMessage({ ok: false, text: reason instanceof Error ? reason.message : "The package was not approved." }); }
    finally { setApproving(undefined); }
  };

  return <div className="file-page">
    <section className="file-heading" aria-labelledby="packages-title">
      <div><div className="file-heading-mark"><Package size={17} /></div><div><h1 id="packages-title">{pageName("/files/")}</h1></div></div>
      <div className="file-heading-actions">
        <Link className="primary-button" href="/package-builder/"><Hammer size={15} />Build a package</Link>
        <button className="secondary-button" onClick={() => void refresh()} disabled={loading}><RefreshCw className={loading ? "spin" : ""} size={15} />Refresh</button>
      </div>
    </section>
    <PageIntro route="/files/" />

    {error && <div className="inline-error" role="alert"><CircleAlert size={17} /><div><strong>Packages are unavailable</strong><span>{error}</span></div><button onClick={() => void refresh()}>Retry</button></div>}

    {switchError && <div className="inline-error" role="alert"><CircleAlert size={17} /><div><strong>Approval setting not changed</strong><span>{switchError}</span></div></div>}

    {approveMessage && <div className={approveMessage.ok ? "builder-result ready" : "inline-error"} role="status">{approveMessage.ok ? <BadgeCheck size={17} /> : <CircleAlert size={17} />}<div><strong>{approveMessage.ok ? "Approved" : "Not approved"}</strong><span>{approveMessage.text}</span></div></div>}

    {isAdmin && approval && <ApprovalKey info={approval} />}

    {inventory && <p className="package-path">Package folder: <code>{inventory.packagesPath}</code></p>}

    {loading ? <div className="file-list" aria-label="Loading packages"><div className="file-skeleton" /></div> : packages.length === 0 ?
      <section className="file-empty"><span className="empty-icon"><Package size={23} /></span><h2>No packages yet</h2>
        <p>Make one with the <Link href="/package-builder/">Package builder</Link>, or with <code>scripts\new-package.ps1</code>; see “How to add a package” below.</p></section> :
      <section className="file-list" aria-label="Packages">{packages.map(item =>
        <PackageRow key={item.id} item={item} inventory={inventory!} open={open === item.id}
          approval={approvalState(item, approval, isAdmin)} approving={approving === item.id}
          onApprove={() => void approve(item)}
          noApproval={noApproval.has(item.id)} onApprovalChange={skip => void toggleApproval(item, skip)}
          onToggle={() => setOpen(open === item.id ? undefined : item.id)} />)}</section>}

    <HowToAddPackage />
  </div>;
}

function PackageRow({ item, inventory, open, onToggle, noApproval, onApprovalChange, approval, approving, onApprove }:
  { item: BatPackage; inventory: PackageInventory; open: boolean; onToggle: () => void;
    noApproval: boolean; onApprovalChange: (skip: boolean) => void;
    approval: ReturnType<typeof approvalState>; approving: boolean; onApprove: () => void }) {
  const blocked = cannotSkipApproval(item);
  const status = packageStatus(item.status);
  const Icon = status.tone === "ready" ? CircleCheck : status.tone === "warning" ? TriangleAlert : CircleAlert;
  return <article className="file-row package-row">
    <div className={`package-status-icon ${status.tone}`}><Icon size={18} /></div>
    <div className="file-row-main">
      <div className="file-row-title"><strong>{item.displayName || item.id}</strong>
        <span className="file-chip">{item.id}</span>{item.kind && <span className="file-chip">{item.kind}</span>}{item.fileType && <span className="file-chip">{item.fileType.toUpperCase()}</span>}
        <span className={`package-status ${status.tone}`}>{status.label}</span></div>
      {item.description && <p className="file-description">{item.description}</p>}
      <p className="file-compatibility">{packageCompatibility(item)}{item.problem && ` · problem ${item.problem}, try order ${item.order ?? 100}`}</p>
      {item.symptoms && item.symptoms.length > 0 && <p className="file-compatibility">Matches: {item.symptoms.map(symptom => <span key={symptom} className="file-chip" dir="auto">{symptom}</span>)}</p>}
      {isInstallPackage(item) && <label className="package-approval-switch" title={blocked ?? undefined}>
        <input type="checkbox" checked={noApproval && !blocked} disabled={Boolean(blocked)}
          onChange={event => onApprovalChange(event.target.checked)} />
        {blocked ?? (noApproval ? "No approval needed: installs as soon as the PC meets the requirements"
          : "Waits for a helpdesk approval on the Approvals page")}</label>}
      {approval.show && <div className="package-approve">
        <span><strong>Waiting for approval</strong>{approval.builtBy ? ` · saved by ${approval.builtBy}` : ""}</span>
        <button type="button" className="primary-button" onClick={onApprove} disabled={Boolean(approval.blocked) || approving}
          title={approval.blocked ?? undefined}><BadgeCheck size={15} />{approving ? "Signing…" : "Approve and sign"}</button>
        {approval.blocked && <small>{approval.blocked}</small>}
      </div>}
      {item.review?.approvedBy && <p className="file-compatibility">Approved in the dashboard by {item.review.approvedBy}{item.review.approvedOn ? ` on ${item.review.approvedOn}` : ""}</p>}
      {item.reasons.length > 0 && <ul className="package-reasons">{item.reasons.map(reason => <li key={reason}>{reason}</li>)}</ul>}
      {item.warnings.length > 0 && <ul className="package-warnings">{item.warnings.map(warning => <li key={warning}>{warning}</li>)}</ul>}
      {isBuilderPackage(item) && <Link className="package-toggle" href={`/package-builder/?id=${encodeURIComponent(item.id)}`}><Hammer size={14} />Change in the Package builder</Link>}
      <button type="button" className="package-toggle" onClick={onToggle} aria-expanded={open}>
        {open ? <ChevronDown size={14} /> : <ChevronRight size={14} />}{open ? "Hide details" : "Show details"}</button>
      {open && <PackageDetail item={item} inventory={inventory} />}
    </div>
  </article>;
}

function PackageDetail({ item, inventory }: { item: BatPackage; inventory: PackageInventory }) {
  const limits = inventory.limits;
  return <div className="package-detail">
    {item.software && <>
      <h3>Software{item.software.name ? `: ${item.software.name}` : ""}{item.software.version ? ` ${item.software.version}` : ""}{item.software.licensed ? " (licensed)" : ""}</h3>
      <h3>Requirements (checked before approval and again before install)</h3>
      <ul>{requirementLines(item.software).map(line => <li key={line}>{line}</li>)}</ul>
      <h3>Install</h3>
      <ul>{installLines(item.software).map(line => <li key={line}>{line}</li>)}</ul>
    </>}
    {item.playbook ? <>
      <h3>Playbook (version {item.playbook.version})</h3>
      <ul>{describeRun(item).map(line => <li key={line}>{line}</li>)}</ul>
      {item.playbook.steps.length > 0 && <ol className="package-steps">{item.playbook.steps.map((step, index) => <li key={index}>{describeStep(step, index)}</li>)}</ol>}
      <h3>Finish rules</h3>
      <ul>{finishRules(item.playbook, limits.defaultGuiDialogGraceSeconds).map(rule => <li key={rule}>{rule}</li>)}</ul>
    </> : <p>No readable playbook.</p>}
    {acceptedRiskLines(item).length > 0 && <>
      <h3>Accepted risks</h3>
      <ul>{acceptedRiskLines(item).map(line => <li key={line}>{line}</li>)}</ul>
    </>}
    <h3>Hashes</h3>
    <dl className="package-hashes">
      <div><dt>File</dt><dd>{item.batFileName ?? "—"}{item.batSizeBytes ? ` · ${item.batSizeBytes} bytes` : ""}</dd></div>
      {item.fileType === "exe" && <div><dt>Signed by</dt><dd>{item.signer ?? "no embedded signature"}</dd></div>}
      <div><dt>File SHA-256 pinned</dt><dd>{item.pinnedBatSha256 ?? "not set"}</dd></div>
      <div><dt>File SHA-256 on disk</dt><dd>{item.actualBatSha256 ?? "—"}</dd></div>
      <div><dt>Playbook SHA-256 pinned</dt><dd>{item.pinnedPlaybookSha256 ?? "not set"}</dd></div>
      <div><dt>Playbook SHA-256 computed</dt><dd>{item.computedPlaybookSha256 ?? "—"}</dd></div>
      {item.supportFiles.map(file => <div key={file.fileName}><dt>files\{file.fileName}</dt><dd>{file.sha256}</dd></div>)}
    </dl>
    <h3>Limits</h3>
    <p className="package-limits">Up to {limits.maxSteps} steps and {limits.maxResponses} answers, {limits.minStepTimeoutSeconds}–{limits.maxStepTimeoutSeconds} s per step, {item.playbook?.timeoutSeconds ? `${item.playbook.timeoutSeconds} s` : `${limits.maxRunMinutes} minutes`} and {Math.round(limits.maxOutputBytes / 1024)} KB of output per run. Answers are at most {limits.maxResponseCharacters} characters and never contain passwords or keys.</p>
    {item.review && <><h3>Review</h3><p className="package-limits">{[item.review.reviewedBy && `Reviewed by ${item.review.reviewedBy}`,
      item.review.reviewedOn && `on ${item.review.reviewedOn}`, item.review.source && `Source: ${item.review.source}`,
      item.review.notes].filter(Boolean).join(" · ")}</p></>}
    <p className="package-limits">Folder: <code>{item.folder}</code></p>
  </div>;
}

/** The control plane's approval key, which every PC must trust before it runs approved packages. */
function ApprovalKey({ info }: { info: ApprovalInfo }) {
  return <section className="package-approval-key" aria-labelledby="approval-key-title">
    <h2 id="approval-key-title"><KeyRound size={15} />Approving in the dashboard</h2>
    {info.available ? <>
      <p>Packages an admin approves here are signed with the control plane&apos;s own key <code>{info.keyId}</code>. A PC runs them only after it trusts this key, for packages only (never for agent updates). Add it once on each PC under <code>PackageSigning:ApprovalPublicKeys</code>, or install PCs with <code>install-endpoint.ps1 -ApprovalSigningPublicKey &lt;key&gt;</code>.</p>
      <code className="builder-command">{info.publicKey}</code>
    </> : <p>{info.problem ?? "Approving in the dashboard is not available."} Sign packages with <code>test-bat-playbook.ps1 -Sign</code> instead.</p>}
  </section>;
}

function HowToAddPackage() {
  return <section className="package-howto" aria-labelledby="howto-title">
    <h2 id="howto-title">How to add a package</h2>
    <p>For a BAT, the easiest way is the <Link href="/package-builder/">Package builder</Link>: upload the file, pick the answer for each screen, save, then test and sign it (steps 5 and 6 below). To do it by hand instead:</p>
    <ol>
      <li>Review the BAT or EXE and get it from a trusted source.</li>
      <li>Create the package folder: <code>.\scripts\new-package.ps1 -File &lt;file.bat or file.exe&gt; -Id &lt;id&gt;</code>, adding <code>-NoInput</code> when the file needs no answers.</li>
      <li>For a file with screens, find each one, one round at a time: <code>.\scripts\test-bat-playbook.ps1 -Package &lt;folder&gt; -Execute -Discover</code> and add a playbook step for it.</li>
      <li>Fill in the symptoms users would describe, any fixed arguments, the success and failure texts, compatibility and review, and remove every TODO.</li>
      <li>Packages made in the Package builder: an admin clicks <strong>Approve and sign</strong> on its row. Otherwise check, record the hashes and sign the reviewed revision: <code>.\scripts\test-bat-playbook.ps1 -Package &lt;folder&gt; -Fix -Sign</code> until the status is Ready. Create your signing key once with <code>.\scripts\new-package-signing-key.ps1</code>.</li>
      <li>Copy the folder to the control plane’s <code>FileRepository\packages</code>, refresh this page, and test on one PC before rollout.</li>
    </ol>
    <p>The full guide is <code>docs\bat-packages.md</code>.</p>
  </section>;
}
