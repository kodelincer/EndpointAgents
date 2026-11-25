"use client";

import { ArrowDown, ArrowUp, CircleAlert, CircleCheck, FilePlus, FileUp, Hammer, Plus, Trash2, TriangleAlert } from "lucide-react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { useEffect, useMemo, useState } from "react";
import { useAuth } from "@/components/auth-gate";
import { PageIntro } from "@/components/page-intro";
import { getPackageInventory } from "@/lib/control-plane";
import {
  addSupportFile, describeScreen, emptyForm, emptyStep, formFromDraft, formFromInstaller, formProblems, idFromFileName, INSTALL_KIND_TEXT, isBuilderPackage,
  KIND_TEXT, loadDraft, readFileBase64, RUNTIMES, savePackage, scanBat, stepFromSuggestion, toRequest, uploadInstaller, uploadSupportFile,
  type BuilderForm, type BuilderKind, type BuildResult, type InstallerInfo, type InstallKind, type ScanResult,
  type ScreenSuggestion, type StepRow, type SupportFileInfo
} from "@/lib/package-builder";
import { packageStatus } from "@/lib/packages";
import { pageName } from "@/lib/pages";
import type { BatPackage } from "@/lib/types";

/**
 * Builds a package folder from a form instead of editing package.json by hand. The control
 * plane reads the BAT's text to suggest its screens; the person picks every answer.
 */
