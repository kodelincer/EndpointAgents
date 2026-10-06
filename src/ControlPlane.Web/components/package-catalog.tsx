"use client";

import { ChevronDown, ChevronRight, CircleAlert, CircleCheck, Package, RefreshCw, TriangleAlert } from "lucide-react";
import { useCallback, useEffect, useState } from "react";
import { getPackageInventory } from "@/lib/control-plane";
import { describeStep, finishRules, packageCompatibility, packageStatus } from "@/lib/packages";
import type { BatPackage, PackageInventory } from "@/lib/types";

/** Read-only operator view of FileRepository\packages and why each package is or is not offered. */
export function PackageCatalog() {
  const [inventory, setInventory] = useState<PackageInventory>();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string>();
  const [open, setOpen] = useState<string>();

  const refresh = useCallback(async () => {
    setLoading(true);
    try {
      setInventory(await getPackageInventory());
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

  return <div className="file-page">
    <section className="file-heading" aria-labelledby="packages-title">
      <div><div className="file-heading-mark"><Package size={17} /></div><div><h1 id="packages-title">BAT packages</h1>
        <p>Reviewed BAT files with their playbooks. Only Ready packages are offered to endpoints.</p></div></div>
      <button className="secondary-button" onClick={() => void refresh()} disabled={loading}><RefreshCw className={loading ? "spin" : ""} size={15} />Refresh</button>
    </section>

    {error && <div className="inline-error" role="alert"><CircleAlert size={17} /><div><strong>Packages are unavailable</strong><span>{error}</span></div><button onClick={() => void refresh()}>Retry</button></div>}

    {inventory && <p className="package-path">Package folder: <code>{inventory.packagesPath}</code></p>}

    {loading ? <div className="file-list" aria-label="Loading packages"><div className="file-skeleton" /></div> : packages.length === 0 ?
      <section className="file-empty"><span className="empty-icon"><Package size={23} /></span><h2>No packages yet</h2>
        <p>Create one with <code>scripts\new-bat-package.ps1</code>; see “How to add a package” below.</p></section> :
      <section className="file-list" aria-label="BAT packages">{packages.map(item =>
        <PackageRow key={item.id} item={item} inventory={inventory!} open={open === item.id}
          onToggle={() => setOpen(open === item.id ? undefined : item.id)} />)}</section>}

    <HowToAddPackage />
  </div>;
}

function PackageRow({ item, inventory, open, onToggle }:
  { item: BatPackage; inventory: PackageInventory; open: boolean; onToggle: () => void }) {
  const status = packageStatus(item.status);
  const Icon = status.tone === "ready" ? CircleCheck : status.tone === "warning" ? TriangleAlert : CircleAlert;
  return <article className="file-row package-row">
    <div className={`package-status-icon ${status.tone}`}><Icon size={18} /></div>
    <div className="file-row-main">
      <div className="file-row-title"><strong>{item.displayName || item.id}</strong>
        <span className="file-chip">{item.id}</span>{item.kind && <span className="file-chip">{item.kind}</span>}
        <span className={`package-status ${status.tone}`}>{status.label}</span></div>
      {item.description && <p className="file-description">{item.description}</p>}
      <p className="file-compatibility">{packageCompatibility(item)}</p>
      {item.reasons.length > 0 && <ul className="package-reasons">{item.reasons.map(reason => <li key={reason}>{reason}</li>)}</ul>}
      {item.warnings.length > 0 && <ul className="package-warnings">{item.warnings.map(warning => <li key={warning}>{warning}</li>)}</ul>}
      <button type="button" className="package-toggle" onClick={onToggle} aria-expanded={open}>
        {open ? <ChevronDown size={14} /> : <ChevronRight size={14} />}{open ? "Hide details" : "Show details"}</button>
      {open && <PackageDetail item={item} inventory={inventory} />}
    </div>
  </article>;
}

function PackageDetail({ item, inventory }: { item: BatPackage; inventory: PackageInventory }) {
  const limits = inventory.limits;
  return <div className="package-detail">
    {item.playbook ? <>
      <h3>Playbook (version {item.playbook.version})</h3>
      <ol className="package-steps">{item.playbook.steps.map((step, index) => <li key={index}>{describeStep(step, index)}</li>)}</ol>
      <h3>Finish rules</h3>
      <ul>{finishRules(item.playbook, limits.defaultGuiDialogGraceSeconds).map(rule => <li key={rule}>{rule}</li>)}</ul>
    </> : <p>No readable playbook.</p>}
    <h3>Hashes</h3>
    <dl className="package-hashes">
      <div><dt>BAT file</dt><dd>{item.batFileName ?? "—"}{item.batSizeBytes ? ` · ${item.batSizeBytes} bytes` : ""}</dd></div>
      <div><dt>BAT SHA-256 pinned</dt><dd>{item.pinnedBatSha256 ?? "not set"}</dd></div>
      <div><dt>BAT SHA-256 on disk</dt><dd>{item.actualBatSha256 ?? "—"}</dd></div>
      <div><dt>Playbook SHA-256 pinned</dt><dd>{item.pinnedPlaybookSha256 ?? "not set"}</dd></div>
      <div><dt>Playbook SHA-256 computed</dt><dd>{item.computedPlaybookSha256 ?? "—"}</dd></div>
      {item.supportFiles.map(file => <div key={file.fileName}><dt>files\{file.fileName}</dt><dd>{file.sha256}</dd></div>)}
    </dl>
    <h3>Limits</h3>
    <p className="package-limits">Up to {limits.maxSteps} steps and {limits.maxResponses} answers, {limits.minStepTimeoutSeconds}–{limits.maxStepTimeoutSeconds} s per step, {limits.maxRunMinutes} minutes and {Math.round(limits.maxOutputBytes / 1024)} KB of output per run. Answers are at most {limits.maxResponseCharacters} characters and never contain passwords or keys.</p>
    {item.review && <><h3>Review</h3><p className="package-limits">{[item.review.reviewedBy && `Reviewed by ${item.review.reviewedBy}`,
      item.review.reviewedOn && `on ${item.review.reviewedOn}`, item.review.source && `Source: ${item.review.source}`,
      item.review.notes].filter(Boolean).join(" · ")}</p></>}
    <p className="package-limits">Folder: <code>{item.folder}</code></p>
  </div>;
}

function HowToAddPackage() {
  return <section className="package-howto" aria-labelledby="howto-title">
    <h2 id="howto-title">How to add a package</h2>
    <ol>
      <li>Review the BAT and get it from a trusted source.</li>
      <li>Create the package folder: <code>.\scripts\new-bat-package.ps1 -Bat &lt;file.bat&gt; -Id &lt;id&gt; -Kind office</code></li>
      <li>Find each screen, one round at a time: <code>.\scripts\test-bat-playbook.ps1 -Package &lt;folder&gt; -Execute -Discover</code> and add a playbook step for it.</li>
      <li>Fill in the success text, failure texts, compatibility and review, and remove every TODO.</li>
      <li>Check and record the hashes: <code>.\scripts\test-bat-playbook.ps1 -Package &lt;folder&gt; -Fix</code> until the status is Ready.</li>
      <li>Copy the folder to the control plane’s <code>FileRepository\packages</code>, refresh this page, and test on one PC before rollout.</li>
    </ol>
    <p>The full guide is <code>docs\bat-packages.md</code>.</p>
  </section>;
}
