import { useEffect, useState } from 'react';
import type { Manufacturer, ManufacturerPromptHistoryEntry } from '../types';
import type { Theme } from '../hooks/useTheme';
import { listPromptHistory } from '../api/manufacturersApi';
import { getAiModel, setAiModel, getEmbeddingModel, setEmbeddingModel } from '../api/settingsApi';

interface Props {
  manufacturers: Manufacturer[];
  onCreate: (name: string) => Promise<void>;
  onUpdate: (id: string, changes: { name?: string; defaultInstructions?: string | null }) => Promise<void>;
  onDelete: (id: string) => Promise<void>;
  theme: Theme;
  onThemeChange: (t: Theme) => void;
  onClose: () => void;
}

function formatTimestamp(iso: string) {
  return new Date(iso).toLocaleString(undefined, {
    year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit',
  });
}

/**
 * Admin Settings page: manages the manufacturer picker (previously a hardcoded frontend array —
 * see data/manufacturers.ts, now unused) and each manufacturer's default "processing instructions"
 * template, which the upload screen pre-fills whenever that manufacturer is selected. Also holds
 * the light/dark mode toggle.
 */
export function SettingsPage({ manufacturers, onCreate, onUpdate, onDelete, theme, onThemeChange, onClose }: Props) {
  const [newName, setNewName] = useState('');
  const [creating, setCreating] = useState(false);
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [draftInstructions, setDraftInstructions] = useState('');
  const [renameId, setRenameId] = useState<string | null>(null);
  const [renameDraft, setRenameDraft] = useState('');
  const [busyId, setBusyId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const [historyOpenId, setHistoryOpenId] = useState<string | null>(null);
  const [historyLoading, setHistoryLoading] = useState(false);
  const [historyById, setHistoryById] = useState<Record<string, ManufacturerPromptHistoryEntry[]>>({});

  const [aiModel, setAiModelState] = useState<{ current: string; available: string[] } | null>(null);
  const [aiModelBusy, setAiModelBusy] = useState(false);

  const [embeddingModel, setEmbeddingModelState] = useState<{ current: string; available: string[] } | null>(null);
  const [embeddingModelBusy, setEmbeddingModelBusy] = useState(false);

  useEffect(() => {
    getAiModel().then(setAiModelState).catch((err) => {
      console.error('Failed to load AI model settings', err);
    });
    getEmbeddingModel().then(setEmbeddingModelState).catch((err) => {
      console.error('Failed to load embedding model settings', err);
    });
  }, []);

  async function handleAiModelChange(deployment: string) {
    if (aiModelBusy || deployment === aiModel?.current) return;
    setAiModelBusy(true);
    setError(null);
    try {
      setAiModelState(await setAiModel(deployment));
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to switch AI model.');
    } finally {
      setAiModelBusy(false);
    }
  }

  async function handleEmbeddingModelChange(deployment: string) {
    if (embeddingModelBusy || deployment === embeddingModel?.current) return;
    setEmbeddingModelBusy(true);
    setError(null);
    try {
      setEmbeddingModelState(await setEmbeddingModel(deployment));
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to switch embedding model.');
    } finally {
      setEmbeddingModelBusy(false);
    }
  }

  async function handleCreate() {
    const name = newName.trim();
    if (!name) return;
    setCreating(true);
    setError(null);
    try {
      await onCreate(name);
      setNewName('');
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to add manufacturer.');
    } finally {
      setCreating(false);
    }
  }

  function startEditInstructions(m: Manufacturer) {
    setExpandedId(m.id);
    setDraftInstructions(m.defaultInstructions ?? '');
  }

  async function saveInstructions(id: string) {
    setBusyId(id);
    setError(null);
    try {
      await onUpdate(id, { defaultInstructions: draftInstructions.trim() || null });
      setExpandedId(null);
      // The server just recorded the outgoing value as a new history entry — drop the cached list
      // so the next "History" click re-fetches instead of showing a now-stale snapshot.
      setHistoryById((prev) => {
        const { [id]: _drop, ...rest } = prev;
        return rest;
      });
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to save default instructions.');
    } finally {
      setBusyId(null);
    }
  }

  async function saveRename(id: string) {
    const name = renameDraft.trim();
    if (!name) { setRenameId(null); return; }
    setBusyId(id);
    setError(null);
    try {
      await onUpdate(id, { name });
      setRenameId(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to rename manufacturer.');
    } finally {
      setBusyId(null);
    }
  }

  async function toggleHistory(m: Manufacturer) {
    if (historyOpenId === m.id) { setHistoryOpenId(null); return; }
    setHistoryOpenId(m.id);
    setExpandedId(null);
    if (historyById[m.id]) return; // already fetched this session
    setHistoryLoading(true);
    setError(null);
    try {
      const entries = await listPromptHistory(m.id);
      setHistoryById((prev) => ({ ...prev, [m.id]: entries }));
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load prompt history.');
    } finally {
      setHistoryLoading(false);
    }
  }

  /** Loads a past version back into the edit box for review rather than saving it immediately. */
  function restoreVersion(m: Manufacturer, entry: ManufacturerPromptHistoryEntry) {
    setHistoryOpenId(null);
    setExpandedId(m.id);
    setDraftInstructions(entry.instructions);
  }

  async function handleDelete(m: Manufacturer) {
    if (!window.confirm(`Remove "${m.name}" from the manufacturer list? Documents already uploaded under this name are not affected.`)) return;
    setBusyId(m.id);
    setError(null);
    try {
      await onDelete(m.id);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to remove manufacturer.');
    } finally {
      setBusyId(null);
    }
  }

  return (
    <div style={{ maxWidth: 980, margin: '0 auto', padding: '30px 40px' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 14, marginBottom: 26 }}>
        <h2 style={{ fontSize: 28 }}>Settings</h2>
        <button className="pill" style={{ marginLeft: 'auto' }} onClick={onClose}>← Back to queue</button>
      </div>

      {error && (
        <div style={{
          marginBottom: 18, padding: '12px 16px', borderRadius: 8,
          background: '#fffbeb', border: '1px solid #fde68a', color: '#92400e', fontSize: 14,
        }}>
          {error}
        </div>
      )}

      <div style={{ display: 'grid', gridTemplateColumns: 'minmax(0,1fr) minmax(0,1fr) minmax(0,1fr)', gap: 22, marginBottom: 22 }}>
        <div className="card" style={{ padding: 22 }}>
          <h3 style={{ fontSize: 19, marginBottom: 4 }}>Appearance</h3>
          <div style={{ color: 'var(--muted)', fontSize: 13, marginBottom: 14 }}>
            Applies to this browser only.
          </div>
          <div style={{ display: 'flex', gap: 8 }}>
            <button
              className={theme === 'light' ? 'pill pill-solid' : 'pill'}
              onClick={() => onThemeChange('light')}
            >
              ☀ Light
            </button>
            <button
              className={theme === 'dark' ? 'pill pill-solid' : 'pill'}
              onClick={() => onThemeChange('dark')}
            >
              ☾ Dark
            </button>
          </div>
        </div>

        <div className="card" style={{ padding: 22 }}>
          <h3 style={{ fontSize: 19, marginBottom: 4 }}>AI model</h3>
          <div style={{ color: 'var(--muted)', fontSize: 13, marginBottom: 14 }}>
            Which Azure OpenAI deployment the canonical mapper calls. Applies to every document
            processed from now on, app-wide.
          </div>
          {aiModel ? (
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
              {aiModel.available.map((deployment) => (
                <button
                  key={deployment}
                  className={deployment === aiModel.current ? 'pill pill-solid' : 'pill'}
                  disabled={aiModelBusy}
                  onClick={() => handleAiModelChange(deployment)}
                  title={deployment === aiModel.current ? 'Currently active' : `Switch to ${deployment}`}
                >
                  {deployment === aiModel.current ? '✓ ' : ''}{deployment}
                </button>
              ))}
            </div>
          ) : (
            <div style={{ color: 'var(--muted)', fontSize: 13 }}>Loading…</div>
          )}
        </div>

        <div className="card" style={{ padding: 22 }}>
          <h3 style={{ fontSize: 19, marginBottom: 4 }}>Embedding model</h3>
          <div style={{ color: 'var(--muted)', fontSize: 13, marginBottom: 14 }}>
            Which Azure OpenAI embedding deployment is used, once a feature that needs one is built.
          </div>
          {embeddingModel ? (
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
              {embeddingModel.available.map((deployment) => (
                <button
                  key={deployment}
                  className={deployment === embeddingModel.current ? 'pill pill-solid' : 'pill'}
                  disabled={embeddingModelBusy}
                  onClick={() => handleEmbeddingModelChange(deployment)}
                  title={deployment === embeddingModel.current ? 'Currently active' : `Switch to ${deployment}`}
                >
                  {deployment === embeddingModel.current ? '✓ ' : ''}{deployment}
                </button>
              ))}
            </div>
          ) : (
            <div style={{ color: 'var(--muted)', fontSize: 13 }}>Loading…</div>
          )}
        </div>
      </div>

      <div className="card" style={{ padding: 22 }}>
        <h3 style={{ fontSize: 19, marginBottom: 4 }}>Manufacturers</h3>
        <div style={{ color: 'var(--muted)', fontSize: 13, marginBottom: 16 }}>
          The list offered on the upload screen. Give a manufacturer a default set of processing
          instructions here and it will pre-fill the instructions box automatically whenever that
          manufacturer is picked for a new import — still editable per file at upload time.
        </div>

        <div style={{ display: 'flex', gap: 10, marginBottom: 18 }}>
          <input
            className="fld"
            style={{ flex: 1 }}
            placeholder="New manufacturer name…"
            value={newName}
            onChange={(e) => setNewName(e.target.value)}
            onKeyDown={(e) => { if (e.key === 'Enter') handleCreate(); }}
          />
          <button className="pill pill-solid" disabled={creating || !newName.trim()} onClick={handleCreate}>
            {creating ? 'Adding…' : '+ Add manufacturer'}
          </button>
        </div>

        <div style={{ display: 'flex', flexDirection: 'column', gap: 0 }}>
          {manufacturers.length === 0 && (
            <div style={{ color: 'var(--muted)', fontSize: 13.5, padding: '14px 0' }}>No manufacturers yet.</div>
          )}
          {manufacturers.map((m) => (
            <div key={m.id} style={{ borderBottom: '1px solid var(--line-soft)', padding: '14px 0' }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                {renameId === m.id ? (
                  <input
                    className="fld"
                    style={{ flex: 1 }}
                    autoFocus
                    value={renameDraft}
                    onChange={(e) => setRenameDraft(e.target.value)}
                    onKeyDown={(e) => { if (e.key === 'Enter') saveRename(m.id); if (e.key === 'Escape') setRenameId(null); }}
                  />
                ) : (
                  <span style={{ flex: 1, fontSize: 14.5, color: 'var(--ink)' }}>{m.name}</span>
                )}

                {m.defaultInstructions && renameId !== m.id && (
                  <span className="pill" style={{ fontSize: 11.5, padding: '3px 10px', color: 'var(--orange)', cursor: 'default' }}>
                    ✦ has default prompt
                  </span>
                )}

                {renameId === m.id ? (
                  <>
                    <button className="pill" disabled={busyId === m.id} onClick={() => saveRename(m.id)}>Save</button>
                    <button className="pill" onClick={() => setRenameId(null)}>Cancel</button>
                  </>
                ) : (
                  <>
                    <button className="pill" onClick={() => startEditInstructions(m)}>
                      {m.defaultInstructions ? 'Edit prompt' : '+ Default prompt'}
                    </button>
                    <button className={historyOpenId === m.id ? 'pill pill-solid' : 'pill'} onClick={() => toggleHistory(m)}>
                      History
                    </button>
                    <button className="pill" onClick={() => { setRenameId(m.id); setRenameDraft(m.name); }}>Rename</button>
                    <button className="icon-btn" disabled={busyId === m.id} onClick={() => handleDelete(m)} title="Remove manufacturer">
                      <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round">
                        <path d="M3 6h18M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2m3 0-1 14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2L4 6" />
                      </svg>
                    </button>
                  </>
                )}
              </div>

              {expandedId === m.id && (
                <div style={{ marginTop: 12, display: 'flex', flexDirection: 'column', gap: 8 }}>
                  <textarea
                    rows={3}
                    autoFocus
                    placeholder='e.g. On sheet "Direct Sales Data" use "Sales $" as netSales.'
                    style={{
                      width: '100%', padding: '10px 12px', font: 'inherit', fontSize: 13.5,
                      color: 'var(--ink)', background: 'var(--bg)', border: '1px solid var(--line)',
                      borderRadius: 8, resize: 'vertical',
                    }}
                    value={draftInstructions}
                    onChange={(e) => setDraftInstructions(e.target.value)}
                  />
                  <div style={{ display: 'flex', gap: 8 }}>
                    <button className="pill pill-solid" disabled={busyId === m.id} onClick={() => saveInstructions(m.id)}>
                      {busyId === m.id ? 'Saving…' : 'Save default prompt'}
                    </button>
                    <button className="pill" onClick={() => setExpandedId(null)}>Cancel</button>
                  </div>
                </div>
              )}

              {historyOpenId === m.id && (
                <div style={{ marginTop: 12 }}>
                  {historyLoading && !historyById[m.id] ? (
                    <div style={{ color: 'var(--muted)', fontSize: 13, padding: '6px 0' }}>Loading history…</div>
                  ) : (historyById[m.id]?.length ?? 0) === 0 ? (
                    <div style={{ color: 'var(--muted)', fontSize: 13, padding: '6px 0' }}>
                      No earlier versions yet — history starts recording the next time this manufacturer's default prompt is changed.
                    </div>
                  ) : (
                    <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                      {historyById[m.id]!.map((entry) => (
                        <div key={entry.id} style={{
                          display: 'flex', gap: 12, alignItems: 'flex-start',
                          padding: '10px 12px', background: 'var(--bg)', border: '1px solid var(--line)', borderRadius: 8,
                        }}>
                          <div style={{ flex: 1, minWidth: 0 }}>
                            <div className="lbl" style={{ marginBottom: 4 }}>{formatTimestamp(entry.createdDate)}</div>
                            <div style={{
                              fontSize: 13, color: 'var(--text)', whiteSpace: 'pre-wrap', wordBreak: 'break-word',
                            }}>
                              {entry.instructions}
                            </div>
                          </div>
                          <button className="pill" style={{ flex: 'none' }} onClick={() => restoreVersion(m, entry)}>
                            Restore
                          </button>
                        </div>
                      ))}
                    </div>
                  )}
                </div>
              )}
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
