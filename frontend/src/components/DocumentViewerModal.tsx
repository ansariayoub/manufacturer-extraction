import { useEffect, useRef, useState } from 'react';
import type { CSSProperties, ReactNode } from 'react';
import type { CanonicalPage, DocumentDetail, DocumentSummary, ExtractedPage, SourcePage } from '../types';
import {
  downloadCanonicalCsv, downloadExtractedJson, getCanonicalPage, getExtractedPage, getSourcePage,
} from '../api/documentsApi';
import { money, monthLabel } from '../utils/format';

export type ViewerTab = 'source' | 'extract' | 'canon' | 'instructions';
type CanonView = 'table' | 'json';

interface Props {
  /** The queue row — already on screen, so the viewer can open instantly with its header filled in. */
  summary: DocumentSummary;
  /** Header-level detail (the original file's link); null until it has arrived. */
  detail: DocumentDetail | null;
  initialTab: ViewerTab;
  onClose: () => void;
}

const CANONICAL_PAGE_SIZE = 500;
const SOURCE_PAGE_SIZE = 300;

// --- small building blocks -----------------------------------------------------------------

function TabButton({ active, onClick, children }: { active: boolean; onClick: () => void; children: ReactNode }) {
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

/** "Loading…" with a spinner, so a slow request never looks like a frozen page. */
function Loading({ label = 'Loading…' }: { label?: string }) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 12, padding: '60px 0', color: 'var(--muted)', fontSize: 14.5 }}>
      <span className="spinner" aria-hidden="true" />
      {label}
    </div>
  );
}

function ErrorBox({ message }: { message: string }) {
  return (
    <div className="card" style={{ padding: '20px 22px', color: '#b45309', background: '#fffbeb', border: '1px solid #fde68a', fontSize: 14 }}>
      Couldn't load this content: {message}
    </div>
  );
}

/** Previous / Next controls shared by every paged tab. */
function Pager({
  page, pageCount, onChange, loading, detail,
}: { page: number; pageCount: number; onChange: (page: number) => void; loading: boolean; detail?: string }) {
  if (pageCount <= 1) return detail ? <span style={{ color: 'var(--muted)', fontSize: 13 }}>{detail}</span> : null;
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
      <button className="pill" style={{ fontSize: 12.5, padding: '5px 14px' }} disabled={page <= 1 || loading} onClick={() => onChange(page - 1)}>
        ← Previous
      </button>
      <span style={{ color: 'var(--text)', fontSize: 13, fontVariantNumeric: 'tabular-nums' }}>
        Page {page} of {pageCount}
      </span>
      <button className="pill" style={{ fontSize: 12.5, padding: '5px 14px' }} disabled={page >= pageCount || loading} onClick={() => onChange(page + 1)}>
        Next →
      </button>
      {loading && <span className="spinner" aria-label="Loading" />}
      {detail && <span style={{ color: 'var(--muted)', fontSize: 13 }}>{detail}</span>}
    </div>
  );
}

/**
 * Runs a loader whenever its inputs change. The previous result stays on screen while the next one
 * loads (so paging does not flash blank), and `loading` drives the spinner. The request of a load
 * that has been superseded (another page, another tab, the viewer closed) is aborted, which also
 * stops the server from finishing a heavy database read nobody is waiting for any more.
 */
