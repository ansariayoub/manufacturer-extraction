import type { DocumentDetail, DocumentStatus, DocumentSummary, UploadRequest } from '../types';

const BASE_URL = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5193';

async function handle<T>(res: Response): Promise<T> {
  if (!res.ok) {
    const body = await res.text().catch(() => '');
    throw new Error(`API ${res.status} ${res.statusText}: ${body}`);
  }
  // 204 No Content
  if (res.status === 204) return undefined as T;
  return res.json() as Promise<T>;
}

/**
 * Uploads a single file (POST /api/documents/upload, multipart/form-data).
 * Mirrors: BlobStorageService.UploadAsync + Documents row creation in DocumentsController.
 */
export async function uploadDocument(
  file: File,
  meta: UploadRequest
): Promise<DocumentSummary> {
  const form = new FormData();
  form.append('file', file);
  form.append('manufacturer', meta.manufacturer);
  form.append('periodMonth', meta.periodMonth);
  form.append('periodYear', meta.periodYear);
  form.append('customInstructions', meta.customInstructions ?? '');

  const res = await fetch(`${BASE_URL}/api/documents/upload`, {
    method: 'POST',
    body: form,
  });
  return handle<DocumentSummary>(res);
}

/**
 * Kicks off the background pipeline for a document
 * (POST /api/documents/{id}/analyze -> Task.Run with IServiceScopeFactory, see Bug 1).
 * Fire-and-forget from the UI's perspective: poll listDocuments()/getDocument() afterwards.
 */
export async function analyzeDocument(id: string): Promise<void> {
  const res = await fetch(`${BASE_URL}/api/documents/${id}/analyze`, {
    method: 'POST',
  });
  return handle<void>(res);
}

/** GET /api/documents — used to refresh the queue table + stat cards. */
export async function listDocuments(): Promise<DocumentSummary[]> {
  const res = await fetch(`${BASE_URL}/api/documents`);
  return handle<DocumentSummary[]>(res);
}

/**
 * GET /api/documents/status — progress-only rows, a few small columns each.
 * This is what the polling loop calls: the full listing is only refetched when the set of
 * documents actually changes, not on every tick.
 */
export async function listStatuses(): Promise<DocumentStatus[]> {
  const res = await fetch(`${BASE_URL}/api/documents/status`);
  return handle<DocumentStatus[]>(res);
}

/**
 * POST /api/documents/{id}/reanalyze — re-runs the canonical mapping, optionally with new
 * instructions. Reuses the stored raw extraction, so Content Understanding is not called again.
 */
export async function reanalyzeDocument(
  id: string,
  customInstructions: string | null
): Promise<DocumentSummary> {
  const res = await fetch(`${BASE_URL}/api/documents/${id}/reanalyze`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ customInstructions }),
  });
  return handle<DocumentSummary>(res);
}

/** GET /api/documents/{id} — used to populate the source/extract/canonical viewer. */
export async function getDocument(id: string): Promise<DocumentDetail> {
  const res = await fetch(`${BASE_URL}/api/documents/${id}`);
  return handle<DocumentDetail>(res);
}

/** DELETE /api/documents/{id} — remove-from-batch button in the queue table. */
export async function deleteDocument(id: string): Promise<void> {
  const res = await fetch(`${BASE_URL}/api/documents/${id}`, { method: 'DELETE' });
  return handle<void>(res);
}

/** Convenience: upload then immediately trigger analysis, as the "drop files" flow does. */
export async function uploadAndAnalyze(
  file: File,
  meta: UploadRequest
): Promise<DocumentSummary> {
  const doc = await uploadDocument(file, meta);
  await analyzeDocument(doc.id);
  return doc;
}
