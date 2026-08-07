import { useState } from 'react';
import type { ChangeEvent, DragEvent } from 'react';

const MANUFACTURERS = ['Walraven', 'Josam', 'DFC', 'Caleffi', 'Burnham Commercial'];
const MONTH_OPTIONS = [
  ['01', 'January'], ['02', 'February'], ['03', 'March'], ['04', 'April'],
  ['05', 'May'], ['06', 'June'], ['07', 'July'], ['08', 'August'],
  ['09', 'September'], ['10', 'October'], ['11', 'November'], ['12', 'December'],
];
const YEAR_OPTIONS = ['2026', '2025', '2024'];

interface Props {
  manufacturer: string;
  month: string;
  year: string;
  onManufacturerChange: (v: string) => void;
  onMonthChange: (v: string) => void;
  onYearChange: (v: string) => void;
  onFilesAdded: (files: FileList) => void;
}

export function NewBatchPanel({
  manufacturer, month, year,
  onManufacturerChange, onMonthChange, onYearChange, onFilesAdded,
}: Props) {
  const [dragging, setDragging] = useState(false);

  function handleDrop(e: DragEvent<HTMLLabelElement>) {
    e.preventDefault();
    setDragging(false);
    if (e.dataTransfer?.files?.length) onFilesAdded(e.dataTransfer.files);
  }

  function handlePick(e: ChangeEvent<HTMLInputElement>) {
    if (e.target.files?.length) onFilesAdded(e.target.files);
    e.target.value = '';
  }

  return (
    <div className="card" style={{ padding: 20 }}>
      <div style={{ display: 'flex', alignItems: 'baseline', gap: 12, marginBottom: 16 }}>
        <h3 style={{ fontSize: 22 }}>New batch</h3>
        <span style={{ color: 'var(--muted)', fontSize: 13 }}>
          Set the manufacturer and period, then drop this month's reports.
        </span>
      </div>

      <div style={{ display: 'grid', gridTemplateColumns: 'minmax(0,1fr) minmax(0,130px) minmax(0,100px)', gap: 14, marginBottom: 16 }}>
        <label style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          <span style={{ fontSize: 12.5, color: 'var(--muted)' }}>Manufacturer</span>
          <input
            className="fld"
            list="mfr-list"
            placeholder="Search manufacturers…"
            value={manufacturer}
            onChange={(e) => onManufacturerChange(e.target.value)}
          />
          <datalist id="mfr-list">
            {MANUFACTURERS.map((m) => <option key={m} value={m} />)}
          </datalist>
        </label>

        <label style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          <span style={{ fontSize: 12.5, color: 'var(--muted)' }}>Month</span>
          <select className="fld" value={month} onChange={(e) => onMonthChange(e.target.value)}>
            {MONTH_OPTIONS.map(([v, label]) => <option key={v} value={v}>{label}</option>)}
          </select>
        </label>

        <label style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          <span style={{ fontSize: 12.5, color: 'var(--muted)' }}>Year</span>
          <select className="fld" value={year} onChange={(e) => onYearChange(e.target.value)}>
            {YEAR_OPTIONS.map((y) => <option key={y} value={y}>{y}</option>)}
          </select>
        </label>
      </div>

      <label
        htmlFor="filepick"
        onDragOver={(e) => { e.preventDefault(); if (!dragging) setDragging(true); }}
        onDragLeave={() => setDragging(false)}
        onDrop={handleDrop}
        style={{
          display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 16,
          minHeight: 118, padding: '18px 22px', cursor: 'pointer',
          border: `1.5px dashed ${dragging ? 'var(--blue)' : '#cdd8e6'}`,
          borderRadius: 12, background: dragging ? 'var(--blue-soft)' : 'var(--bg)',
          transition: 'background .15s,border-color .15s',
        }}
      >
        <span style={{
          display: 'flex', alignItems: 'center', justifyContent: 'center',
          width: 44, height: 44, borderRadius: 999, background: 'var(--blue-soft)',
          color: 'var(--blue-deep)', flex: 'none',
        }}>
          <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
            <path d="M12 16V4" /><path d="m7 9 5-5 5 5" /><path d="M4 16v2a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-2" />
          </svg>
        </span>
        <span style={{ display: 'flex', flexDirection: 'column', gap: 3 }}>
          <span style={{ fontFamily: "'Source Serif 4',Georgia,serif", fontSize: 19, color: 'var(--ink)' }}>
            Drop .xlsx, .xls or .pdf files here
          </span>
          <span style={{ color: 'var(--muted)', fontSize: 13 }}>
            Drop as many as you like — each is processed on its own, in the background. Keep adding while it works.
          </span>
        </span>
      </label>
      <input id="filepick" type="file" multiple accept=".xlsx,.xls,.pdf" style={{ display: 'none' }} onChange={handlePick} />
    </div>
  );
}