function useLoad<T>(load: (signal: AbortSignal) => Promise<T>, deps: readonly unknown[]) {
  const [state, setState] = useState<{ data: T | null; loading: boolean; error: string | null }>({
    data: null, loading: true, error: null,
  });
  const loadRef = useRef(load);
  loadRef.current = load;

  useEffect(() => {
    const controller = new AbortController();
    setState((s) => ({ ...s, loading: true, error: null }));
    loadRef.current(controller.signal)
      .then((data) => { if (!controller.signal.aborted) setState({ data, loading: false, error: null }); })
      .catch((err) => { if (!controller.signal.aborted) setState((s) => ({ ...s, loading: false, error: err instanceof Error ? err.message : String(err) })); });
    return () => controller.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);

  return state;
}

const preStyle: CSSProperties = {
  margin: 0, background: '#0f2537', color: '#cfe0ef', padding: 18, borderRadius: 12,
  fontSize: 12.5, lineHeight: 1.6, overflow: 'auto',
  // pre-wrap + break-word: the raw extraction can contain one enormous unbroken line per sheet;
  // this wraps it like a normal document instead of one endless horizontal scroll.
  whiteSpace: 'pre-wrap', wordBreak: 'break-word',
  fontFamily: 'ui-monospace,SFMono-Regular,Menlo,monospace',
};

const cell: CSSProperties = { fontSize: 13, padding: '9px 10px' };
const num: CSSProperties = { ...cell, textAlign: 'right', fontVariantNumeric: 'tabular-nums' };

// --- tabs ---------------------------------------------------------------------------------------

function InstructionsTab({ text }: { text: string }) {
  const [copied, setCopied] = useState(false);

  async function copy() {
    try {
      await navigator.clipboard.writeText(text);
      setCopied(true);
      setTimeout(() => setCopied(false), 1800);
    } catch {
      // Clipboard access can be blocked by the browser; the text is still selectable by hand.
    }
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
        <div className="lbl">Processing instructions — exactly as sent with this file</div>
        <button className="pill" style={{ marginLeft: 'auto', color: 'var(--blue)' }} onClick={copy}>
          {copied ? 'Copied!' : 'Copy'}
        </button>
      </div>
      <pre style={{ ...preStyle, userSelect: 'text' }}>{text}</pre>
    </div>
  );
}

function SourceTab({ summary, detail }: { summary: DocumentSummary; detail: DocumentDetail | null }) {
  const isPdf = /\.pdf$/i.test(summary.fileName);
  const [sheet, setSheet] = useState(0);
  const [page, setPage] = useState(1);

  const { data, loading, error } = useLoad<SourcePage | null>(
    (signal) => (isPdf ? Promise.resolve(null) : getSourcePage(summary.id, sheet, page, SOURCE_PAGE_SIZE, signal)),
    [summary.id, sheet, page, isPdf]
  );

  const openLink = detail?.sourceUrl ? (
    <a className="pill" style={{ marginLeft: 'auto', color: 'var(--blue)', textDecoration: 'none' }} href={detail.sourceUrl} target="_blank" rel="noreferrer">
      Open original file
    </a>
  ) : null;

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
        <div className="lbl">{isPdf ? 'Original PDF' : 'Original workbook'} — as uploaded</div>
        {openLink}
      </div>

      {isPdf ? (
        <div className="card" style={{ padding: 20 }}>
          <div style={{ fontFamily: "'Source Serif 4',Georgia,serif", fontSize: 20, color: 'var(--ink)' }}>
            {summary.manufacturer} — {summary.fileName}
          </div>
          <div style={{ color: 'var(--muted)', fontSize: 12.5, marginBottom: 12 }}>
            Period ending {monthLabel(summary.periodMonth)} {summary.periodYear}
          </div>
          {detail?.sourceUrl
            ? <iframe title="Original document" src={detail.sourceUrl} style={{ width: '100%', height: '70vh', border: '1px solid var(--line)', borderRadius: 8 }} />
            : <Loading label="Loading the document…" />}
        </div>
      ) : error ? (
        <ErrorBox message={error} />
      ) : !data ? (
        <Loading label="Loading the workbook…" />
      ) : data.sheets.length === 0 ? (
        <div className="card" style={{ padding: '28px 20px', textAlign: 'center', color: 'var(--muted)', fontSize: 13.5 }}>
          This file's content isn't available as a table yet — it appears once the extraction step has run.
          You can still open the original file above.
        </div>
      ) : (
        <>
          {data.sheets.length > 1 && (
            <div style={{ display: 'flex', gap: 7, flexWrap: 'wrap' }}>
              {data.sheets.map((s, i) => (
                <TabButton key={i} active={i === data.sheetIndex} onClick={() => { setSheet(i); setPage(1); }}>
                  {s.name} <span style={{ opacity: 0.7 }}>({s.rowCount.toLocaleString('en-US')})</span>
                </TabButton>
              ))}
            </div>
          )}
          <div className="card" style={{ padding: '6px 20px 16px' }}>
            <div style={{ padding: '14px 0 6px', display: 'flex', alignItems: 'center', gap: 16, flexWrap: 'wrap' }}>
              <span style={{ fontFamily: "'Source Serif 4',Georgia,serif", fontSize: 17, color: 'var(--ink)' }}>
                {data.sheets[data.sheetIndex]?.name}
              </span>
              <Pager
                page={data.page}
                pageCount={Math.max(1, Math.ceil(data.totalRows / data.pageSize))}
                onChange={setPage}
                loading={loading}
                detail={`${data.totalRows.toLocaleString('en-US')} row${data.totalRows === 1 ? '' : 's'}`}
              />
            </div>
            <div style={{ overflowX: 'auto' }}>
              <table>
                <thead>
                  <tr>{data.headers.map((h, i) => <th key={i}>{h || ' '}</th>)}</tr>
                </thead>
                <tbody>
                  {data.rows.map((row, ri) => (
                    <tr key={ri}>
                      {data.headers.map((_, ci) => <td key={ci} style={cell}>{row[ci] ?? ''}</td>)}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        </>
      )}
    </div>
  );
}

function ExtractedTab({ summary }: { summary: DocumentSummary }) {
  const [page, setPage] = useState(1);
  const [downloading, setDownloading] = useState(false);
  const baseName = summary.fileName.replace(/\.[^.]+$/, '');

  const { data, loading, error } = useLoad<ExtractedPage>((signal) => getExtractedPage(summary.id, page, signal), [summary.id, page]);

  async function download() {
    setDownloading(true);
    try { await downloadExtractedJson(summary.id, baseName); } finally { setDownloading(false); }
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
        <div className="lbl">Phase 1 — extraction result</div>
        {data && (
          <Pager
            page={data.page} pageCount={data.pageCount} onChange={setPage} loading={loading}
            detail={`${data.totalChars.toLocaleString('en-US')} characters`}
          />
        )}
        <button className="pill" style={{ marginLeft: 'auto', color: 'var(--blue)' }} disabled={downloading} onClick={download}>
          {downloading ? 'Preparing…' : 'Download JSON'}
        </button>
      </div>
      {error ? <ErrorBox message={error} /> : !data ? <Loading label="Loading the extracted data…" /> : <pre style={preStyle}>{data.text}</pre>}
    </div>
  );
}

function CanonicalTab({ summary }: { summary: DocumentSummary }) {
  const [page, setPage] = useState(1);
  const [view, setView] = useState<CanonView>('table');
  const [downloading, setDownloading] = useState(false);
  const baseName = summary.fileName.replace(/\.[^.]+$/, '');

  const { data, loading, error } = useLoad<CanonicalPage>(
    (signal) => getCanonicalPage(summary.id, page, CANONICAL_PAGE_SIZE, signal),
    [summary.id, page]
  );

  async function download() {
    setDownloading(true);
    try { await downloadCanonicalCsv(summary.id, baseName); } finally { setDownloading(false); }
  }

  const viewButton = (v: CanonView, label: string) => (
    <button
      className="pill"
      style={{ fontSize: 12.5, padding: '5px 12px', background: view === v ? 'var(--blue-soft)' : 'var(--surface)', color: view === v ? 'var(--blue-deep)' : 'var(--text)' }}
      onClick={() => setView(v)}
    >
      {label}
    </button>
  );

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
        <div className="lbl">Phase 2 — canonical analytics records</div>
        {data && (
          <Pager
            page={data.page}
            pageCount={Math.max(1, Math.ceil(data.totalRecords / data.pageSize))}
            onChange={setPage}
            loading={loading}
            detail={`${data.totalRecords.toLocaleString('en-US')} lines`}
          />
        )}
        <span style={{ display: 'flex', marginLeft: 'auto', gap: 7 }}>
          {viewButton('table', 'Table')}
          {viewButton('json', 'JSON')}
          <button className="pill" style={{ fontSize: 12.5, padding: '5px 12px', color: 'var(--blue)' }} disabled={downloading} onClick={download}>
            {downloading ? 'Preparing…' : 'Download CSV'}
          </button>
        </span>
      </div>

      {error ? (
        <ErrorBox message={error} />
      ) : !data ? (
        <Loading label="Loading the canonical data…" />
      ) : view === 'table' ? (
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
              {data.records.map((r, i) => (
                <tr key={i}>
                  <td style={{ ...cell, fontVariantNumeric: 'tabular-nums' }}>{r.customerId}</td>
                  <td style={{ ...cell, color: 'var(--ink)' }}>{r.customerName}</td>
                  <td style={{ ...cell, fontVariantNumeric: 'tabular-nums' }}>{r.date}</td>
                  <td style={cell}>{r.city}</td>
                  <td style={cell}>{r.state}</td>
                  <td style={cell}>{r.productFamily}</td>
                  <td style={{ ...cell, fontVariantNumeric: 'tabular-nums' }}>{r.partNo}</td>
                  <td style={num}>{r.quantity}</td>
                  <td style={num}>{money(r.netSales)}</td>
                  <td style={num}>{money(r.commission)}</td>
                </tr>
              ))}
            </tbody>
            <tfoot>
              <tr>
                <td colSpan={7} style={{ borderBottom: 'none', color: 'var(--ink)', fontWeight: 500, fontSize: 13 }}>
                  Totals — all {data.totalRecords.toLocaleString('en-US')} lines (not just this page)
                </td>
                <td style={{ borderBottom: 'none' }} />
                <td style={{ borderBottom: 'none', textAlign: 'right', color: 'var(--ink)', fontWeight: 700, fontVariantNumeric: 'tabular-nums' }}>
                  {money(data.totalNetSales)}
                </td>
                <td style={{ borderBottom: 'none', textAlign: 'right', color: 'var(--ink)', fontWeight: 700, fontVariantNumeric: 'tabular-nums' }}>
                  {money(data.totalCommission)}
                </td>
              </tr>
            </tfoot>
          </table>
          <div style={{ color: 'var(--muted)', fontSize: 12, marginTop: 10 }}>
            These two totals are the values shown in the queue — recalculate them from the input file to confirm the mapping.
          </div>
        </div>
      ) : (
        <pre style={preStyle}>{JSON.stringify(data.records, null, 2)}</pre>
      )}
    </div>
  );
}

// --- the viewer ----------------------------------------------------------------------------------

export function DocumentViewerModal({ summary, detail, initialTab, onClose }: Props) {
  const [tab, setTab] = useState<ViewerTab>(initialTab);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [onClose]);

  const instructions = detail?.customInstructions ?? summary.customInstructions;

  return (
    <div
      // Full screen: the viewer covers the whole window instead of floating as a dialog, so wide
      // tables and long extractions get all the room there is.
      style={{ position: 'fixed', inset: 0, zIndex: 20, display: 'flex', flexDirection: 'column', background: 'var(--surface)' }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: 16, padding: '16px 28px', borderBottom: '1px solid var(--line)', flex: 'none' }}>
        <span style={{ display: 'flex', flexDirection: 'column', minWidth: 0 }}>
          <span style={{
            fontFamily: "'Source Serif 4',Georgia,serif", fontSize: 20, color: 'var(--ink)',
            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
          }}>
            {summary.fileName}
          </span>
          <span style={{ color: 'var(--muted)', fontSize: 12.5 }}>
            {summary.clientName && <>{summary.clientName} · </>}
            {summary.manufacturer} · {monthLabel(summary.periodMonth)} {summary.periodYear} · {summary.status}
          </span>
        </span>
        <span style={{ display: 'flex', marginLeft: 'auto', gap: 7, alignItems: 'center', flex: 'none' }}>
          {instructions && (
            <TabButton active={tab === 'instructions'} onClick={() => setTab('instructions')}>Instructions</TabButton>
          )}
          <TabButton active={tab === 'source'} onClick={() => setTab('source')}>Input file</TabButton>
          <TabButton active={tab === 'extract'} onClick={() => setTab('extract')}>Extracted data</TabButton>
          <TabButton active={tab === 'canon'} onClick={() => setTab('canon')}>Canonical data</TabButton>
          <button
            title="Close (Esc)"
            aria-label="Close"
            style={{
              width: 34, height: 34, marginLeft: 8, display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
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

      <div style={{ flex: 1, minHeight: 0, overflow: 'auto', padding: '22px 28px 40px', background: 'var(--bg)' }}>
        {tab === 'instructions' && <InstructionsTab text={instructions ?? ''} />}
        {tab === 'source' && <SourceTab summary={summary} detail={detail} />}
        {tab === 'extract' && <ExtractedTab summary={summary} />}
        {tab === 'canon' && <CanonicalTab summary={summary} />}
      </div>
    </div>
  );
}
