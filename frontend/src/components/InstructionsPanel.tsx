import { useState } from 'react';

const PRESETS: Array<[string, string]> = [
  ['Rates in header, not per line', 'Commission rates are stated once in the sheet header, not per line item.'],
  ['Ignore YTD blocks', 'Ignore any year-to-date or cumulative summary block; extract current-period lines only.'],
  ['Brackets are credits', 'Figures in parentheses or brackets are credits — map them as negative net sales.'],
  ['Split multi-sheet workbooks', 'Each worksheet is a separate region; extract all sheets and merge into one canonical set.'],
  ['Trust printed totals', 'If a printed totals row disagrees with the sum of lines, flag it and keep the line-level sum.'],
];

interface Props {
  instructions: string;
  onInstructionsChange: (v: string) => void;
}

export function InstructionsPanel({ instructions, onInstructionsChange }: Props) {
  const [open, setOpen] = useState(true);

  function addPreset(text: string) {
    setOpen(true);
    onInstructionsChange(instructions.trim() ? instructions.trim() + '\n' + text : text);
  }

  return (
    <div className="card" style={{ padding: 20, display: 'flex', flexDirection: 'column' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 11 }}>
        <span style={{ color: 'var(--orange)', display: 'flex' }}>
          <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
            <path d="M12 3l1.6 4.4L18 9l-4.4 1.6L12 15l-1.6-4.4L6 9l4.4-1.6L12 3Z" />
            <path d="M18 15l.8 2.2L21 18l-2.2.8L18 21l-.8-2.2L15 18l2.2-.8L18 15Z" />
          </svg>
        </span>
        <h3 style={{ fontSize: 22 }}>Processing instructions</h3>
        <button className="pill" style={{ marginLeft: 'auto', color: 'var(--blue)' }} onClick={() => setOpen((o) => !o)}>
          {open ? 'Hide' : instructions.trim() ? 'Edit rules' : 'Add rules'}
        </button>
      </div>
      <div style={{ color: 'var(--muted)', fontSize: 13, marginTop: 4 }}>
        Applied by the canonical mapper to every file you upload next. These rules take precedence
        over the default extraction rules wherever the two disagree.
      </div>

      {open && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 11, marginTop: 14, flex: 1 }}>
          <textarea
            rows={3}
            placeholder='e.g. Commission rates live in the sheet header, not per line. Ignore the "YTD" block. Treat bracketed figures as credits.'
            style={{
              width: '100%', flex: 1, minHeight: 86, padding: '12px 14px', font: 'inherit',
              fontSize: 14, color: 'var(--ink)', background: 'var(--bg)', border: '1px solid var(--line)',
              borderRadius: 10, resize: 'vertical',
            }}
            value={instructions}
            onChange={(e) => onInstructionsChange(e.target.value)}
          />
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 7, alignItems: 'center' }}>
            <span className="lbl">Suggested rules</span>
            {PRESETS.map(([label, text]) => (
              <button key={label} className="pill" style={{ fontSize: 12.5, padding: '5px 12px' }} onClick={() => addPreset(text)}>
                {label}
              </button>
            ))}
          </div>
          <div style={{ color: 'var(--muted)', fontSize: 12.5 }}>
            {instructions.trim()
              ? 'Applied to files uploaded from now on. Files already in the queue keep the instructions they were sent with — use the ↻ button on a row to re-run it with these rules instead.'
              : 'No custom instructions — the mapper runs on its default prompt.'}
          </div>
        </div>
      )}
    </div>
  );
}