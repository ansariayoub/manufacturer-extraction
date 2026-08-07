import { useState } from 'react';
import type { MouseEvent } from 'react';
import type { DocumentDetail } from '../types';
import { money, monthLabel } from '../utils/format';

type Tab = 'source' | 'extract' | 'canon';
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

const preStyle: React.CSSProperties = {
  margin: 0, background: '#0f2537', color: '#cfe0ef', padding: 18, borderRadius: 12,
  fontSize: 12.5, lineHeight: 1.6, overflow: 'auto', maxHeight: '56vh',
  fontFamily: 'ui-monospace,SFMono-Regular,Menlo,monospace',
};

export function DocumentViewerModal({ doc, initialTab, onClose }: Props) {
  const [tab, setTab] = useState<Tab>(initialTab);
  const [canonView, setCanonView] = useState<CanonView>('table');

  const isPdf = /\.pdf$/i.test(doc.fileName);
  const baseName = doc.fileName.replace(/\.[^.]+$/, '');
  const totalNet = doc.canonicalRecords.reduce((a, r) => a + r.netSales, 0);
  const totalComm = doc.canonicalRecords.reduce((a, r) => a + r.commission, 0);

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
          width: 'min(1320px,100%)', margin: 'auto', maxHeight: '100%', display: 'flex', flexDirection: 'column',
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

        <div style={{ overflow: 'auto', padding: 22, background: 'var(--bg)' }}>
          {tab === 'source' && (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <div className="lbl">{isPdf ? 'Original PDF' : 'Original workbook'} — as uploaded</div>
              <div className="card" style={{ padding: 20 }}>
                <div style={{ fontFamily: "'Source Serif 4',Georgia,serif", fontSize: 20, color: 'var(--ink)' }}>
                  {doc.manufacturer} — {doc.fileName}
                </div>
                <div style={{ color: 'var(--muted)', fontSize: 12.5, marginBottom: 12 }}>
                  Period ending {monthLabel(doc.periodMonth)} {doc.periodYear}
                </div>
                {isPdf ? (
                  <iframe
                    title="Original document"
                    src={doc.sourceUrl}
                    style={{ width: '100%', height: '60vh', border: '1px solid var(--line)', borderRadius: 8 }}
                  />
                ) : (
                  <div style={{ padding: '28px 0', textAlign: 'center', color: 'var(--muted)', fontSize: 13.5 }}>
                    Excel workbooks can't be previewed inline — open the original to inspect it.
                  </div>
                )}
                <a
                  className="pill pill-solid"
                  style={{ display: 'inline-block', marginTop: 12, textDecoration: 'none' }}
                  href={doc.sourceUrl}
                  target="_blank"
                  rel="noreferrer"
                >
                  Open original file
                </a>
              </div>
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
              <pre style={preStyle}>{doc.rawExtractionJson}</pre>
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
                    onClick={() => downloadJson(`${baseName}.canonical.json`, doc.canonicalRecords)}
                  >
                    Download
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
