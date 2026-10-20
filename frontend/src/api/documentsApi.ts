import type {
  CanonicalPage, DocumentDetail, DocumentStatus, DocumentSummary, ExtractedPage, SourcePage, UploadRequest,
} from '../types';
import { apiFetch, downloadFromApi, handle } from './http';

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

  const res = await apiFetch('/api/documents/upload', { method: 'POST', body: form });
  return handle<DocumentSummary>(res);
}

/**
 * Kicks off the background pipeline for a document (POST /api/documents/{id}/analyze).
 * Fire-and-forget from the UI's perspective: poll listDocuments()/getDocument() afterwards.
 */
export async function analyzeDocument(id: string): Promise<void> {
  const res = await apiFetch(`/api/documents/${id}/analyze`, { method: 'POST' });
  return handle<void>(res);
}

/** GET /api/documents — used to refresh the queue table + stat cards. */
export async function listDocuments(): Promise<DocumentSummary[]> {
  const res = await apiFetch('/api/documents');
  return handle<DocumentSummary[]>(res);
}

/**
 * GET /api/documents/status — progress-only rows, a few small columns each.
 * This is what the polling loop calls: the full listing is only refetched when the set of
 * documents actually changes, not on every tick.
 */
export async function listStatuses(): Promise<DocumentStatus[]> {
  const res = await apiFetch('/api/documents/status');
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
  const res = await apiFetch(`/api/documents/${id}/reanalyze`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ customInstructions }),
  });
  return handle<DocumentSummary>(res);
}

/** GET /api/documents/{id} — header-level detail; returns instantly, the tabs load their own content. */
export async function getDocument(id: string): Promise<DocumentDetail> {
  const res = await apiFetch(`/api/documents/${id}`);
  return handle<DocumentDetail>(res);
}

/** GET /api/documents/{id}/canonical — one page of the canonical lines plus whole-document totals. */
export async function getCanonicalPage(id: string, page: number, pageSize = 500, signal?: AbortSignal): Promise<CanonicalPage> {
  const res = await apiFetch(`/api/documents/${id}/canonical?page=${page}&pageSize=${pageSize}`, { signal });
  return handle<CanonicalPage>(res);
}

/** GET /api/documents/{id}/extracted — one slice of the pretty-printed raw extraction. */
export async function getExtractedPage(id: string, page: number, signal?: AbortSignal): Promise<ExtractedPage> {
  const res = await apiFetch(`/api/documents/${id}/extracted?page=${page}`, { signal });
  return handle<ExtractedPage>(res);
}

/** GET /api/documents/{id}/source — one page of rows from one sheet of the original workbook. */
export async function getSourcePage(id: string, sheet: number, page: number, pageSize = 300, signal?: AbortSignal): Promise<SourcePage> {
  const res = await apiFetch(`/api/documents/${id}/source?sheet=${sheet}&page=${page}&pageSize=${pageSize}`, { signal });
  return handle<SourcePage>(res);
}

export function downloadCanonicalCsv(id: string, baseName: string): Promise<void> {
  return downloadFromApi(`/api/documents/${id}/canonical.csv`, `${baseName}.canonical.csv`);
}

export function downloadExtractedJson(id: string, baseName: string): Promise<void> {
  return downloadFromApi(`/api/documents/${id}/extracted/download`, `${baseName}.extraction.json`);
}

/** DELETE /api/documents/{id} — remove-from-batch button in the queue table. */
export async function deleteDocument(id: string): Promise<void> {
  const res = await apiFetch(`/api/documents/${id}`, { method: 'DELETE' });
  return handle<void>(res);
}
