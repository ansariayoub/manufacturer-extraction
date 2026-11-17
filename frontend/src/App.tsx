import { useCallback, useEffect, useRef, useState } from 'react';
import type { DocumentDetail, DocumentSummary, Manufacturer, Me } from './types';
import {
  listDocuments, listStatuses, getDocument, uploadDocument,
  analyzeDocument, deleteDocument, reanalyzeDocument,
} from './api/documentsApi';
import { getMe } from './api/adminApi';
import { NotAuthorizedError } from './api/http';
import { signOut } from './auth/auth';
import { listManufacturers, createManufacturer, updateManufacturer, deleteManufacturer } from './api/manufacturersApi';
import { useTheme } from './hooks/useTheme';
import { AppHeader } from './components/AppHeader';
import { NewBatchPanel } from './components/NewBatchPanel';
import { InstructionsPanel } from './components/InstructionsPanel';
import { QueueStats } from './components/QueueStats';
import { DocumentQueueTable } from './components/DocumentQueueTable';
import { DocumentViewerModal } from './components/DocumentViewerModal';
import type { ViewerTab } from './components/DocumentViewerModal';
import { SettingsPage } from './components/SettingsPage';
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

/** Shown to someone who signed in with Microsoft fine but is not on the application's allow-list. */
function AccessDenied({ onSignOut, showSignOut }: { onSignOut: () => void; showSignOut: boolean }) {
  return (
    <div style={{ minHeight: '100vh', display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 24 }}>
      <div className="card" style={{ maxWidth: 460, padding: '34px 36px', textAlign: 'center' }}>
        <h3 style={{ fontSize: 24, marginBottom: 10 }}>Access denied</h3>
        <p style={{ margin: '0 0 22px', color: 'var(--muted)', fontSize: 14.5 }}>
          Your Microsoft account is not on the list of people allowed to use this application.
          Ask an administrator to add you, then sign in again.
        </p>
        {showSignOut && <button className="pill pill-solid" onClick={onSignOut}>Sign out</button>}
      </div>
    </div>
  );
}

/**
 * Entry point: finds out who the signed-in person is (GET /api/me) before anything else is loaded,
 * so a person who is not on the allow-list never even triggers the data requests.
 */
export default function App() {
  const [me, setMe] = useState<Me | null>(null);
  const [denied, setDenied] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    let timer: number | undefined;
    let failures = 0;

    async function load() {
      try {
        const result = await getMe();
        if (!cancelled) { setFailure(null); setMe(result); }
      } catch (err) {
        if (cancelled) return;
        if (err instanceof NotAuthorizedError) { setDenied(true); return; }
        // The API or its serverless database may still be waking up — keep trying, but after a few
        // failures say what is going wrong instead of spinning in silence.
        console.error('Failed to identify the signed-in user, retrying', err);
        failures += 1;
        if (failures >= 3) setFailure(err instanceof Error ? err.message : String(err));
        timer = window.setTimeout(load, 3000);
      }
    }
    load();

    return () => { cancelled = true; if (timer) window.clearTimeout(timer); };
  }, []);

  if (denied) return <AccessDenied onSignOut={() => void signOut()} showSignOut />;
  if (!me) {
    return (
      <div style={{ minHeight: '100vh', display: 'flex', alignItems: 'center', justifyContent: 'center', padding: 24 }}>
        <div style={{ maxWidth: 560, textAlign: 'center', color: 'var(--muted)' }}>
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 12 }}>
            <span className="spinner" aria-hidden="true" /> Connecting…
          </div>
          {failure && (
            <div style={{ marginTop: 22 }}>
              <p style={{ margin: '0 0 10px', fontSize: 14 }}>
                The application could not be reached yet. It keeps retrying; if this lasts, sign in again.
                Last error:
              </p>
              <code style={{
                display: 'block', margin: '0 0 16px', padding: '10px 12px', borderRadius: 8, fontSize: 12.5,
                background: 'var(--bg)', border: '1px solid var(--line)', color: 'var(--ink)', wordBreak: 'break-word',
              }}>
                {failure}
              </code>
              <div style={{ display: 'flex', gap: 8, justifyContent: 'center' }}>
                <button className="pill" onClick={() => window.location.reload()}>Try again</button>
                <button className="pill pill-solid" onClick={() => void signOut()}>Sign out and sign in again</button>
              </div>
            </div>
          )}
        </div>
      </div>
    );
  }
  return <Console me={me} />;
}