export function PackageBuilder() {
  const { session } = useAuth();
  const params = useSearchParams();
  const [form, setForm] = useState<BuilderForm>(() => emptyForm());
  const [file, setFile] = useState<{ fileName: string; fileBase64: string } | null>(null);
  const [existing, setExisting] = useState<{ id: string; signed: boolean; fileName?: string } | null>(null);
  const [scan, setScan] = useState<ScanResult | null>(null);
  const [installer, setInstaller] = useState<InstallerInfo | null>(null);
  const [support, setSupport] = useState<SupportFileInfo[]>([]);
  const [packages, setPackages] = useState<BatPackage[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [result, setResult] = useState<BuildResult>();
  const [showProblems, setShowProblems] = useState(false);

  const set = <K extends keyof BuilderForm>(key: K, value: BuilderForm[K]) => setForm(current => ({ ...current, [key]: value }));

  useEffect(() => {
    getPackageInventory().then(inventory => setPackages(inventory.packages.filter(isBuilderPackage)))
      .catch(() => setPackages([]));
  }, []);

  const openPackage = async (id: string) => {
    if (!id) return;
    setBusy(true); setError(undefined); setResult(undefined);
    try {
      const draft = await loadDraft(id);
      setForm(formFromDraft(draft));
      setScan(draft.scan ?? null);
      setFile(null); setInstaller(null); setSupport(draft.supportFiles ?? []);
      setExisting({ id, signed: draft.signed, fileName: draft.manifest.bat?.fileName ?? draft.manifest.file?.fileName });
    } catch (reason) { setError(reason instanceof Error ? reason.message : "The package could not be opened."); }
    finally { setBusy(false); }
  };

  const requested = params.get("id");
  useEffect(() => {
    if (!requested) return;
    const timer = window.setTimeout(() => void openPackage(requested), 0);
    return () => window.clearTimeout(timer);
  }, [requested]);

  const chooseFile = async (chosen: File | undefined) => {
    if (!chosen) return;
    setBusy(true); setError(undefined); setResult(undefined);
    try {
      if (form.kind === "install") {
        const info = await uploadInstaller(chosen);
        setInstaller(info);
        setForm(current => formFromInstaller(current, info, Boolean(existing)));
        return;
      }
      const fileBase64 = await readFileBase64(chosen);
      const scanned = await scanBat(chosen.name, fileBase64);
      setFile({ fileName: chosen.name, fileBase64 });
      setScan(scanned);
      setForm(current => current.id || existing ? current : { ...current, id: idFromFileName(chosen.name) });
    } catch (reason) { setError(reason instanceof Error ? reason.message : "The file could not be read."); }
    finally { setBusy(false); }
  };

  const startOver = () => {
    setForm(emptyForm()); setFile(null); setScan(null); setInstaller(null); setSupport([]); setExisting(null); setResult(undefined); setError(undefined); setShowProblems(false);
  };

  const install = form.kind === "install";
  const hasFile = Boolean((install ? installer : file) || existing?.fileName);
  const chooseKind = (kind: BuilderKind) => {
    if ((kind === "install") !== install) { setFile(null); setScan(null); setInstaller(null); setSupport([]); }
    setForm(current => ({ ...emptyForm(kind), ...pickCommon(current, kind === "install" || install), kind }));
  };
  const problems = useMemo(() => formProblems(form, hasFile), [form, hasFile]);

  const save = async () => {
    setShowProblems(true);
    if (problems.length > 0) return;
    if (existing?.signed && !window.confirm(`"${existing.id}" is signed and may be in use on PCs. Saving takes it off the PCs until it is signed again. Save anyway?`)) return;
    setBusy(true); setError(undefined); setResult(undefined);
    try {
      const saved = await savePackage(toRequest(form, install ? null : file, Boolean(existing),
        install ? installer?.uploadId ?? null : null, install ? null : support));
      setResult(saved);
      setSupport(current => current.map(item => ({ ...item, uploadId: null })));
      setExisting({ id: form.id, signed: false, fileName: (install ? installer?.fileName : file?.fileName) ?? existing?.fileName });
      setFile(null); setInstaller(null);
    } catch (reason) { setError(reason instanceof Error ? reason.message : "The package was not saved."); }
    finally { setBusy(false); }
  };

  const addSupport = async (chosen: FileList | null) => {
    if (!chosen?.length) return;
    setBusy(true); setError(undefined); setResult(undefined);
    try {
      for (const item of Array.from(chosen)) {
        const added = await uploadSupportFile(item);
        setSupport(current => addSupportFile(current, added));
      }
    } catch (reason) { setError(reason instanceof Error ? reason.message : "The file could not be uploaded."); }
    finally { setBusy(false); }
  };

  const suggestions = [
    ...(scan?.screens ?? []).map(screen => ({ screen, source: null as string | null })),
    ...support.flatMap(item => item.screens.map(screen => ({ screen, source: item.fileName })))
  ];

  const steps = form.steps;
  const setSteps = (next: StepRow[]) => set("steps", next);
  const updateStep = (key: string, patch: Partial<StepRow>) => setSteps(steps.map(step => step.key === key ? { ...step, ...patch } : step));
  const moveStep = (index: number, by: number) => {
    const next = [...steps];
    const [step] = next.splice(index, 1);
    next.splice(index + by, 0, step);
    setSteps(next);
  };

  return <div className="file-page builder-page">
    <section className="file-heading" aria-labelledby="builder-title">
      <div><div className="file-heading-mark"><Hammer size={17} /></div><div><h1 id="builder-title">{pageName("/package-builder/")}</h1></div></div>
      <button className="secondary-button" onClick={startOver} disabled={busy}>Start a new package</button>
    </section>
    <PageIntro route="/package-builder/" />

    {error && <div className="inline-error" role="alert"><CircleAlert size={17} /><div><strong>Something went wrong</strong><span>{error}</span></div></div>}

    <section className="builder-card" aria-labelledby="builder-file">
      <h2 id="builder-file">1. What you are making, and its file</h2>
      <fieldset className="builder-kinds" disabled={Boolean(existing) || busy}>
        <legend>What is it for?</legend>
        {(Object.keys(KIND_TEXT) as BuilderKind[]).map(kind => <label key={kind} className={form.kind === kind ? "selected" : undefined}>
          <input type="radio" name="kind" checked={form.kind === kind} onChange={() => chooseKind(kind)} />
          <strong>{KIND_TEXT[kind].name}</strong><span>{KIND_TEXT[kind].help}</span></label>)}
      </fieldset>
      <div className="builder-row">
        <label className="builder-upload"><FileUp size={16} /><span>{install
          ? installer ? installer.fileName : existing?.fileName ? `Keep ${existing.fileName} (or choose a new installer)` : "Choose an .msi or .exe installer"
          : file ? file.fileName : existing?.fileName ? `Keep ${existing.fileName} (or choose a new file)` : "Choose a .bat file"}</span>
          <input type="file" accept={install ? ".msi,.exe" : ".bat"} disabled={busy} onChange={event => { void chooseFile(event.target.files?.[0]); event.target.value = ""; }} /></label>
        {packages.length > 0 && <label className="builder-inline">or change a package
          <select value={existing?.id ?? ""} disabled={busy} onChange={event => void openPackage(event.target.value)}>
            <option value="">Choose…</option>
            {packages.map(item => <option key={item.id} value={item.id}>{item.displayName || item.id}</option>)}
          </select></label>}
      </div>
      {install
        ? <p className="builder-help">The control plane reads the installer&apos;s signature and type. It never runs it. Large installers are fine: the file goes straight to the server. Only package installers from the vendor that you are licensed to use.</p>
        : <p className="builder-help">The control plane only reads the file&apos;s text to suggest its screens. It never runs it. Only package files from a trusted source that you understand and are licensed to use.</p>}
      {busy && install && !installer && <p className="builder-help">Uploading and reading the installer…</p>}
      {existing && <p className="builder-help">Changing <code>{existing.id}</code>{existing.signed ? ". It is signed now; saving takes it off the PCs until it is signed again." : "."}</p>}
      {!install && scan && <ScanSummary scan={scan} />}
      {!install && <SupportFiles files={support} busy={busy} onAdd={files => void addSupport(files)}
        onRemove={name => setSupport(current => current.filter(item => item.fileName !== name))} />}
      {install && installer && <InstallerSummary info={installer} />}
    </section>

    <section className="builder-card" aria-labelledby="builder-about">
      <h2 id="builder-about">2. About the package</h2>
      <div className="builder-grid">
        <label>Id (folder name)<input value={form.id} disabled={Boolean(existing)} onChange={event => set("id", event.target.value.toLowerCase())} placeholder={install ? "7zip-install" : "office-reseller-fix"} maxLength={64} /></label>
        <label>Name shown to the helpdesk<input value={form.displayName} onChange={event => set("displayName", event.target.value)} maxLength={120} placeholder={install ? "Install 7-Zip" : "Office activation (reseller BAT)"} /></label>
        {install && <label>Application name (as users know it)<input value={form.appName} onChange={event => set("appName", event.target.value)} maxLength={120} placeholder="7-Zip" /></label>}
      </div>
      <label className="builder-wide">What it does and when to use it<textarea rows={2} value={form.description} onChange={event => set("description", event.target.value)} maxLength={500} /></label>
      {(form.kind === "general" || install) && <>
        <label className="builder-wide">{install ? "How users ask for it (one per line, Persian is fine)" : "How users describe the problem (one per line, Persian is fine)"}<textarea dir="auto" rows={3} value={form.symptoms} onChange={event => set("symptoms", event.target.value)} /></label>
        <div className="builder-grid">
          <label>Problem group (optional)<input value={form.problem} onChange={event => set("problem", event.target.value.toLowerCase())} placeholder={install ? `install-${form.id.replace(/-install$/, "") || "app"}` : "printing-stuck"} /></label>
          <label>Try order in the group (1–999)<input value={form.order} inputMode="numeric" onChange={event => set("order", event.target.value)} placeholder="100" /></label>
          <label className="builder-check"><input type="checkbox" checked={form.requiresApproval} onChange={event => set("requiresApproval", event.target.checked)} />Wait for a helpdesk approval before each run</label>
          {install && <label className="builder-check"><input type="checkbox" checked={form.licensed} onChange={event => set("licensed", event.target.checked)} />Licensed software (every install always waits for a helpdesk approval)</label>}
        </div>
      </>}
    </section>

    {install && <InstallSection form={form} set={set} installer={installer} />}

    {!install && <section className="builder-card" aria-labelledby="builder-screens">
      <h2 id="builder-screens">3. The screens and your answers</h2>
      <p className="builder-help">Add one step for every screen where the BAT waits, in the order they appear. Click an answer under a suggestion to add it as a step, or add a step by hand. To be sure of the exact text, run the BAT once on a test PC and copy what the screen shows. Leave the list empty if the BAT needs no answers.</p>
      <div className="builder-columns">
        <div className="builder-suggestions" aria-label="Screens found in the BAT">
          <h3>Found in the BAT{scan || support.length ? ` (${suggestions.length})` : ""}</h3>
          {!scan && suggestions.length === 0 && <p className="builder-help">Upload the BAT to see its menus and questions here.</p>}
          {suggestions.map(({ screen, source }) => <SuggestionCard key={`${source ?? ""}:${screen.line}`} screen={screen} source={source}
            onAdd={answer => setSteps([...steps, stepFromSuggestion(screen, answer)])} />)}
        </div>
        <div className="builder-steps">
          <h3>Playbook steps ({steps.length})</h3>
          {steps.length === 0 && <p className="builder-help">No steps: the BAT gets no answers and must run to the end by itself.</p>}
          <ol>{steps.map((step, index) => <li key={step.key} className="builder-step">
            <div className="builder-step-head"><strong>Step {index + 1}</strong>{step.note && <span className="file-chip">{step.note}</span>}
              <span className="builder-step-tools">
                <button type="button" aria-label="Move up" disabled={index === 0} onClick={() => moveStep(index, -1)}><ArrowUp size={14} /></button>
                <button type="button" aria-label="Move down" disabled={index === steps.length - 1} onClick={() => moveStep(index, 1)}><ArrowDown size={14} /></button>
                <button type="button" aria-label="Remove step" onClick={() => setSteps(steps.filter(item => item.key !== step.key))}><Trash2 size={14} /></button>
              </span></div>
            <label>When the screen shows<input value={step.expect} onChange={event => updateStep(step.key, { expect: event.target.value })} placeholder="Select an option [1-6]:" /></label>
            <div className="builder-step-grid">
              <label>Answer<input value={step.send} maxLength={64} onChange={event => updateStep(step.key, { send: event.target.value })} placeholder="1" /></label>
              <label>How<select value={step.submit} onChange={event => updateStep(step.key, { submit: event.target.value as StepRow["submit"] })}>
                <option value="line">Type it and press Enter</option><option value="key">Press one key</option></select></label>
              <label>Wait up to (s)<input value={step.timeoutSeconds} inputMode="numeric" onChange={event => updateStep(step.key, { timeoutSeconds: event.target.value })} /></label>
              <label>Repeats<input value={step.maxRepeats} inputMode="numeric" onChange={event => updateStep(step.key, { maxRepeats: event.target.value })} /></label>
            </div>
            <label className="builder-check"><input type="checkbox" checked={step.optional} onChange={event => updateStep(step.key, { optional: event.target.checked })} />This screen does not always appear (skip it if it is not shown)</label>
          </li>)}</ol>
          <button type="button" className="secondary-button" onClick={() => setSteps([...steps, emptyStep()])}><Plus size={15} />Add a step by hand</button>
        </div>
      </div>
    </section>}

    {!install && <section className="builder-card" aria-labelledby="builder-finish">
      <h2 id="builder-finish">4. How to tell it worked</h2>
      <div className="builder-grid">
        <label>Text shown only when it worked<input value={form.successMarker} onChange={event => set("successMarker", event.target.value)} placeholder="Product activation successful" maxLength={128} /></label>
        <label>Exit codes that mean success<input value={form.successExitCodes} onChange={event => set("successExitCodes", event.target.value)} placeholder="0" /></label>
      </div>
      <label className="builder-wide">Texts that mean it failed (one per line; the run stops at once)<textarea rows={2} value={form.failureMarkers} onChange={event => set("failureMarkers", event.target.value)} placeholder={"Activation failed\nError:"} /></label>
      {(form.kind === "general" || steps.length === 0) && <div className="builder-grid">
        <label>Fixed arguments (one per line, optional)<textarea rows={2} value={form.arguments} onChange={event => set("arguments", event.target.value)} placeholder="/quiet" /></label>
        <label>Whole run may take up to (s)<input value={form.runTimeoutSeconds} inputMode="numeric" onChange={event => set("runTimeoutSeconds", event.target.value)} /></label>
        <label>Exit codes that mean &quot;worked, restart needed&quot;<input value={form.restartExitCodes} onChange={event => set("restartExitCodes", event.target.value)} placeholder="3010" /></label>
      </div>}
    </section>}

    <section className="builder-card" aria-labelledby="builder-fits">
      <h2 id="builder-fits">{install ? 4 : 5}. Which PCs it fits</h2>
      {form.kind === "office" && <div className="builder-grid">
        <label>Lowest Office version<input value={form.minOfficeVersion} onChange={event => set("minOfficeVersion", event.target.value)} /></label>
        <label>Below Office version<input value={form.maxOfficeVersionExclusive} onChange={event => set("maxOfficeVersionExclusive", event.target.value)} /></label>
        <label>Office products (comma-separated, empty = all)<input value={form.officeProductReleaseIds} onChange={event => set("officeProductReleaseIds", event.target.value)} placeholder="ProPlus2021Volume, ProPlus2019Volume" /></label>
      </div>}
      {form.kind !== "office" && <div className="builder-grid">
        <label>Lowest Windows build<input value={form.minWindowsVersion} onChange={event => set("minWindowsVersion", event.target.value)} placeholder="10.0.19041" /></label>
        <label>Below Windows build{form.kind === "general" || install ? " (optional)" : ""}<input value={form.maxWindowsVersionExclusive} onChange={event => set("maxWindowsVersionExclusive", event.target.value)} placeholder="10.0.30000" /></label>
        {form.kind === "windows" && <label>Windows editions (comma-separated, empty = all)<input value={form.editionIds} onChange={event => set("editionIds", event.target.value)} placeholder="Professional, Enterprise" /></label>}
      </div>}
      {install && <Requirements form={form} set={set} />}
      {form.kind === "office" && <p className="builder-help">Office 2016 to 2024 all report version 16.0, so list the products the BAT really supports. To see a PC&apos;s Office, run &quot;Office inventory&quot; from the HelpDesk page.</p>}
    </section>

    <section className="builder-card" aria-labelledby="builder-review">
      <h2 id="builder-review">{install ? 5 : 6}. Review</h2>
      <div className="builder-grid">
        <label>Reviewed by<input value={form.reviewedBy} onChange={event => set("reviewedBy", event.target.value)} placeholder={session.displayName || session.userName} /></label>
        <label>Where the file came from<input value={form.source} onChange={event => set("source", event.target.value)} placeholder={install ? "Vendor website, downloaded 2026-11-25" : "Office reseller, email of 2026-11-10"} /></label>
      </div>
      <label className="builder-wide">Notes<textarea rows={2} value={form.notes} onChange={event => set("notes", event.target.value)} /></label>
    </section>

    {showProblems && problems.length > 0 && <div className="inline-error" role="alert"><CircleAlert size={17} /><div><strong>Fix these first</strong>
      <ul className="builder-problems">{problems.map(problem => <li key={problem}>{problem}</li>)}</ul></div></div>}

    <div className="builder-actions">
      <button className="primary-button" onClick={() => void save()} disabled={busy}>{busy ? "Working…" : existing ? "Save changes" : "Create the package"}</button>
      <span>Saves a draft with all hashes filled in. No PC gets it until an admin approves it on the Packages page.</span>
    </div>

    {result?.saved && <SaveResult result={result} install={install} />}
  </div>;
}

/** Fields that stay when the kind changes; switching to or from installs keeps only the review. */
function pickCommon(form: BuilderForm, crossesInstall: boolean): Partial<BuilderForm> {
  const { id, displayName, description, steps, successMarker, failureMarkers, successExitCodes, reviewedBy, source, notes } = form;
  return crossesInstall ? { reviewedBy, source, notes }
    : { id, displayName, description, steps, successMarker, failureMarkers, successExitCodes, reviewedBy, source, notes };
}

type SetField = <K extends keyof BuilderForm>(key: K, value: BuilderForm[K]) => void;

function InstallerSummary({ info }: { info: InstallerInfo }) {
  return <div className="builder-scan">
    <p><strong>{info.fileName}</strong> · {Math.max(1, Math.round(info.sizeBytes / 1024 / 1024))} MB · SHA-256 <code>{info.sha256.slice(0, 16)}…</code></p>
    <p>Type: <strong>{INSTALL_KIND_TEXT[info.technology]}</strong>{info.productName && <> · Product: <strong>{info.productName}</strong></>}</p>
    {info.signer
      ? <p><CircleCheck size={13} /> Signed by <code>{info.signer}</code>. PCs check this signer again before they install it.</p>
      : <div className="builder-lint blocked"><strong><CircleAlert size={14} /> Not signed by its vendor. It cannot be saved.</strong></div>}
    {info.notes.filter(note => !note.includes("Authenticode")).map(note => <p key={note} className="builder-help">{note}</p>)}
  </div>;
}

/** How the PC runs the installer and how it checks the install worked. */
function InstallSection({ form, set, installer }: { form: BuilderForm; set: SetField; installer: InstallerInfo | null }) {
  const msi = form.installKind === "msi";
  const detected = installer && installer.technology === form.installKind;
  return <section className="builder-card" aria-labelledby="builder-install">
    <h2 id="builder-install">3. How it installs, and how to tell it worked</h2>
    <div className="builder-grid">
      <label>Installer type<select value={form.installKind} onChange={event => set("installKind", event.target.value as InstallKind)}>
        {(Object.keys(INSTALL_KIND_TEXT) as InstallKind[]).filter(kind => installer ? (kind === "msi") === (installer.fileType === "msi") : true)
          .map(kind => <option key={kind} value={kind}>{INSTALL_KIND_TEXT[kind]}</option>)}
      </select>{detected && <span className="builder-help">Found in the file. Change it only if the vendor says otherwise.</span>}</label>
      {msi
        ? <label>MSI properties (one NAME=value per line, optional)<textarea rows={2} value={form.msiProperties} onChange={event => set("msiProperties", event.target.value)} placeholder={"ALLUSERS=1\nAUTO_UPDATE=0"} /></label>
        : <label>{form.installKind === "exe" ? "The vendor's silent-install switches" : "Extra switches (optional)"}<input value={form.installArguments} onChange={event => set("installArguments", event.target.value)} placeholder={form.installKind === "exe" ? "/silent /norestart" : "/NOICONS"} /></label>}
      <label>The install may take up to (s)<input value={form.installTimeoutSeconds} inputMode="numeric" onChange={event => set("installTimeoutSeconds", event.target.value)} /></label>
    </div>
    <div className="builder-grid">
      <label>Name in Apps &amp; features (the PC checks this after installing)<input value={form.detectName} onChange={event => set("detectName", event.target.value)} placeholder="7-Zip" maxLength={120} /></label>
      <label>Or this name (optional)<input value={form.detectNameAlt} onChange={event => set("detectNameAlt", event.target.value)} placeholder="7-Zip (x64)" maxLength={120} /></label>
    </div>
    <p className="builder-help">The name matches from its start, so &quot;7-Zip&quot; also matches &quot;7-Zip 24.08 (x64)&quot;. Leave version numbers out: they change with every release. To be sure, install the app on a test PC and copy its name from Settings &gt; Apps.</p>
    <p className="builder-help">Installers whose wizard asks questions (recorded answers) and installers with no silent mode still need new-install-package.ps1.</p>
  </section>;
}

function Requirements({ form, set }: { form: BuilderForm; set: SetField }) {
  const toggle = (key: "architectures" | "runtimes", value: string) =>
    set(key, form[key].includes(value) ? form[key].filter(item => item !== value) : [...form[key], value]);
  return <>
    <h3>System requirements (from the vendor&apos;s page; each PC is checked before installing)</h3>
    <div className="builder-grid">
      <label>Lowest Windows build number (optional)<input value={form.minWindowsBuild} inputMode="numeric" onChange={event => set("minWindowsBuild", event.target.value)} placeholder="19045" /></label>
      <label>Memory at least (GB, optional)<input value={form.minMemoryGB} inputMode="decimal" onChange={event => set("minMemoryGB", event.target.value)} placeholder="8" /></label>
      <label>Free space on C: at least (GB, optional)<input value={form.minFreeDiskGB} inputMode="decimal" onChange={event => set("minFreeDiskGB", event.target.value)} placeholder="4" /></label>
    </div>
    <fieldset className="builder-checks"><legend>Processor (none ticked = any)</legend>
      {["x64", "x86", "arm64"].map(value => <label key={value} className="builder-check"><input type="checkbox" checked={form.architectures.includes(value)} onChange={() => toggle("architectures", value)} />{value}</label>)}
    </fieldset>
    <fieldset className="builder-checks"><legend>Needs these runtimes (installed first when the catalog has them)</legend>
      {RUNTIMES.map(value => <label key={value} className="builder-check"><input type="checkbox" checked={form.runtimes.includes(value)} onChange={() => toggle("runtimes", value)} />{value}</label>)}
    </fieldset>
    <label className="builder-check"><input type="checkbox" checked={form.domainJoined} onChange={event => set("domainJoined", event.target.checked)} />Only PCs in the domain</label>
  </>;
}

/** The package's files\ folder: each file is pinned by the hash the control plane works out. */
function SupportFiles({ files, busy, onAdd, onRemove }: {
  files: SupportFileInfo[]; busy: boolean; onAdd: (files: FileList | null) => void; onRemove: (name: string) => void;
}) {
  return <div className="builder-support">
    <h3>Supporting files (optional)</h3>
    <p className="builder-help">Files the BAT uses while it runs, such as a tool, a CAB or a menu text file. They go in the package&apos;s <code>files\</code> folder next to the BAT, and each one is pinned by its SHA-256, so PCs refuse to run the package if any file changes. Menus in text files are added to the suggestions below.</p>
    {files.length > 0 && <ul className="builder-support-list">{files.map(item => <li key={item.fileName}>
      <div><strong>{item.fileName}</strong>{item.uploadId && <span className="file-chip">new</span>}
        <span>{item.sizeBytes ? `${Math.max(1, Math.round(item.sizeBytes / 1024))} KB · ` : ""}SHA-256 <code>{item.sha256.slice(0, 16)}…</code>
          {item.signer && <> · signed by <code>{item.signer}</code></>}</span>
        {item.notes.map(note => <span key={note} className="builder-help">{note}</span>)}</div>
      <button type="button" aria-label={`Remove ${item.fileName}`} disabled={busy} onClick={() => onRemove(item.fileName)}><Trash2 size={14} /></button>
    </li>)}</ul>}
    <label className="builder-upload"><FilePlus size={16} /><span>Add supporting files</span>
      <input type="file" multiple disabled={busy} onChange={event => { onAdd(event.target.files); event.target.value = ""; }} /></label>
  </div>;
}

function ScanSummary({ scan }: { scan: ScanResult }) {
  return <div className="builder-scan">
    <p><strong>{scan.fileName}</strong> · {scan.lineCount} lines · {Math.max(1, Math.round(scan.sizeBytes / 1024))} KB · SHA-256 <code>{scan.sha256.slice(0, 16)}…</code></p>
    {scan.lintErrors.length > 0 && <div className="builder-lint blocked"><strong><CircleAlert size={14} /> PCs will refuse this file until these are fixed or accepted:</strong>
      <ul>{scan.lintErrors.map(line => <li key={line}>{line}</li>)}</ul></div>}
    {scan.lintWarnings.length > 0 && <div className="builder-lint warning"><strong><TriangleAlert size={14} /> Warnings (allowed):</strong>
      <ul>{scan.lintWarnings.map(line => <li key={line}>{line}</li>)}</ul></div>}
    {scan.notes.map(note => <p key={note} className="builder-help">{note}</p>)}
  </div>;
}

function SuggestionCard({ screen, source, onAdd }: { screen: ScreenSuggestion; source: string | null; onAdd: (answer: string) => void }) {
  const [custom, setCustom] = useState("");
  return <article className="builder-suggestion">
    <h4>{describeScreen(screen)}{source && <> in <code>{source}</code></>}</h4>
    {screen.shown.length > 0 && <pre className="builder-screen" dir="auto">{screen.shown.join("\n")}</pre>}
    <p className="builder-expect">Waits for: {screen.expect ? <code>{screen.expect}</code> : <em>no fixed text found</em>}</p>
    {screen.notes.map(note => <p key={note} className="builder-help">{note}</p>)}
    <div className="builder-options">
      {screen.options.map(option => <button key={option.value} type="button" className="builder-option" onClick={() => onAdd(option.value)}
        title={option.goesTo ? `Goes to :${option.goesTo}` : undefined}>
        <strong>{option.value}</strong>{option.label && <span>{option.label}</span>}{option.goesTo && <small>→ :{option.goesTo}</small>}</button>)}
    </div>
    {screen.kind !== "pause" && <form className="builder-custom" onSubmit={event => { event.preventDefault(); if (custom.trim()) { onAdd(custom.trim()); setCustom(""); } }}>
      <input value={custom} onChange={event => setCustom(event.target.value)} placeholder="Other answer" maxLength={64} aria-label="Other answer" />
      <button type="submit" className="secondary-button" disabled={!custom.trim()}>Add</button>
    </form>}
  </article>;
}

function SaveResult({ result, install }: { result: BuildResult; install: boolean }) {
  const status = result.status;
  if (!status) return <div className="builder-result"><CircleCheck size={18} /><div><strong>Saved</strong><span>Refresh the Packages page to see it.</span></div></div>;
  const tone = packageStatus(status.status);
  const unsignedOnly = status.status === "Unsigned";
  return <div className={`builder-result ${tone.tone}`}>
    {tone.tone === "ready" ? <CircleCheck size={18} /> : <TriangleAlert size={18} />}
    <div>
      <strong>Saved {status.id}: {tone.label}</strong>
      {status.reasons.length > 0 && <ul>{status.reasons.map(reason => <li key={reason}>{reason}</li>)}</ul>}
      {status.warnings.length > 0 && <ul>{status.warnings.map(warning => <li key={warning}>{warning}</li>)}</ul>}
      {unsignedOnly && <p>Saved as a draft, and everything else is in order. Next, an admin (you, if you are one) opens the <Link href="/files/">Packages page</Link>, checks the steps and clicks <strong>Approve and sign</strong>. No PC gets it before that. To try it on a test PC first{install ? " (this really installs it)" : ""}:
        <code className="builder-command">.\scripts\test-bat-playbook.ps1 -Package &quot;{status.folder}&quot; -Execute</code></p>}
      <p><Link href="/files/">Open the Packages page</Link></p>
    </div>
  </div>;
}
