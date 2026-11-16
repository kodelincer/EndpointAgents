"use client";

import { CheckCircle2, CircleAlert, Download, FilePlus2, History, MessageSquareText, RefreshCw, Send, ShieldCheck,
  Sparkles, Trash2 } from "lucide-react";
import { useSearchParams } from "next/navigation";
import { useCallback, useEffect, useState } from "react";
import { useAuth } from "@/components/auth-gate";
import { PageIntro } from "@/components/page-intro";
import { pageName } from "@/lib/pages";
import { draftFromConversation, getSkill, getSkills, getSkillTemplate, issueCounts, lintSkill, publishSkill,
  rejectSkill, retireSkill, saveSkillDraft, skillDownloadUrl, skillStatusText, submitSkill, type HelpdeskSkill,
  type SkillDetail, type SkillDraft, type SkillIssue, type SkillStats } from "@/lib/capabilities";

const blank: SkillDraft = { name: "", description: "", body: "" };

/** Helpdesk members write Hermes skills here; a reviewer publishes them into Hermes. */
export function SkillsEditor() {
  const { can } = useAuth();
  const reviewer = can("skills.publish");
  const params = useSearchParams();
  const [skills, setSkills] = useState<{ skill: HelpdeskSkill; stats?: SkillStats | null }[]>([]);
  const [selected, setSelected] = useState<SkillDetail>();
  const [draft, setDraft] = useState<SkillDraft>(blank);
  const [issues, setIssues] = useState<SkillIssue[]>();
  const [note, setNote] = useState("");
  const [conversation, setConversation] = useState(params.get("conversation") ?? "");
  const [error, setError] = useState<string>();
  const [notice, setNotice] = useState<string>();
  const [busy, setBusy] = useState(false);

  const refresh = useCallback(async () => {
    try { setSkills(await getSkills()); setError(undefined); }
    catch (reason) { setError(reason instanceof Error ? reason.message : "Skills could not be loaded."); }
  }, []);
  useEffect(() => { const id = window.setTimeout(() => void refresh(), 0); return () => window.clearTimeout(id); }, [refresh]);

  const act = async (work: () => Promise<unknown>, done: string) => {
    setBusy(true);
    try { await work(); if (done) setNotice(done); setError(undefined); await refresh(); }
    catch (reason) { setError(reason instanceof Error ? reason.message : "That did not work."); setNotice(undefined); }
    finally { setBusy(false); }
  };

  const open = (name: string) => void act(async () => {
    const detail = await getSkill(name);
    setSelected(detail); setIssues(detail.issues); setNote("");
    setDraft({ name: detail.skill.name, description: detail.skill.description, body: detail.skill.body,
      sourceConversationId: detail.skill.sourceConversationId });
  }, "");
  const startNew = () => void act(async () => {
    setSelected(undefined); setIssues(undefined);
    setDraft({ ...blank, body: (await getSkillTemplate()).body });
  }, "");
  const fromConversation = () => void act(async () => {
    setSelected(undefined); setIssues(undefined);
    setDraft(await draftFromConversation(conversation.trim()));
  }, "Draft started from the conversation. Rename it and edit the steps.");
  const check = () => void act(async () => setIssues(await lintSkill(draft)), "Checked.");
  const save = () => void act(async () => {
    const saved = await saveSkillDraft(draft);
    setIssues(saved.issues);
    setSelected(await getSkill(saved.skill.name));
  }, "Draft saved.");
  const reload = async () => draft.name && setSelected(await getSkill(draft.name));

  const counts = issues ? issueCounts(issues) : undefined;
  const status = selected?.skill.status;

  return <div className="file-page skills-page">
    <section className="file-heading" aria-labelledby="skills-title">
      <div><div className="file-heading-mark"><Sparkles size={17} /></div><div>
        <h1 id="skills-title">{pageName("/skills/")}</h1></div></div>
      <button className="secondary-button" onClick={() => void refresh()}><RefreshCw size={15} />Refresh</button>
    </section>
    <PageIntro route="/skills/" />

    {error && <div className="inline-error" role="alert"><CircleAlert size={17} />
      <div><strong>Not done</strong><span>{error}</span></div><button onClick={() => setError(undefined)}>Dismiss</button></div>}
    {notice && !error && <p className="success-text" role="status">{notice}</p>}

    <div className="skills-layout">
      <aside className="skills-list" aria-label="Skills">
        <button className="primary-button" onClick={startNew}><FilePlus2 size={14} />New skill</button>
        <div className="skills-from">
          <input value={conversation} onChange={event => setConversation(event.target.value)} placeholder="Conversation id" aria-label="Conversation id" />
          <button className="secondary-button" disabled={!conversation.trim()} onClick={fromConversation}>
            <MessageSquareText size={14} />From conversation</button>
        </div>
        {skills.length === 0 && <p className="capability-note">No helpdesk skills yet.</p>}
        {skills.map(({ skill, stats }) => <button key={skill.name} className={`skills-item ${selected?.skill.name === skill.name ? "selected" : ""}`}
          onClick={() => open(skill.name)}>
          <strong>{skill.name}</strong>
          <span className={`ticket-status ${skill.status.toLowerCase()}`}>{skillStatusText[skill.status]}</span>
          <small>v{skill.version}{skill.publishedVersion ? ` · live v${skill.publishedVersion}` : ""}
            {stats ? ` · ${stats.conversations} chats, ${stats.resolved} solved, ${stats.escalated} escalated` : ""}</small>
        </button>)}
      </aside>

      <section className="skills-editor" aria-label="Skill editor">
        <label>Name <small>(lowercase, dashes; published as hd-name)</small>
          <input value={draft.name} disabled={Boolean(selected)} onChange={event => setDraft({ ...draft, name: event.target.value })}
            placeholder="vpn-after-password-change" /></label>
        <label>Description <small>(one line: which user problems it is for; include Persian phrases users write)</small>
          <input value={draft.description} dir="auto" onChange={event => setDraft({ ...draft, description: event.target.value })} /></label>
        <label>Steps
          <textarea value={draft.body} dir="auto" rows={20} onChange={event => setDraft({ ...draft, body: event.target.value })} /></label>
        <label>Note for the reviewer or change note<input value={note} dir="auto" onChange={event => setNote(event.target.value)} /></label>

        {issues && <div className={`skills-issues ${counts?.errors ? "has-errors" : ""}`} role="status">
          {issues.length === 0 ? <p><CheckCircle2 size={14} /> No problems found.</p>
            : issues.map((issue, index) => <p key={index} className={issue.severity}>
              <strong>{issue.severity === "error" ? "Error" : "Warning"}:</strong> {issue.message}</p>)}</div>}

        <div className="skills-actions">
          <button className="secondary-button" disabled={busy} onClick={check}><ShieldCheck size={14} />Check</button>
          <button className="primary-button" disabled={busy || !draft.name || !draft.body} onClick={save}>Save draft</button>
          {selected && status === "Draft" && <button className="secondary-button" disabled={busy}
            onClick={() => void act(async () => { await submitSkill(draft.name); await reload(); }, "Submitted for review.")}>
            <Send size={14} />Submit for review</button>}
          {selected && reviewer && (status === "InReview" || status === "Draft") && <button className="primary-button" disabled={busy}
            onClick={() => void act(async () => {
              const result = await publishSkill(draft.name, null, note);
              setIssues(result.issues);
              if (!result.published) throw new Error(result.error ?? "Not published.");
              setNotice(result.writtenToHermes ? "Published into Hermes. New conversations use it."
                : "Published. Download SKILL.md and copy it into the Hermes skills folder.");
              await reload();
            }, "")}>Publish (reviewer)</button>}
          {selected && reviewer && status === "InReview" && <button className="secondary-button" disabled={busy}
            onClick={() => void act(async () => { await rejectSkill(draft.name, note); await reload(); }, "Sent back to the author.")}>Send back</button>}
          {selected && reviewer && status === "Published" && <button className="secondary-button" disabled={busy}
            onClick={() => window.confirm("Retire this skill? Hermes stops using it.") && void act(async () => {
              await retireSkill(draft.name, note); await reload(); }, "Retired.")}><Trash2 size={14} />Retire</button>}
          {selected && <a className="secondary-button" href={skillDownloadUrl(draft.name)} download={`${draft.name}-SKILL.md`}>
            <Download size={14} />SKILL.md</a>}
        </div>
        {selected?.skill.reviewNote && <p className="capability-note" dir="auto">Review note: {selected.skill.reviewNote}</p>}

        {selected && selected.versions.length > 1 && <section className="skills-versions" aria-label="Versions">
          <h2><History size={14} /> Versions</h2>
          {selected.versions.map(version => <div key={version.version} className="skills-version">
            <span>v{version.version} · {version.author} · {new Date(version.createdAtUtc).toLocaleString()}
              {version.note && <> · <span dir="auto">{version.note}</span></>}</span>
            {reviewer && version.version !== selected.skill.version && <button className="secondary-button" disabled={busy}
              onClick={() => window.confirm(`Publish version ${version.version} again?`) && void act(async () => {
                await publishSkill(draft.name, version.version, `Rollback to v${version.version}`); await reload();
              }, `Rolled back to v${version.version}.`)}>Roll back to this</button>}
          </div>)}
        </section>}
      </section>
    </div>
  </div>;
}
