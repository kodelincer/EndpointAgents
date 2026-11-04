"use client";

import { useState } from "react";
import { compact, dayTotal, niceTicks, shortDay, type OverviewDay } from "@/lib/overview";

export type Series = { key: string; label: string; color: string };

/** Stacked columns per day, one baseline, hover tooltip per day and a numbers table for readers who need it. */
export function DailyColumns({ days, series, label, unit }: { days: OverviewDay[]; series: Series[]; label: string; unit: string }) {
  const [hover, setHover] = useState<number>();
  const keys = series.map(item => item.key);
  const max = Math.max(0, ...days.map(day => dayTotal(day, keys)));
  const ticks = niceTicks(max);
  const top = ticks[ticks.length - 1];
  const every = Math.max(1, Math.ceil(days.length / 7));
  const shown = hover != null ? days[hover] : undefined;

  return <figure className="ov-figure" aria-label={label}>
    <Legend series={series} totals={series.map(item => days.reduce((sum, day) => sum + (day.values[item.key] ?? 0), 0))} />
    <div className="ov-columns-frame">
      <div className="ov-yaxis" aria-hidden="true">{[...ticks].reverse().map(tick => <span key={tick}>{compact(tick)}</span>)}</div>
      <div className="ov-plot" onMouseLeave={() => setHover(undefined)}>
        {ticks.map(tick => <span key={tick} className="ov-gridline" style={{ bottom: `${(tick / top) * 100}%` }} aria-hidden="true" />)}
        <div className="ov-columns">
          {days.map((day, index) => {
            const total = dayTotal(day, keys);
            return <button type="button" key={day.date} className={`ov-column${hover === index ? " is-hover" : ""}`}
              onMouseEnter={() => setHover(index)} onFocus={() => setHover(index)} onBlur={() => setHover(undefined)}
              aria-label={`${shortDay(day.date)}: ${total} ${unit}. ${series.map(item => `${item.label} ${day.values[item.key] ?? 0}`).join(", ")}`}>
              <span className="ov-stack" style={{ height: `${top ? (total / top) * 100 : 0}%` }}>
                {series.map(item => (day.values[item.key] ?? 0) > 0 && <span key={item.key} className="ov-segment"
                  style={{ flexGrow: day.values[item.key], background: item.color }} />)}
              </span>
            </button>;
          })}
        </div>
        {shown && hover != null && <div className="ov-tooltip" role="status"
          style={{ left: `${((hover + 0.5) / days.length) * 100}%`, transform: `translateX(${hover > days.length * 0.6 ? "-100%" : hover < days.length * 0.25 ? "0" : "-50%"})` }}>
          <strong>{shortDay(shown.date)}</strong>
          {series.map(item => <span key={item.key}><i style={{ background: item.color }} />{item.label}<b>{shown.values[item.key] ?? 0}</b></span>)}
          <span className="ov-tooltip-total">Total<b>{dayTotal(shown, keys)}</b></span>
        </div>}
      </div>
    </div>
    <div className="ov-xaxis" aria-hidden="true">
      {days.map((day, index) => <span key={day.date}>{index % every === 0 || index === days.length - 1 ? shortDay(day.date) : ""}</span>)}
    </div>
    <details className="ov-numbers"><summary>Show the numbers</summary>
      <div className="ov-table-wrap"><table className="ov-table"><thead><tr><th>Day</th>{series.map(item => <th key={item.key}>{item.label}</th>)}<th>Total</th></tr></thead>
        <tbody>{[...days].reverse().map(day => <tr key={day.date}><td>{shortDay(day.date)}</td>
          {series.map(item => <td key={item.key}>{day.values[item.key] ?? 0}</td>)}<td>{dayTotal(day, keys)}</td></tr>)}</tbody></table></div>
    </details>
  </figure>;
}

export function Legend({ series, totals }: { series: Series[]; totals?: number[] }) {
  return <ul className="ov-legend">{series.map((item, index) => <li key={item.key}>
    <i style={{ background: item.color }} aria-hidden="true" />{item.label}{totals && <b>{compact(totals[index])}</b>}</li>)}</ul>;
}

