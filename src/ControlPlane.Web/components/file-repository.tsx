"use client";

import { Check, CircleAlert, Copy, Download, FileArchive, LockKeyhole, RefreshCw, Server } from "lucide-react";
import { useCallback, useEffect, useState } from "react";
import { CONTROL_PLANE_URL, getFileInventory } from "@/lib/control-plane";
import { fileCompatibilityLabel, fileRequirementLabels, fileRequiresEndpointToken } from "@/lib/files";
import type { EndpointFile, FileInventory } from "@/lib/types";

function formatBytes(value: number) {
  if (value < 1024) return `${value} B`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`;
  return `${(value / (1024 * 1024)).toFixed(1)} MB`;
}

function formatDate(value: string) {
  return new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
}

/** Operator view of the read-only artifact repository used by endpoint agents. */
export function FileRepository() {
  const [inventory, setInventory] = useState<FileInventory>();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string>();
  const [copied, setCopied] = useState(false);

  const refresh = useCallback(async () => {
    setLoading(true);
    try {
      setInventory(await getFileInventory());
      setError(undefined);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : "The file inventory could not be loaded.");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    const initial = window.setTimeout(() => void refresh(), 0);
    return () => window.clearTimeout(initial);
  }, [refresh]);

  const copyUrl = async (file: EndpointFile) => {
    await navigator.clipboard.writeText(`${CONTROL_PLANE_URL}${file.downloadPath}`);
    setCopied(true);
    window.setTimeout(() => setCopied(false), 1500);
  };

  const files = inventory?.files ?? [];

  return <div className="file-page">
    <section className="file-heading" aria-labelledby="files-title">
      <div><div className="file-heading-mark"><FileArchive size={17} /></div><div><h1 id="files-title">Endpoint file repository</h1>
        <p>Approved files that endpoint agents can download from this control plane.</p></div></div>
      <button className="secondary-button" onClick={() => void refresh()} disabled={loading}><RefreshCw className={loading ? "spin" : ""} size={15} />Refresh</button>
    </section>

    {error && <div className="inline-error" role="alert"><CircleAlert size={17} /><div><strong>File inventory is unavailable</strong><span>{error}</span></div><button onClick={() => void refresh()}>Retry</button></div>}

    <section className="file-route" aria-label="Endpoint download route">
      <div className="file-route-icon"><Server size={17} /></div><div><strong>Endpoint download routes</strong><span>GET {CONTROL_PLANE_URL}/api/files/ibsng-dialer · /api/files/office/&#123;artifactId&#125; · /api/files/office/bugfix/&#123;artifactId&#125; · /api/files/windows/bugfix/&#123;artifactId&#125;</span></div>
      <span className="file-route-status"><span className="online-indicator" />Read-only</span>
    </section>

    {inventory?.issues?.map(issue => <div className="file-warning" role="status" key={`${issue.source}-${issue.message}`}><CircleAlert size={15} /><span><strong>{issue.source}</strong>{issue.message}</span></div>)}

    {loading ? <div className="file-list" aria-label="Loading files"><div className="file-skeleton" /><div className="file-skeleton" /></div> : files.length === 0 ?
      <section className="file-empty"><span className="empty-icon"><FileArchive size={23} /></span><h2>No endpoint files available</h2>
        <p>Place the IBSng file, add Microsoft remediation entries to <code>office-artifacts.json</code>, or register approved Office BAT files in <code>office-bugfix-managers.json</code>. Endpoint downloads are SHA-256 verified.</p>
        <code className="repository-path">C:\ProgramData\EndpointAgents\ControlPlane\FileRepository</code>
      </section> :
      <section className="file-list" aria-label="Available endpoint files">{files.map(file => <FileRow key={file.artifactId} file={file} copied={copied} onCopy={() => void copyUrl(file)} />)}</section>}

    <p className="file-note">Remote upload is intentionally disabled while the control plane has no authentication. An administrator manages the repository folder directly. For remote endpoints, set <code>ControlPlane:HubUrl</code> to the control-plane hostname or IP; <code>localhost</code> only works when the agent and API share the same machine.</p>
  </div>;
}

function FileRow({ file, copied, onCopy }: { file: EndpointFile; copied: boolean; onCopy: () => void }) {
  const url = `${CONTROL_PLANE_URL}${file.downloadPath}`;
  const requirements = fileRequirementLabels(file);
  const requiresEndpointToken = fileRequiresEndpointToken(file);
  return <article className="file-row">
    <div className="file-row-icon"><FileArchive size={20} /></div><div className="file-row-main"><div className="file-row-title"><strong>{file.displayName ?? file.fileName}</strong><span className="file-chip">{file.category}</span>{file.kind && <span className="file-chip">{file.kind}</span>}<span className="file-available"><span className="online-indicator" />Available</span></div>
      {file.description && <p className="file-description">{file.description}</p>}
      <p className="file-compatibility">{fileCompatibilityLabel(file)}</p>
      {requirements.length > 0 && <div className="file-tags">{requirements.map(requirement => <span key={requirement}>{requirement}</span>)}</div>}
      <dl><div><dt>Size</dt><dd>{formatBytes(file.sizeBytes)}</dd></div><div><dt>Modified</dt><dd>{formatDate(file.lastModifiedUtc)}</dd></div><div className="file-hash"><dt>SHA-256</dt><dd title={file.sha256}>{file.sha256}</dd></div></dl>
      <code className="file-url">{file.fileName} · {url}</code>
    </div><div className="file-row-actions"><button type="button" className="secondary-button" onClick={onCopy}>{copied ? <Check size={14} /> : <Copy size={14} />}{copied ? "Copied" : requiresEndpointToken ? "Copy route" : "Copy URL"}</button>{requiresEndpointToken
      ? <button type="button" className="primary-button" disabled title="Requires an enrolled endpoint device token."><LockKeyhole size={14} />Endpoint only</button>
      : <a className="primary-button" href={url} download={file.fileName}><Download size={14} />Download</a>}</div>
  </article>;
}
