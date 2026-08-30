import { useCallback, useEffect, useRef, useState } from 'react';
import type { DocumentDetail, DocumentSummary } from './types';
import {
  listDocuments, listStatuses, getDocument, uploadDocument,
  analyzeDocument, deleteDocument, reanalyzeDocument,
} from './api/documentsApi';
import { AppHeader } from './components/AppHeader';
import { NewBatchPanel } from './components/NewBatchPanel';
import { InstructionsPanel } from './components/InstructionsPanel';
import { QueueStats } from './components/QueueStats';
import { DocumentQueueTable } from './components/DocumentQueueTable';
import { DocumentViewerModal } from './components/DocumentViewerModal';
import './styles/tokens.css';

// Fast tick while anything is moving, slow tick when the queue is idle. Previously a flat 2s
// interval fired regardless — and since each tick fetched the full listing (which was slow), the
// in-flight guard skipped most of them and progress appeared frozen.
const POLL_ACTIVE_MS = 1000;
const POLL_IDLE_MS = 6000;

// How many files are uploaded at the same time. Uploads used to be strictly sequential, so with a
// batch of large files nothing appeared in the queue until the last one had finished uploading.
const UPLOAD_CONCURRENCY = 3;

const ACTIVE_STATUSES = ['Queued', 'Extracting', 'Mapping'];