function Console({ me }: { me: Me }) {
  // No default: forces an explicit choice from the dropdown rather than silently uploading
  // under whichever manufacturer happened to be first/last selected.
  const [manufacturer, setManufacturer] = useState('');
  const [month, setMonth] = useState('06');
  const [year, setYear] = useState('2026');
  const [instructions, setInstructions] = useState('');

  const [manufacturers, setManufacturers] = useState<Manufacturer[]>([]);
  const [view, setView] = useState<'queue' | 'settings'>('queue');
  const { theme, setTheme } = useTheme();

  const [documents, setDocuments] = useState<DocumentSummary[]>([]);
  const [viewer, setViewer] = useState<{ id: string; tab: ViewerTab } | null>(null);
  // Header-level detail of the open document (the original file's link). Loaded in the background:
  // the viewer itself opens immediately from the queue row, and each tab fetches its own content.
  const [viewerDetail, setViewerDetail] = useState<DocumentDetail | null>(null);

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

  const refreshManufacturers = useCallback(async () => {
    try {
      setManufacturers(await listManufacturers());
    } catch (err) {
      console.error('Failed to load manufacturers', err);
    }
  }, []);

  useEffect(() => { refreshManufacturers(); }, [refreshManufacturers]);

  /**
   * Per the admin's spec: picking a manufacturer pre-fills the instructions box with whatever
   * default was configured for it in Settings, which the operator can still freely edit for this
   * one import without changing the saved default. Switching manufacturer always overwrites the
   * box (even a non-empty one) since the box is scoped to "the import about to happen", not a
   * running draft independent of which manufacturer is selected.
   */
  function handleManufacturerChange(name: string) {
    setManufacturer(name);
    const match = manufacturers.find((m) => m.name === name);
    setInstructions(match?.defaultInstructions ?? '');
  }

  async function handleCreateManufacturer(name: string) {
    const created = await createManufacturer(name);
    setManufacturers((prev) => [...prev, created].sort((a, b) => a.name.localeCompare(b.name)));
  }

  async function handleUpdateManufacturer(id: string, changes: { name?: string; defaultInstructions?: string | null }) {
    const updated = await updateManufacturer(id, changes);
    setManufacturers((prev) =>
      prev.map((m) => (m.id === id ? updated : m)).sort((a, b) => a.name.localeCompare(b.name))
    );
  }

  async function handleDeleteManufacturer(id: string) {
    await deleteManufacturer(id);
    setManufacturers((prev) => prev.filter((m) => m.id !== id));
  }

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
      clientName: me.clientName,
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

  function handleOpen(id: string, tab: ViewerTab) {
    // Open at once — no waiting on the network — then fill in the original file's link.
    setViewer({ id, tab });
    setViewerDetail(null);
    getDocument(id)
      .then((detail) => setViewerDetail(detail))
      .catch((err) => console.error('Failed to load document detail', err));
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
      setViewerDetail(null);
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

  const viewerSummary = viewer ? documents.find((d) => d.id === viewer.id) : undefined;
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

  if (view === 'settings') {
    return (
      <div style={{ minHeight: '100vh', paddingBottom: 72 }}>
        <AppHeader me={me} onOpenSettings={() => setView('queue')} onSignOut={() => void signOut()} />
        <SettingsPage
          me={me}
          manufacturers={manufacturers}
          onCreate={handleCreateManufacturer}
          onUpdate={handleUpdateManufacturer}
          onDelete={handleDeleteManufacturer}
          theme={theme}
          onThemeChange={setTheme}
          onClose={() => setView('queue')}
        />
      </div>
    );
  }

  return (
    <div style={{ minHeight: '100vh', paddingBottom: 72 }}>
      <AppHeader me={me} onOpenSettings={() => setView('settings')} onSignOut={() => void signOut()} />

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
            manufacturers={manufacturers}
            month={month}
            year={year}
            onManufacturerChange={handleManufacturerChange}
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

      {viewer && viewerSummary && (
        <DocumentViewerModal
          key={viewer.id}
          summary={viewerSummary}
          detail={viewerDetail && viewerDetail.id === viewer.id ? viewerDetail : null}
          initialTab={viewer.tab}
          onClose={() => { setViewer(null); setViewerDetail(null); }}
        />
      )}
    </div>
  );
}
