import type { Manufacturer, ManufacturerPromptHistoryEntry } from '../types';
import { apiFetch, handle } from './http';

/** GET /api/manufacturers — the signed-in client's list, sorted by name, for both the upload picker and Settings. */
export async function listManufacturers(): Promise<Manufacturer[]> {
  const res = await apiFetch('/api/manufacturers');
  return handle<Manufacturer[]>(res);
}

/** POST /api/manufacturers — adds a manufacturer not yet on the list. */
export async function createManufacturer(name: string, defaultInstructions?: string | null): Promise<Manufacturer> {
  const res = await apiFetch('/api/manufacturers', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name, defaultInstructions: defaultInstructions ?? null }),
  });
  return handle<Manufacturer>(res);
}

/**
 * PUT /api/manufacturers/{id} — renames a manufacturer and/or saves its default processing-
 * instructions template. Pass only the fields being changed; omitted fields are left untouched
 * server-side (see ManufacturersController.Update).
 */
export async function updateManufacturer(
  id: string,
  changes: { name?: string; defaultInstructions?: string | null }
): Promise<Manufacturer> {
  const res = await apiFetch(`/api/manufacturers/${id}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(changes),
  });
  return handle<Manufacturer>(res);
}

/** DELETE /api/manufacturers/{id} — removes it from the picker; existing documents are unaffected. */
export async function deleteManufacturer(id: string): Promise<void> {
  const res = await apiFetch(`/api/manufacturers/${id}`, { method: 'DELETE' });
  return handle<void>(res);
}

/**
 * GET /api/manufacturers/{id}/prompt-history — past default-prompt versions, newest first. A new
 * entry is recorded automatically (server-side) each time updateManufacturer changes an existing,
 * non-blank default to something different.
 */
export async function listPromptHistory(id: string): Promise<ManufacturerPromptHistoryEntry[]> {
  const res = await apiFetch(`/api/manufacturers/${id}/prompt-history`);
  return handle<ManufacturerPromptHistoryEntry[]>(res);
}
