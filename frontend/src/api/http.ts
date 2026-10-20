import { getAccessToken } from '../auth/auth';

export const BASE_URL: string = import.meta.env.VITE_API_BASE_URL ?? 'http://localhost:5193';

/** The caller signed in with Microsoft but is not on the application's allow-list (HTTP 403). */
export class NotAuthorizedError extends Error {
  constructor() {
    super('Your account is not allowed to use this application.');
    this.name = 'NotAuthorizedError';
  }
}

/** fetch() against the API, with the Microsoft bearer token attached when sign-in is enabled. */
export async function apiFetch(path: string, init: RequestInit = {}): Promise<Response> {
  const token = await getAccessToken();
  const headers = new Headers(init.headers);
  if (token) headers.set('Authorization', `Bearer ${token}`);
  return fetch(`${BASE_URL}${path}`, { ...init, headers });
}

export async function handle<T>(res: Response): Promise<T> {
  if (!res.ok) {
    const body = await res.text().catch(() => '');
    if (res.status === 403 && body.includes('not_authorized')) throw new NotAuthorizedError();
    throw new Error(`API ${res.status} ${res.statusText}: ${body}`);
  }
  if (res.status === 204) return undefined as T;
  return res.json() as Promise<T>;
}

/** Fetches a file from the API (a CSV/JSON export) and hands it to the browser as a download. */
export async function downloadFromApi(path: string, fallbackName: string): Promise<void> {
  const res = await apiFetch(path);
  if (!res.ok) throw new Error(`API ${res.status} ${res.statusText}`);

  const disposition = res.headers.get('Content-Disposition') ?? '';
  const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition);
  const fileName = match ? decodeURIComponent(match[1]) : fallbackName;

  const blob = await res.blob();
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = fileName;
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 4000);
}
