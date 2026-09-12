import { useState } from 'react';
import type { MouseEvent } from 'react';
import type { DocumentDetail } from '../types';
import { money, monthLabel } from '../utils/format';

type Tab = 'source' | 'extract' | 'canon' | 'instructions';
type CanonView = 'table' | 'json';

interface Props {
  doc: DocumentDetail;
  initialTab: Tab;
  onClose: () => void;
}

function TabButton({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button
      className="pill"
      style={{ background: active ? 'var(--blue-deep)' : 'var(--surface)', color: active ? '#fff' : 'var(--text)' }}
      onClick={onClick}
    >
      {children}
    </button>
  );
}

function downloadJson(fileName: string, data: unknown) {
  const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' });
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = fileName;
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 4000);
}

/** Quotes a CSV field only when it needs it (contains a comma, quote or newline). */
function csvField(value: unknown): string {
  const s = value === null || value === undefined ? '' : String(value);
  return /[",\n]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s;
}

function downloadCsv(fileName: string, headers: string[], rows: (string | number)[][]) {
  const lines = [headers, ...rows].map((row) => row.map(csvField).join(','));
  // Leading BOM so Excel opens the accented/UTF-8 content correctly instead of guessing a codepage.
  const blob = new Blob(['﻿' + lines.join('\r\n')], { type: 'text/csv;charset=utf-8;' });
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = fileName;
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 4000);
}

interface SourceSheet {
  name: string;
  headers: string[];
  rows: string[][];
}

/**
 * Parses the raw extraction JSON's per-sheet markdown tables (produced directly by
 * SpreadsheetExtractionService — see its `contents` array, each entry `{path, markdown}`) back
 * into plain rows, so the "Input file" tab can render the actual workbook content as a real table
 * instead of just linking out to the binary file.
 */
function parseSourceSheets(rawExtractionJson: string): SourceSheet[] {
  try {
    const parsed = JSON.parse(rawExtractionJson);
    const contents: Array<{ path?: string; markdown?: string }> = parsed?.result?.contents ?? [];
    return contents
      .filter((c) => typeof c.markdown === 'string')
      .map((c) => {
        const lines = c.markdown!.split('\n');
        let name = c.path ?? 'Sheet';
        let headers: string[] = [];
        let headerSeen = false;
        const rows: string[][] = [];
        for (const raw of lines) {
          const line = raw.trim();
          if (line.startsWith('# ')) { name = line.slice(2).trim(); continue; }
          if (!line.startsWith('|')) continue;
          const cells = line.slice(1, line.endsWith('|') ? -1 : undefined).split('|').map((v) => v.trim());
          if (cells.every((v) => /^-*$/.test(v))) continue; // "| --- | --- |" separator row
          if (!headerSeen) { headers = cells; headerSeen = true; continue; }
          rows.push(cells);
        }
        return { name, headers, rows };
      })
      .filter((s) => s.headers.length > 0);
  } catch {
    return [];
  }
}

const preStyle: React.CSSProperties = {
  margin: 0, background: '#0f2537', color: '#cfe0ef', padding: 18, borderRadius: 12,
  fontSize: 12.5, lineHeight: 1.6, overflow: 'auto', maxHeight: '100%',
  // pre-wrap + break-word: a raw JSON blob with no embedded newlines used to render as one
  // unbroken horizontal line the width of the whole payload — this wraps it like a normal
  // pretty-printed document instead, regardless of whether the source string has real linebreaks.
  whiteSpace: 'pre-wrap', wordBreak: 'break-word',
  fontFamily: 'ui-monospace,SFMono-Regular,Menlo,monospace',
};

/** Best-effort pretty-print: re-indents valid JSON, falls back to the raw string otherwise. */
function prettyJson(raw: string): string {
  try {
    return JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    return raw;
  }
}

