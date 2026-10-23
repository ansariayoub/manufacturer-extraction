import { useEffect, useState } from 'react';
import {
  getExtractionPrompt, setExtractionPrompt, resetExtractionPrompt, type ExtractionPromptSettings,
} from '../api/settingsApi';

/**
 * Admin-only editor for the default system prompt the extraction pipeline sends to Azure OpenAI.
 * App-wide: it applies to every client from the next document processed. The JSON output schema is
 * shown read-only because its field names are tied to the stored data, the viewer and the exports.
 */
export function ExtractionPromptPanel() {
  const [settings, setSettings] = useState<ExtractionPromptSettings | null>(null);
  const [draft, setDraft] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);
  const [showSchema, setShowSchema] = useState(false);

  useEffect(() => {
    getExtractionPrompt()
      .then((s) => { setSettings(s); setDraft(s.prompt); })
      .catch((err) => setError(err instanceof Error ? err.message : 'Failed to load the prompt.'));
  }, []);

  const normalize = (s: string) => s.replace(/\r\n/g, '\n').trim();
  const dirty = settings !== null && normalize(draft) !== normalize(settings.prompt);

  async function run(action: () => Promise<ExtractionPromptSettings>, confirmText?: string) {
    if (confirmText && !window.confirm(confirmText)) return;
    setBusy(true);
    setError(null);
    setSaved(false);
    try {
      const next = await action();
      setSettings(next);
      setDraft(next.prompt);
      setSaved(true);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Request failed.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="card" style={{ padding: 22, marginBottom: 22 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 4 }}>
        <h3 style={{ fontSize: 19 }}>Extraction prompt</h3>
        {settings && (
          <span className="pill" style={{ fontSize: 11.5, padding: '3px 10px', cursor: 'default', color: settings.isCustomized ? 'var(--orange)' : undefined }}>
            {settings.isCustomized ? '✦ customized' : 'factory default'}
          </span>
        )}
      </div>
      <div style={{ color: 'var(--muted)', fontSize: 13, marginBottom: 14 }}>
        The default instructions sent to OpenAI to turn each report into the canonical rows. Applies to every
        client, from the next document processed. Per-manufacturer instructions are still added after it and win
        on conflicts. If you rename a field in the text, the output schema below does not follow: keep the field
        names identical to the schema.
      </div>

      {error && (
        <div style={{ marginBottom: 12, padding: '10px 14px', borderRadius: 8, background: '#fffbeb', border: '1px solid #fde68a', color: '#92400e', fontSize: 13.5 }}>
          {error}
        </div>
      )}

      {!settings ? (
        <div style={{ color: 'var(--muted)', fontSize: 13 }}>{error ? '' : 'Loading…'}</div>
      ) : (
        <>
          <textarea
            rows={22}
            spellCheck={false}
            value={draft}
            onChange={(e) => { setDraft(e.target.value); setSaved(false); }}
            style={{
              width: '100%', padding: '12px 14px', fontFamily: 'ui-monospace, Consolas, monospace', fontSize: 12.5,
              lineHeight: 1.5, color: 'var(--ink)', background: 'var(--bg)', border: '1px solid var(--line)',
              borderRadius: 8, resize: 'vertical',
            }}
          />
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 10, flexWrap: 'wrap' }}>
            <button
              className="pill pill-solid"
              disabled={busy || !dirty || !draft.trim() || draft.length > settings.maxLength}
              onClick={() => run(() => setExtractionPrompt(draft))}
            >
              {busy ? 'Saving…' : 'Save prompt'}
            </button>
            <button className="pill" disabled={busy || !dirty} onClick={() => { setDraft(settings.prompt); setSaved(false); }}>
              Discard changes
            </button>
            <button
              className="pill"
              disabled={busy || !settings.isCustomized}
              onClick={() => run(resetExtractionPrompt, 'Go back to the factory prompt? Your customized text will be lost.')}
            >
              Reset to factory default
            </button>
            {saved && <span style={{ color: 'var(--muted)', fontSize: 13 }}>Saved — used from the next document.</span>}
            <span style={{ marginLeft: 'auto', color: draft.length > settings.maxLength ? '#b45309' : 'var(--muted)', fontSize: 12.5 }}>
              {draft.length.toLocaleString()} / {settings.maxLength.toLocaleString()}
            </span>
          </div>

          <div style={{ marginTop: 16 }}>
            <button className="pill" onClick={() => setShowSchema((v) => !v)}>
              {showSchema ? 'Hide' : 'Show'} output schema (read-only)
            </button>
            {showSchema && (
              <pre style={{
                marginTop: 10, padding: '12px 14px', fontSize: 12, overflow: 'auto', maxHeight: 360,
                background: 'var(--bg)', border: '1px solid var(--line)', borderRadius: 8, color: 'var(--ink)',
              }}>
                {settings.schema}
              </pre>
            )}
          </div>
        </>
      )}
    </div>
  );
}
