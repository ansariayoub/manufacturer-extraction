import type { DocumentSummary } from '../types';
import { fileExt, fileSizeLabel, money, STATUS_COLOR, STATUS_LABEL, timeLabel } from '../utils/format';

interface Props {
  documents: DocumentSummary[];
  /** True during the very first load — shows placeholder rows instead of the "empty" message. */
  loading?: boolean;
  onOpen: (id: string, tab: 'source' | 'extract' | 'canon') => void;
  onRemove: (id: string) => void;
  onReanalyze: (id: string) => void;
}

export function DocumentQueueTable({ documents, loading = false, onOpen, onRemove, onReanalyze }: Props) {
  return (
    <div className="card" style={{ padding: '6px 20px 10px' }}>
      <table>
        <thead>
          <tr>
            <th style={{ width: '28%' }}>File</th>
            <th style={{ width: '17%' }}>Pipeline</th>
            <th style={{ width: '11%', textAlign: 'right' }}>Total net sales</th>
            <th style={{ width: '11%', textAlign: 'right' }}>Total commissions</th>
            {/* Fixed px, not '1%' — table-layout: fixed takes declared widths literally, it no
                longer shrinks this column to fit its three pill buttons the way auto layout did. */}
            <th style={{ width: 250, textAlign: 'right', whiteSpace: 'nowrap' }}>Inspect</th>
            <th style={{ width: 48 }} />
          </tr>
        </thead>
        <tbody>
          {documents.map((doc) => {
            const isDone = doc.status === 'Done';
            const notDone = !isDone;
            const showBar = doc.status === 'Extracting' || doc.status === 'Mapping';
            const statusLabel = STATUS_LABEL[doc.status];
            const statusColor = STATUS_COLOR[doc.status];
            const meta = `${fileSizeLabel(doc.fileSizeBytes)} · uploaded ${timeLabel(doc.uploadedAt)}`;
            const lineNote = doc.lineCount != null && doc.customerCount != null
              ? `${doc.lineCount} canonical lines · ${doc.customerCount} customers`
              : null;

            // A year-to-date report accumulates from January, so its own total is not comparable
            // to a monthly figure. When the previous month is available we derive the month and
            // show THAT as the headline number, with the cumulative total underneath.
            const hasMonthly = doc.isCumulative && doc.monthlyNetSales != null;
            const headlineNet = hasMonthly ? doc.monthlyNetSales : doc.totalNetSales;
            const headlineComm = hasMonthly ? doc.monthlyCommission : doc.totalCommission;

            return (
              <tr key={doc.id}>
                <td>
                  <div style={{ display: 'flex', gap: 10, alignItems: 'flex-start' }}>
                    <span style={{
                      marginTop: 2, flex: 'none', padding: '2px 9px', borderRadius: 999,
                      background: 'var(--bg)', border: '1px solid var(--line)', fontSize: 10.5,
                      letterSpacing: '0.06em', color: 'var(--muted)',
                    }}>
                      {fileExt(doc.fileName)}
                    </span>
                    <span style={{ display: 'flex', flexDirection: 'column', gap: 2, minWidth: 0 }}>
                      <span style={{ color: 'var(--ink)', fontWeight: 500, overflow: 'hidden', textOverflow: 'ellipsis' }}>
                        {doc.fileName}
                        {doc.isCumulative && (
                          <span
                            title={hasMonthly
                              ? "Year-to-date report. The headline figure is this month alone, derived by subtracting the previous month's report; the cumulative total is shown underneath."
                              : "Year-to-date report: this total accumulates from January and is NOT the month's figure. Upload the previous month for the same manufacturer and period to derive it."}
                            style={{
                              marginLeft: 8, padding: '1px 7px', borderRadius: 5, fontSize: 10.5,
                              letterSpacing: '0.04em', verticalAlign: 'middle', cursor: 'help',
                              background: hasMonthly ? 'var(--blue-soft)' : '#fffbeb',
                              color: hasMonthly ? 'var(--blue-deep)' : '#b45309',
                              border: hasMonthly ? 'none' : '1px solid #fde68a',
                            }}
                          >
                            YTD
                          </span>
                        )}
                      </span>
                      <span style={{ color: 'var(--muted)', fontSize: 12 }}>{meta}</span>
                      {doc.customInstructions && (
                        // Shows exactly which rules this file was actually sent with — the panel
                        // above only reflects what the NEXT upload will use.
                        <span
                          title={doc.customInstructions}
                          style={{
                            marginTop: 2, width: 'fit-content', maxWidth: '100%',
                            padding: '2px 8px', borderRadius: 6, fontSize: 11.5,
                            background: 'var(--blue-soft)', color: 'var(--blue-deep)',
                            overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                          }}
                        >
                          ✦ {doc.customInstructions}
                        </span>
                      )}
                    </span>
                  </div>
                </td>
                <td>
                  <div style={{ display: 'flex', flexDirection: 'column', gap: 7 }}>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 13, color: statusColor }}>
                      <span style={{ width: 7, height: 7, borderRadius: 999, background: statusColor }} />
                      <span>{doc.status === 'Failed' && doc.errorMessage ? doc.errorMessage : statusLabel}</span>
                    </div>
                    {isDone && doc.hasWarnings && doc.errorMessage && (
                      // The full warning list (which chunks fell short, by how much) is in the
                      // tooltip — the totals on this row are not safe to use as-is.
                      <div title={doc.errorMessage} style={{
                        display: 'flex', alignItems: 'center', gap: 6, fontSize: 11.5,
                        color: '#b45309', background: '#fffbeb', border: '1px solid #fde68a',
                        borderRadius: 6, padding: '3px 8px', width: 'fit-content', cursor: 'help',
                      }}>
                        ⚠ Incomplete extraction — hover for details
                      </div>
                    )}
                    {showBar && (
                      <div style={{ height: 5, borderRadius: 999, background: '#eaeff6', width: 150, overflow: 'hidden' }}>
                        <div style={{
                          height: '100%', borderRadius: 999, background: 'var(--blue)',
                          width: `${Math.round(doc.progressPct)}%`, transition: 'width .3s linear',
                        }} />
                      </div>
                    )}
                    {isDone && lineNote && <span style={{ color: 'var(--muted)', fontSize: 12 }}>{lineNote}</span>}
                  </div>
                </td>
                <td style={{ textAlign: 'right', color: 'var(--ink)', fontVariantNumeric: 'tabular-nums', paddingTop: 15 }}>
                  {isDone ? money(headlineNet) : '—'}
                  {isDone && doc.isCumulative && (
                    <div style={{ color: 'var(--muted)', fontSize: 11.5, fontWeight: 400, marginTop: 3 }}>
                      {hasMonthly ? `YTD ${money(doc.totalNetSales)}` : 'YTD cumulative'}
                    </div>
                  )}
                </td>
                <td style={{ textAlign: 'right', color: 'var(--ink)', fontVariantNumeric: 'tabular-nums', paddingTop: 15 }}>
                  {isDone ? money(headlineComm) : '—'}
                </td>
                <td style={{ paddingTop: 12 }}>
                  <div style={{ display: 'flex', gap: 7, justifyContent: 'flex-end', flexWrap: 'nowrap' }}>
                    <button className="pill" style={{ fontSize: 12.5, padding: '6px 13px' }} onClick={() => onOpen(doc.id, 'source')}>
                      Input file
                    </button>
                    <button className="pill" style={{ fontSize: 12.5, padding: '6px 13px' }} disabled={notDone} onClick={() => onOpen(doc.id, 'extract')}>
                      Extracted
                    </button>
                    <button className="pill pill-solid" style={{ fontSize: 12.5, padding: '6px 13px' }} disabled={notDone} onClick={() => onOpen(doc.id, 'canon')}>
                      Canonical
                    </button>
                  </div>
                </td>
                <td style={{ textAlign: 'right', paddingTop: 12, whiteSpace: 'nowrap' }}>
                  <button
                    title="Re-run the canonical mapping with the instructions currently in the panel above (does not re-upload or re-extract the file)"
                    className="icon-btn"
                    disabled={notDone && doc.status !== 'Failed'}
                    onClick={() => onReanalyze(doc.id)}
                  >
                    <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
                      <path d="M21 12a9 9 0 1 1-2.64-6.36" /><path d="M21 3v6h-6" />
                    </svg>
                  </button>
                  <button title="Remove from batch" className="icon-btn" onClick={() => onRemove(doc.id)}>
                    <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
                      <path d="M3 6h18" /><path d="M8 6V4h8v2" /><path d="M6 6l1 14h10l1-14" /><path d="M10 11v6M14 11v6" />
                    </svg>
                  </button>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
      {documents.length === 0 && loading && (
        // Placeholder rows during the first load. An Azure SQL serverless database can take a
        // while to wake up, and a bare empty table read as "the app is broken".
        <div style={{ padding: '14px 8px 24px', display: 'flex', flexDirection: 'column', gap: 14 }}>
          {[0, 1, 2].map((i) => (
            <div key={i} style={{ display: 'flex', gap: 16, alignItems: 'center', opacity: 0.55 }}>
              <div style={{ height: 12, width: '32%', borderRadius: 6, background: '#eaeff6' }} />
              <div style={{ height: 12, width: '18%', borderRadius: 6, background: '#eff3f8' }} />
              <div style={{ height: 12, width: '12%', borderRadius: 6, background: '#eff3f8' }} />
            </div>
          ))}
          <div style={{ color: 'var(--muted)', fontSize: 13 }}>Loading the queue…</div>
        </div>
      )}
      {documents.length === 0 && !loading && (
        <div style={{ padding: '40px 8px', textAlign: 'center', color: 'var(--muted)', fontSize: 13.5 }}>
          Nothing in the queue yet — drop this month's manufacturer reports above.
        </div>
      )}
    </div>
  );
}