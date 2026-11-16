"use client";

import { CircleAlert, GraduationCap, RefreshCw } from "lucide-react";
import Link from "next/link";
import { useCallback, useEffect, useState } from "react";
import { useAuth } from "@/components/auth-gate";
import { getLessons, outcomeLine, patchLesson, skillDraftHref, statusText, type LessonPage, type LessonStatus } from "@/lib/lessons";
import { PageIntro } from "@/components/page-intro";
import { pageName } from "@/lib/pages";

const tabs: (LessonStatus | undefined)[] = ["New", "Drafted", "Kept", "Dropped", undefined];

/** Supervised learning: each solved chat leaves a lesson; people decide; only published skills reach the agent. */
export function Lessons() {
  const { can } = useAuth();
  const reviewer = can("skills.publish");
  const [tab, setTab] = useState<LessonStatus | undefined>("New");
  const [page, setPage] = useState<LessonPage>();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string>();
  const [notes, setNotes] = useState<Record<string, string>>({});

  const refresh = useCallback(async () => {
    setLoading(true);
    try { setPage(await getLessons(tab)); setError(undefined); }
    catch (reason) { setError(reason instanceof Error ? reason.message : "Lessons could not be loaded."); }
    finally { setLoading(false); }
  }, [tab]);

  useEffect(() => {
    const initial = window.setTimeout(() => void refresh(), 0);
    return () => window.clearTimeout(initial);
  }, [refresh]);

  const decide = async (lessonId: string, status: LessonStatus) => {
    try { await patchLesson(lessonId, { status, note: notes[lessonId] }); await refresh(); }
    catch (reason) { setError(reason instanceof Error ? reason.message : "Not saved."); }
  };

  const lessons = page?.lessons ?? [];
  return <div className="file-page">
    <section className="file-heading" aria-labelledby="lessons-title">
      <div><div className="file-heading-mark"><GraduationCap size={17} /></div><div><h1 id="lessons-title">{pageName("/lessons/")}</h1></div></div>
      <button className="secondary-button" onClick={() => void refresh()} disabled={loading}><RefreshCw className={loading ? "spin" : ""} size={15} />Refresh</button>
    </section>
    <PageIntro route="/lessons/" />

    {error && <div className="inline-error" role="alert"><CircleAlert size={17} /><div><strong>Lessons</strong><span>{error}</span></div><button onClick={() => setError(undefined)}>Close</button></div>}

    {page && page.fixes.length > 0 && <section className="package-detail" aria-label="What fixes problems most">
      <h3>What solved the most chats</h3>
      <ul>{page.fixes.map(item => <li key={item.fixedBy || "none"}>{item.fixedBy || "Solved without a fix on the PC"}: {item.solved}</li>)}</ul>
    </section>}

    <div className="tab-row" role="tablist">{tabs.map(item => <button key={item ?? "all"} role="tab" aria-selected={tab === item}
      className={tab === item ? "primary-button" : "secondary-button"} onClick={() => setTab(item)}>{item ? statusText[item] : "All"}</button>)}</div>

    {loading && lessons.length === 0 ? <div className="file-list"><div className="file-skeleton" /></div>
      : lessons.length === 0 ? <section className="file-empty"><span className="empty-icon"><GraduationCap size={23} /></span>
        <h2>No lessons here</h2><p>Lessons appear a few minutes after a chat is marked solved.</p></section>
      : <section className="file-list" aria-label="Lessons">{lessons.map(lesson =>
        <article key={lesson.lessonId} className="file-row">
          <div className="file-row-main">
            <div className="file-row-title"><strong dir="auto">{lesson.symptom || "(no message)"}</strong>
              <span className="ticket-status">{statusText[lesson.status]}</span></div>
            <p className="file-description">{outcomeLine(lesson)}</p>
            {lesson.tried.length > 0 && <ol className="package-steps">{lesson.tried.map((step, index) =>
              <li key={index}>{step.name} ({step.status}){step.summary && `: ${step.summary}`}</li>)}</ol>}
            <p className="file-compatibility">{lesson.machineName || "PC"} · solved {new Date(lesson.resolvedAtUtc).toLocaleString()}
              {lesson.skills.length > 0 && ` · skills ${lesson.skills.join(", ")}`} · <Link href={`/support/?conversation=${encodeURIComponent(lesson.conversationId)}`}>Conversation</Link>
              {lesson.decidedBy && ` · decided by ${lesson.decidedBy}`}</p>
            {lesson.note && <p className="file-compatibility" dir="auto">Note: {lesson.note}</p>}
            {reviewer && <input className="approval-note" dir="auto" value={notes[lesson.lessonId] ?? ""} aria-label="Note"
              placeholder="Optional note for the team" onChange={event => setNotes({ ...notes, [lesson.lessonId]: event.target.value })} />}
          </div>
          {reviewer && <div className="file-row-actions">
            <Link className="primary-button" href={skillDraftHref(lesson.conversationId)} onClick={() => void decide(lesson.lessonId, "Drafted")}>Start skill draft</Link>
            <button className="secondary-button" onClick={() => void decide(lesson.lessonId, "Kept")}>Keep</button>
            <button className="secondary-button" onClick={() => void decide(lesson.lessonId, "Dropped")}>Drop</button>
          </div>}
        </article>)}</section>}
  </div>;
}