/** One whole split into parts (up to six), with a legend that carries the numbers. */
export function SegmentedBar({ parts, label }: { parts: (Series & { count: number })[]; label: string }) {
  const total = parts.reduce((sum, part) => sum + part.count, 0);
  const [hover, setHover] = useState<string>();
  return <figure className="ov-figure" aria-label={label}>
    <div className="ov-segbar" onMouseLeave={() => setHover(undefined)}>
      {total === 0 ? <span className="ov-segbar-empty" /> : parts.filter(part => part.count > 0).map(part =>
        <span key={part.key} className={`ov-segbar-part${hover && hover !== part.key ? " is-dim" : ""}`} style={{ flexGrow: part.count, background: part.color }}
          onMouseEnter={() => setHover(part.key)} title={`${part.label}: ${part.count} (${Math.round(part.count / total * 100)}%)`} />)}
    </div>
    <ul className="ov-legend ov-legend-rows">{parts.map(part => <li key={part.key} className={hover === part.key ? "is-hover" : undefined}>
      <i style={{ background: part.color }} aria-hidden="true" />{part.label}
      <b>{part.count}</b><small>{total ? `${Math.round(part.count / total * 100)}%` : "—"}</small></li>)}</ul>
  </figure>;
}

/** Ranked horizontal bars for one measure; the value sits at the bar tip. */
export function RankedBars({ items, label, color = "var(--ov-series-1)" }:
  { items: { key: string; label: string; value: number; note?: string }[]; label: string; color?: string }) {
  const max = Math.max(1, ...items.map(item => item.value));
  return <ul className="ov-ranked" aria-label={label}>{items.map(item => <li key={item.key} title={`${item.label}: ${item.value}`}>
    <span className="ov-ranked-label"><code>{item.label}</code>{item.note && <small>{item.note}</small>}</span>
    <span className="ov-ranked-track"><span className="ov-ranked-bar" style={{ width: `${(item.value / max) * 100}%`, background: color }} />
      <b>{item.value}</b></span>
  </li>)}</ul>;
}

/** Tiny trend line for a stat tile; the latest day is marked. */
export function Sparkline({ values, label }: { values: number[]; label: string }) {
  if (values.length < 2) return null;
  const max = Math.max(1, ...values);
  const width = 100, height = 28;
  const points = values.map((value, index) => [4 + index / (values.length - 1) * (width - 8), height - 3 - (value / max) * (height - 6)]);
  const path = points.map(([x, y], index) => `${index ? "L" : "M"}${x.toFixed(1)},${y.toFixed(1)}`).join(" ");
  const [lastX, lastY] = points[points.length - 1];
  return <svg className="ov-spark" viewBox={`0 0 ${width} ${height}`} preserveAspectRatio="none" role="img" aria-label={label}>
    <path d={`${path} L${width},${height} L0,${height} Z`} className="ov-spark-area" />
    <path d={path} className="ov-spark-line" vectorEffect="non-scaling-stroke" />
    <path d={`M${lastX},${lastY} l0,0.01`} className="ov-spark-ring" vectorEffect="non-scaling-stroke" />
    <path d={`M${lastX},${lastY} l0,0.01`} className="ov-spark-dot" vectorEffect="non-scaling-stroke" />
  </svg>;
}

/** Share bar for table rows: worked part filled, failed part red, the rest is track. */
export function SuccessMeter({ worked, failed, total }: { worked: number; failed: number; total: number }) {
  if (!total) return <span className="muted">—</span>;
  return <span className="ov-meter" title={`${worked} worked, ${failed} failed of ${total}`}>
    <span className="ov-meter-track">
      <span style={{ flexGrow: worked, background: "var(--ov-good)" }} />
      {failed > 0 && <span style={{ flexGrow: failed, background: "var(--ov-critical)" }} />}
      {total - worked - failed > 0 && <span style={{ flexGrow: total - worked - failed, background: "var(--ov-warning)" }} />}
    </span>
    <b>{Math.round(worked / total * 100)}%</b>
  </span>;
}
