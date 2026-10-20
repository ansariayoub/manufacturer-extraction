import type { AppUser, ClientInfo, Me } from '../types';
import { apiFetch, handle } from './http';

/** GET /api/me — who the signed-in caller is; a 403 means "not on the allow-list". */
export async function getMe(): Promise<Me> {
  const res = await apiFetch('/api/me');
  return handle<Me>(res);
}

const json = { 'Content-Type': 'application/json' };

export async function listClients(): Promise<ClientInfo[]> {
  return handle<ClientInfo[]>(await apiFetch('/api/admin/clients'));
}

export async function createClient(name: string): Promise<ClientInfo> {
  return handle<ClientInfo>(await apiFetch('/api/admin/clients', {
    method: 'POST', headers: json, body: JSON.stringify({ name }),
  }));
}

export async function renameClient(id: string, name: string): Promise<ClientInfo> {
  return handle<ClientInfo>(await apiFetch(`/api/admin/clients/${id}`, {
    method: 'PUT', headers: json, body: JSON.stringify({ name }),
  }));
}

export async function deleteClient(id: string): Promise<void> {
  return handle<void>(await apiFetch(`/api/admin/clients/${id}`, { method: 'DELETE' }));
}

export async function listUsers(): Promise<AppUser[]> {
  return handle<AppUser[]>(await apiFetch('/api/admin/users'));
}

export async function createUser(email: string, clientId: string, isAdmin: boolean): Promise<AppUser> {
  return handle<AppUser>(await apiFetch('/api/admin/users', {
    method: 'POST', headers: json, body: JSON.stringify({ email, clientId, isAdmin }),
  }));
}

export async function updateUser(id: string, changes: { clientId?: string; isAdmin?: boolean }): Promise<AppUser> {
  return handle<AppUser>(await apiFetch(`/api/admin/users/${id}`, {
    method: 'PUT', headers: json, body: JSON.stringify(changes),
  }));
}

export async function deleteUser(id: string): Promise<void> {
  return handle<void>(await apiFetch(`/api/admin/users/${id}`, { method: 'DELETE' }));
}
