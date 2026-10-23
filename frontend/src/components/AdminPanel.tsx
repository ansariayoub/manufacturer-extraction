import { useCallback, useEffect, useState } from 'react';
import type { AppUser, ClientInfo } from '../types';
import { deleteClient, deleteUser, listClients, listUsers, renameClient, updateUser } from '../api/adminApi';

/**
 * Admin-only overview of who uses the app. Nobody is added by hand: Microsoft Entra (the Enterprise
 * application's user assignment) decides who can sign in, and the first time someone does, the app
 * creates their account and a client carrying their name. Here an admin can rename a client, move a
 * person to another client, make someone an admin, or remove an account.
 */
export function AdminPanel() {
  const [clients, setClients] = useState<ClientInfo[] | null>(null);
  const [users, setUsers] = useState<AppUser[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  const [busy, setBusy] = useState(false);

  const reload = useCallback(async () => {
    try {
      const [c, u] = await Promise.all([listClients(), listUsers()]);
      setClients(c);
      setUsers(u);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load clients and users.');
    }
  }, []);

  useEffect(() => { void reload(); }, [reload]);

  /** Runs one admin action, surfacing a server error (e.g. "client still has users") in the banner. */
  async function run(action: () => Promise<unknown>) {
    setBusy(true);
    setError(null);
    try {
      await action();
      await reload();
    } catch (err) {
      setError(err instanceof Error ? err.message.replace(/^API \d+ [^:]*: /, '') : 'Something went wrong.');
    } finally {
      setBusy(false);
    }
  }

  async function handleRename(c: ClientInfo) {
    const name = window.prompt('New name for this client:', c.name)?.trim();
    if (name && name !== c.name) await run(() => renameClient(c.id, name));
  }

  async function handleDeleteClient(c: ClientInfo) {
    if (window.confirm(`Delete the client "${c.name}"? This only works when it has no users, manufacturers or documents.`)) {
      await run(() => deleteClient(c.id));
    }
  }

  async function handleDeleteUser(u: AppUser) {
    if (window.confirm(`Remove the account of ${u.displayName ?? u.email}? It is created again the next time they sign in, unless you also unassign them in Microsoft Entra.`)) await run(() => deleteUser(u.id));
  }

  return (
    <div className="card" style={{ padding: 22, marginTop: 22 }}>
      <h3 style={{ fontSize: 19, marginBottom: 4 }}>Users</h3>
      <div style={{ color: 'var(--muted)', fontSize: 13, marginBottom: 16 }}>
        Each person who signs in with Microsoft gets their own client, named after them, and only sees their
        own manufacturers and reports. To let someone in, assign them to the application in Microsoft Entra
        (Enterprise applications → Users and groups): their account appears here by itself on first sign-in.
      </div>

      {error && (
        <div style={{ marginBottom: 14, padding: '10px 14px', borderRadius: 8, background: '#fffbeb', border: '1px solid #fde68a', color: '#92400e', fontSize: 13.5 }}>
          {error}
        </div>
      )}

      {!clients || !users ? (
        <div style={{ display: 'flex', alignItems: 'center', gap: 10, color: 'var(--muted)', fontSize: 13.5 }}>
          <span className="spinner" aria-hidden="true" /> Loading…
        </div>
      ) : (
        <>
          <div className="lbl" style={{ marginBottom: 8 }}>Clients</div>
          <div style={{ marginBottom: 26 }}>
            {clients.map((c) => (
              <div key={c.id} style={{ display: 'flex', alignItems: 'center', gap: 12, padding: '10px 0', borderTop: '1px solid var(--line-soft)' }}>
                <span style={{ color: 'var(--ink)', fontWeight: 500 }}>{c.name}</span>
                <span style={{ color: 'var(--muted)', fontSize: 12.5 }}>{c.userCount} user{c.userCount === 1 ? '' : 's'}</span>
                <span style={{ marginLeft: 'auto', display: 'flex', gap: 7 }}>
                  <button className="pill" style={{ fontSize: 12.5, padding: '5px 12px' }} disabled={busy} onClick={() => handleRename(c)}>Rename</button>
                  <button className="pill" style={{ fontSize: 12.5, padding: '5px 12px', color: '#b45309' }} disabled={busy} onClick={() => handleDeleteClient(c)}>Delete</button>
                </span>
              </div>
            ))}
          </div>

          <div className="lbl" style={{ marginBottom: 8 }}>People</div>
          {users.length === 0 && <div style={{ color: 'var(--muted)', fontSize: 13.5, padding: '10px 0' }}>Nobody has signed in yet.</div>}
          {users.map((u) => (
            <div key={u.id} style={{ display: 'flex', alignItems: 'center', gap: 12, padding: '10px 0', borderTop: '1px solid var(--line-soft)', flexWrap: 'wrap' }}>
              <span style={{ display: 'flex', flexDirection: 'column', minWidth: 0 }}>
                <span style={{ color: 'var(--ink)', fontWeight: 500 }}>{u.displayName ?? u.email}</span>
                <span style={{ color: 'var(--muted)', fontSize: 12.5 }}>
                  {u.displayName ? `${u.email} · ` : ''}{u.hasSignedIn ? 'signed in' : 'has not signed in yet'}
                </span>
              </span>
              <span style={{ marginLeft: 'auto', display: 'flex', gap: 8, alignItems: 'center' }}>
                <select
                  className="fld" style={{ height: 32, fontSize: 13 }} value={u.clientId} disabled={busy}
                  onChange={(e) => run(() => updateUser(u.id, { clientId: e.target.value }))}
                >
                  {clients.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
                </select>
                <label style={{ display: 'flex', alignItems: 'center', gap: 5, fontSize: 13, color: 'var(--text)' }}>
                  <input type="checkbox" checked={u.isAdmin} disabled={busy} onChange={(e) => run(() => updateUser(u.id, { isAdmin: e.target.checked }))} /> Admin
                </label>
                <button className="pill" style={{ fontSize: 12.5, padding: '5px 12px', color: '#b45309' }} disabled={busy} onClick={() => handleDeleteUser(u)}>Remove</button>
              </span>
            </div>
          ))}
        </>
      )}
    </div>
  );
}