export function DocumentViewerModal({ doc, initialTab, onClose }: Props) {
  const [tab, setTab] = useState<Tab>(initialTab);
  const [canonView, setCanonView] = useState<CanonView>('table');
  const [copied, setCopied] = useState(false);

  async function copyInstructions() {
    try {
      await navigator.clipboard.writeText(doc.customInstructions ?? '');
      setCopied(true);
      setTimeout(() => setCopied(false), 1800);
    } catch {
      // Clipboard access can be blocked by the browser; the text is still selectable by hand.
    }
  }

  const isPdf = /\.pdf$/i.test(doc.fileName);
  const baseName = doc.fileName.replace(/\.[^.]+$/, '');
  const totalNet = doc.canonicalRecords.reduce((a, r) => a + r.netSales, 0);
  const totalComm = doc.canonicalRecords.reduce((a, r) => a + r.commission, 0);
  const sourceSheets = isPdf ? [] : parseSourceSheets(doc.rawExtractionJson);

  function downloadCanonicalCsv() {
    downloadCsv(
      `${baseName}.canonical.csv`,
      ['Customer ID', 'Customer', 'Date', 'City', 'State', 'Product family', 'Part no', 'Qty', 'Net sales', 'Commission'],
      doc.canonicalRecords.map((r) => [
        r.customerId, r.customerName, r.date, r.city, r.state, r.productFamily, r.partNo,
        r.quantity, r.netSales, r.commission,
      ])
    );
  }

  function stop(e: MouseEvent) {
    e.stopPropagation();
  }

  return (
    <div
      style={{ position: 'fixed', inset: 0, zIndex: 20, display: 'flex', padding: 32, background: 'rgba(18,54,90,.32)' }}
      onClick={onClose}
    >
      <div
        style={{
          // A FIXED height (not just a max-height cap) so the frame stays the same size across
          // every tab — it previously shrank to fit whichever tab's content was shortest (e.g.
          // "Instructions") and grew again on "Canonical", visibly resizing the window on every
          // click. min(...) still keeps it from overflowing a short viewport.
          width: 'min(1320px,100%)', height: 'min(880px,92vh)', margin: 'auto', display: 'flex', flexDirection: 'column',
          background: 'var(--surface)', border: '1px solid var(--line)', borderRadius: 16,
          boxShadow: '0 24px 60px rgba(18,54,90,.22)', overflow: 'hidden',
        }}
        onClick={stop}
      >
        <div style={{ display: 'flex', alignItems: 'center', gap: 16, padding: '18px 22px', borderBottom: '1px solid var(--line)' }}>
          <span style={{ display: 'flex', flexDirection: 'column', minWidth: 0 }}>
            <span style={{
              fontFamily: "'Source Serif 4',Georgia,serif", fontSize: 20, color: 'var(--ink)',
              overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}>
              {doc.fileName}
            </span>
            <span style={{ color: 'var(--muted)', fontSize: 12.5 }}>
              {doc.manufacturer} · {monthLabel(doc.periodMonth)} {doc.periodYear} · {doc.status}
            </span>
          </span>
          <span style={{ display: 'flex', marginLeft: 'auto', gap: 7, alignItems: 'center', flex: 'none' }}>
            {doc.customInstructions && (
              <TabButton active={tab === 'instructions'} onClick={() => setTab('instructions')}>Instructions</TabButton>
            )}
            <TabButton active={tab === 'source'} onClick={() => setTab('source')}>Input file</TabButton>
            <TabButton active={tab === 'extract'} onClick={() => setTab('extract')}>Extracted data</TabButton>
            <TabButton active={tab === 'canon'} onClick={() => setTab('canon')}>Canonical data</TabButton>
            <button
              style={{
                width: 32, height: 32, marginLeft: 6, display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
                color: 'var(--muted)', background: 'transparent', border: '1px solid var(--line)', borderRadius: 999, cursor: 'pointer',
              }}
              onClick={onClose}
            >
              <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5">
                <path d="M18 6 6 18M6 6l12 12" />
              </svg>
            </button>
          </span>
        </div>

        <div style={{ flex: 1, minHeight: 0, overflow: 'auto', padding: 22, background: 'var(--bg)' }}>
          {tab === 'instructions' && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
                <div className="lbl">Processing instructions — exactly as sent with this file</div>
                <button
                  className="pill"
                  style={{ marginLeft: 'auto', color: 'var(--blue)' }}
                  onClick={copyInstructions}
                >
                  {copied ? 'Copied!' : 'Copy'}
                </button>
              </div>
              <pre style={{ ...preStyle, whiteSpace: 'pre-wrap', userSelect: 'text' }}>
                {doc.customInstructions}
              </pre>
            </div>
          )}

          {tab === 'source' && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
                <div className="lbl">{isPdf ? 'Original PDF' : 'Original workbook'} — as uploaded</div>
                <a
                  className="pill"
                  style={{ marginLeft: 'auto', color: 'var(--blue)', textDecoration: 'none' }}
                  href={doc.sourceUrl}
                  target="_blank"
                  rel="noreferrer"
                >
                  Open original file
                </a>
              </div>

              {isPdf ? (
                <div className="card" style={{ padding: 20 }}>
                  <div style={{ fontFamily: "'Source Serif 4',Georgia,serif", fontSize: 20, color: 'var(--ink)' }}>
                    {doc.manufacturer} — {doc.fileName}
                  </div>
                  <div style={{ color: 'var(--muted)', fontSize: 12.5, marginBottom: 12 }}>
                    Period ending {monthLabel(doc.periodMonth)} {doc.periodYear}
                  </div>
                  <iframe
                    title="Original document"
                    src={doc.sourceUrl}
                    style={{ width: '100%', height: '60vh', border: '1px solid var(--line)', borderRadius: 8 }}
                  />
                </div>
              ) : sourceSheets.length > 0 ? (
                // The workbook itself can't be embedded, but its content already went through
                // SpreadsheetExtractionService as plain rows before any normalization or LLM
                // mapping touched it — rendering those rows here shows the file's real content,
                // sheet by sheet, the same way the Canonical tab's table view does.
                sourceSheets.map((sheet, si) => (
                  <div key={si} className="card" style={{ padding: '6px 20px 16px' }}>
                    <div style={{ padding: '14px 0 4px', fontFamily: "'Source Serif 4',Georgia,serif", fontSize: 17, color: 'var(--ink)' }}>
                      {sheet.name}
                    </div>
                    <div style={{ color: 'var(--muted)', fontSize: 12, marginBottom: 8 }}>
                      {sheet.rows.length} row{sheet.rows.length === 1 ? '' : 's'}
                    </div>
                    <div style={{ overflowX: 'auto' }}>
                      <table>
                        <thead>
                          <tr>
                            {sheet.headers.map((h, i) => <th key={i}>{h || ` `}</th>)}
                          </tr>
                        </thead>
                        <tbody>
                          {sheet.rows.map((row, ri) => (
                            <tr key={ri}>
                              {sheet.headers.map((_, ci) => (
                                <td key={ci} style={{ fontSize: 13, padding: '9px 10px' }}>{row[ci] ?? ''}</td>
                              ))}
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  </div>
                ))
              ) : (
                <div className="card" style={{ padding: '28px 20px', textAlign: 'center', color: 'var(--muted)', fontSize: 13.5 }}>
                  This file's content couldn't be parsed into a table view — open the original to inspect it.
                </div>
              )}
            </div>
          )}

          {tab === 'extract' && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
                <div className="lbl">Phase 1 — Azure Content Understanding result</div>
                <button
                  className="pill"
                  style={{ marginLeft: 'auto', color: 'var(--blue)' }}
                  onClick={() => downloadJson(`${baseName}.extraction.json`, JSON.parse(doc.rawExtractionJson))}
                >
                  Download JSON
                </button>
              </div>
              <pre style={preStyle}>{prettyJson(doc.rawExtractionJson)}</pre>
            </div>
          )}

          {tab === 'canon' && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
                <div className="lbl">Phase 2 — canonical analytics records (WH_Midwest_Sales.dbo.Sales)</div>
                <span style={{ display: 'flex', marginLeft: 'auto', gap: 7 }}>
                  <button
                    className="pill"
                    style={{ fontSize: 12.5, padding: '5px 12px', background: canonView === 'table' ? 'var(--blue-soft)' : 'var(--surface)', color: canonView === 'table' ? 'var(--blue-deep)' : 'var(--text)' }}
                    onClick={() => setCanonView('table')}
                  >
                    Table
                  </button>
                  <button
                    className="pill"
                    style={{ fontSize: 12.5, padding: '5px 12px', background: canonView === 'json' ? 'var(--blue-soft)' : 'var(--surface)', color: canonView === 'json' ? 'var(--blue-deep)' : 'var(--text)' }}
                    onClick={() => setCanonView('json')}
                  >
                    JSON
                  </button>
                  <button
                    className="pill"
                    style={{ fontSize: 12.5, padding: '5px 12px', color: 'var(--blue)' }}
                    onClick={downloadCanonicalCsv}
                  >
                    Download CSV
                  </button>
                </span>
              </div>

              {canonView === 'table' ? (
                <div className="card" style={{ padding: '6px 20px 16px' }}>
                  <table>
                    <thead>
                      <tr>
                        <th>Customer ID</th>
                        <th>Customer</th>
                        <th>Date</th>
                        <th>City</th>
                        <th>State</th>
                        <th>Product family</th>
                        <th>Part no</th>
                        <th style={{ textAlign: 'right' }}>Qty</th>
                        <th style={{ textAlign: 'right' }}>Net sales</th>
                        <th style={{ textAlign: 'right' }}>Commission</th>
                      </tr>
                    </thead>
                    <tbody>
                      {doc.canonicalRecords.map((r, i) => (
                        <tr key={i}>
                          <td style={{ fontSize: 13, padding: '9px 10px', fontVariantNumeric: 'tabular-nums' }}>{r.customerId}</td>
                          <td style={{ fontSize: 13, padding: '9px 10px', color: 'var(--ink)' }}>{r.customerName}</td>
                          <td style={{ fontSize: 13, padding: '9px 10px', fontVariantNumeric: 'tabular-nums' }}>{r.date}</td>
                          <td style={{ fontSize: 13, padding: '9px 10px' }}>{r.city}</td>
                          <td style={{ fontSize: 13, padding: '9px 10px' }}>{r.state}</td>
                          <td style={{ fontSize: 13, padding: '9px 10px' }}>{r.productFamily}</td>
                          <td style={{ fontSize: 13, padding: '9px 10px', fontVariantNumeric: 'tabular-nums' }}>{r.partNo}</td>
                          <td style={{ fontSize: 13, padding: '9px 10px', textAlign: 'right', fontVariantNumeric: 'tabular-nums' }}>{r.quantity}</td>
                          <td style={{ fontSize: 13, padding: '9px 10px', textAlign: 'right', fontVariantNumeric: 'tabular-nums' }}>{money(r.netSales)}</td>
                          <td style={{ fontSize: 13, padding: '9px 10px', textAlign: 'right', fontVariantNumeric: 'tabular-nums' }}>{money(r.commission)}</td>
                        </tr>
                      ))}
                    </tbody>
                    <tfoot>
                      <tr>
                        <td colSpan={7} style={{ borderBottom: 'none', color: 'var(--ink)', fontWeight: 500, fontSize: 13 }}>
                          Totals — {doc.canonicalRecords.length} lines
                        </td>
                        <td style={{ borderBottom: 'none' }} />
                        <td style={{ borderBottom: 'none', textAlign: 'right', color: 'var(--ink)', fontWeight: 700, fontVariantNumeric: 'tabular-nums' }}>
                          {money(totalNet)}
                        </td>
                        <td style={{ borderBottom: 'none', textAlign: 'right', color: 'var(--ink)', fontWeight: 700, fontVariantNumeric: 'tabular-nums' }}>
                          {money(totalComm)}
                        </td>
                      </tr>
                    </tfoot>
                  </table>
                  <div style={{ color: 'var(--muted)', fontSize: 12, marginTop: 10 }}>
                    These two totals are the values shown in the queue — recalculate them from the input file to confirm the mapping.
                  </div>
                </div>
              ) : (
                <pre style={preStyle}>{JSON.stringify(doc.canonicalRecords, null, 2)}</pre>
              )}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