export default function App() {
  // No default: forces an explicit choice from the dropdown rather than silently uploading
  // under whichever manufacturer happened to be first/last selected.
  const [manufacturer, setManufacturer] = useState('');
  const [month, setMonth] = useState('06');
  const [year, setYear] = useState('2026');
  const [instructions, setInstructions] = useState('');

  const [documents, setDocuments] = useState<DocumentSummary[]>([]);
  const [viewer, setViewer] = useState<{ id: string; tab: 'source' | 'extract' | 'canon' | 'instructions' } | null>(null);
  const [viewerDoc, setViewerDoc] = useState<DocumentDetail | null>(null);

  const [initialLoadDone, setInitialLoadDone] = useState(false);
  const [connectionIssue, setConnectionIssue] = useState(false);
  const consecutiveFailuresRef = useRef(0);

  // Prevents overlapping poll requests: if a request is still in flight when the next tick fires,
  // skip that tick rather than piling another one onto the backend.
  const requestInFlightRef = useRef(false);
  const pollRef = useRef<number | null>(null);
  const documentsRef = useRef<DocumentSummary[]>([]);
  documentsRef.current = documents;

  function onSuccess() {
    setInitialLoadDone(true);
    setConnectionIssue(false);
    consecutiveFailuresRef.current = 0;
  }

  function onFailure(err: unknown, what: string) {
    console.error(what, err);
    consecutiveFailuresRef.current += 1;
    // Note: we deliberately do NOT clear `documents` here. Keeping the last-known-good list on
    // screen during a transient failure is what stops the UI from going blank.
    if (consecutiveFailuresRef.current >= 2) setConnectionIssue(true);
  }

  /** Full listing — used on mount and whenever the set of documents may have changed. */
  const refreshFull = useCallback(async () => {
    if (requestInFlightRef.current) return;
    requestInFlightRef.current = true;
    try {
      setDocuments(await listDocuments());
      onSuccess();
    } catch (err) {
      onFailure(err, 'Failed to refresh document queue');
    } finally {
      requestInFlightRef.current = false;
    }
  }, []);

  /**
   * Progress-only tick. Merges status/progress into the rows already on screen and only falls
   * back to a full reload when a document finishes (its totals need fetching) or when the server
   * reports a document we don't know about yet.
   */
  const refreshStatuses = useCallback(async () => {
    if (requestInFlightRef.current) return;
    requestInFlightRef.current = true;
    try {
      const statuses = await listStatuses();
      onSuccess();

      const byId = new Map(statuses.map((s) => [s.id, s]));
      const known = documentsRef.current;

      const needsFullReload =
        statuses.some((s) => !known.find((d) => d.id === s.id)) ||
        known.some((d) => {
          const s = byId.get(d.id);
          return s && ACTIVE_STATUSES.includes(d.status) && !ACTIVE_STATUSES.includes(s.status);
        });

      if (needsFullReload) {
        setDocuments(await listDocuments());
        return;
      }

      setDocuments((prev) =>
        prev.map((d) => {
          const s = byId.get(d.id);
          if (!s || (s.status === d.status && s.progressPct === d.progressPct
            && s.errorMessage === d.errorMessage && s.hasWarnings === d.hasWarnings)) {
            return d;
          }
          return { ...d, status: s.status, progressPct: s.progressPct, errorMessage: s.errorMessage, hasWarnings: s.hasWarnings };
        })
      );
    } catch (err) {
      onFailure(err, 'Failed to refresh pipeline status');
    } finally {
      requestInFlightRef.current = false;
    }
  }, []);

  // Recursive setTimeout rather than setInterval: a slow response can never cause ticks to stack
  // up, and the delay adapts to whether anything is actually running.
  useEffect(() => {
    let cancelled = false;

    async function tick() {
      await refreshStatuses();
      if (cancelled) return;
      const active = documentsRef.current.some((d) => ACTIVE_STATUSES.includes(d.status));
      pollRef.current = window.setTimeout(tick, active ? POLL_ACTIVE_MS : POLL_IDLE_MS);
    }

    refreshFull().then(() => {
      if (!cancelled) pollRef.current = window.setTimeout(tick, POLL_ACTIVE_MS);
    });

    return () => {
      cancelled = true;
      if (pollRef.current) window.clearTimeout(pollRef.current);
    };
  }, [refreshFull, refreshStatuses]);

  async function handleFilesAdded(files: FileList) {
    const meta = { manufacturer, periodMonth: month, periodYear: year, customInstructions: instructions };
    const queue = Array.from(files);

    // Show every dropped file immediately as a placeholder row, before any network call. The user
    // sees the batch land instantly instead of waiting for the uploads to come back.
    const placeholders: DocumentSummary[] = queue.map((file, i) => ({
      id: `pending-${Date.now()}-${i}`,
      fileName: file.name,
      fileSizeBytes: file.size,
      uploadedAt: new Date().toISOString(),
      manufacturer, periodMonth: month, periodYear: year,
      status: 'Queued',
      progressPct: 0,
      errorMessage: null,
      hasWarnings: false,
      totalNetSales: null, totalCommission: null,
      lineCount: null, customerCount: null,
      customInstructions: instructions.trim() || null,
      isCumulative: false,
      monthlyNetSales: null, monthlyCommission: null, monthlyLineCount: null,
    }));
    setDocuments((prev) => [...placeholders, ...prev]);

    let next = 0;
    async function worker() {
      while (true) {
        const index = next++;
        if (index >= queue.length) return;
        const file = queue[index];
        const placeholderId = placeholders[index].id;

        try {
          const doc = await uploadDocument(file, meta);
          // Swap the placeholder for the real row, keeping its position in the list.
          setDocuments((prev) => prev.map((d) => (d.id === placeholderId ? doc : d)));
          try {
            await analyzeDocument(doc.id);
          } catch (err) {
            console.error(`Failed to start analysis for ${file.name}`, err);
          }
        } catch (err) {
          console.error(`Upload failed for ${file.name}`, err);
          setDocuments((prev) =>
            prev.map((d) =>
              d.id === placeholderId
                ? { ...d, status: 'Failed', errorMessage: 'Upload failed', hasWarnings: true }
                : d
            )
          );
        }
      }
    }

    await Promise.all(Array.from({ length: Math.min(UPLOAD_CONCURRENCY, queue.length) }, worker));
    refreshFull();
  }

  async function handleOpen(id: string, tab: 'source' | 'extract' | 'canon' | 'instructions') {
    setViewer({ id, tab });
    try {
      const detail = await getDocument(id);
      setViewerDoc(detail);
    } catch (err) {
      console.error('Failed to load document detail', err);
      setViewer(null);
    }
  }

  async function handleReanalyze(id: string) {
    // Re-runs the canonical mapping with the instructions currently in the panel, so the user can
    // iterate on their rules without re-uploading the file.
    setDocuments((prev) =>
      prev.map((d) =>
        d.id === id
          ? { ...d, status: 'Queued', progressPct: 0, errorMessage: null, hasWarnings: false }
          : d
      )
    );
    try {
      await reanalyzeDocument(id, instructions.trim() || null);
    } catch (err) {
      console.error(`Failed to re-analyze document ${id}`, err);
      refreshFull();
    }
  }

  async function handleRemove(id: string) {
    setDocuments((prev) => prev.filter((d) => d.id !== id));
    if (viewer?.id === id) {
      setViewer(null);
      setViewerDoc(null);
    }
    try {
      await deleteDocument(id);
    } catch (err) {
      console.error(`Failed to delete document ${id} on the backend — it may reappear on next refresh`, err);
      refreshFull();
    }
  }

  function handleClearCompleted() {
    const doneIds = documents.filter((d) => d.status === 'Done').map((d) => d.id);
    setDocuments((prev) => prev.filter((d) => d.status !== 'Done'));
    doneIds.forEach((id) => deleteDocument(id).catch((err) => console.error('Failed to delete', id, err)));
  }

  const doneDocs = documents.filter((d) => d.status === 'Done');
  const runningCount = documents.filter((d) => ACTIVE_STATUSES.includes(d.status)).length;
  // For a year-to-date report the batch total must use the month's own figure, otherwise adding
  // a cumulative total to monthly ones silently double-counts everything since January.
  const periodNet = (d: DocumentSummary) =>
    (d.isCumulative && d.monthlyNetSales != null ? d.monthlyNetSales : d.totalNetSales) ?? 0;
  const periodComm = (d: DocumentSummary) =>
    (d.isCumulative && d.monthlyCommission != null ? d.monthlyCommission : d.totalCommission) ?? 0;

  const totalNetSales = doneDocs.reduce((a, d) => a + periodNet(d), 0);
  const totalCommission = doneDocs.reduce((a, d) => a + periodComm(d), 0);
  const flaggedCount = doneDocs.filter((d) => d.hasWarnings).length;
  const queueSummary = `${month}/${year} · ${manufacturer || 'no manufacturer set'} · ${doneDocs.length} of ${documents.length} complete` +
    (flaggedCount > 0 ? ` · ${flaggedCount} flagged` : '');

  return (
    <div style={{ minHeight: '100vh', paddingBottom: 72 }}>
      <AppHeader />

      <div style={{ maxWidth: 1680, margin: '0 auto', padding: '30px 40px' }}>
        {connectionIssue && (
          <div style={{
            marginBottom: 20, padding: '12px 16px', borderRadius: 8,
            background: '#fffbeb', border: '1px solid #fde68a', color: '#92400e', fontSize: 14,
          }}>
            {initialLoadDone
              ? 'Connection to the server is slow or was lost — retrying now, displayed data may be slightly out of date.'
              : "Connecting to the database... if this is the first request in a while, the Azure SQL (serverless) database can take up to a minute to wake up."}
          </div>
        )}

        <div style={{ display: 'grid', gridTemplateColumns: 'minmax(0,1.05fr) minmax(0,1fr)', gap: 18, alignItems: 'stretch', marginBottom: 34 }}>
          <NewBatchPanel
            manufacturer={manufacturer}
            month={month}
            year={year}
            onManufacturerChange={setManufacturer}
            onMonthChange={setMonth}
            onYearChange={setYear}
            onFilesAdded={handleFilesAdded}
          />
          <InstructionsPanel instructions={instructions} onInstructionsChange={setInstructions} />
        </div>

        <div style={{ display: 'flex', alignItems: 'flex-end', gap: 14, marginBottom: 16 }}>
          <div>
            <h3 style={{ fontSize: 26 }}>Processing queue</h3>
            <div style={{ color: 'var(--muted)', fontSize: 13.5 }}>{queueSummary}</div>
          </div>
          <button className="pill" style={{ marginLeft: 'auto' }} onClick={handleClearCompleted}>
            Clear completed
          </button>
        </div>

        <QueueStats
          fileCount={documents.length}
          runningCount={runningCount}
          totalNetSales={totalNetSales}
          totalCommission={totalCommission}
        />

        <DocumentQueueTable
          documents={documents}
          loading={!initialLoadDone}
          onOpen={handleOpen}
          onRemove={handleRemove}
          onReanalyze={handleReanalyze}
        />
      </div>

      {viewer && viewerDoc && viewerDoc.id === viewer.id && (
        <DocumentViewerModal
          doc={viewerDoc}
          initialTab={viewer.tab}
          onClose={() => { setViewer(null); setViewerDoc(null); }}
        />
      )}
    </div>
  );
}
