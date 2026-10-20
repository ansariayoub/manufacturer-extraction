import logo from '../assets/bbb-logo.png';
import type { Me } from '../types';

interface Props {
  me: Me;
  onOpenSettings: () => void;
  onSignOut: () => void;
}

export function AppHeader({ me, onOpenSettings, onSignOut }: Props) {
  return (
    <div
      style={{
        display: 'flex', alignItems: 'center', gap: 14, padding: '14px 40px',
        background: 'var(--surface)', borderBottom: '1px solid var(--line)',
        position: 'sticky', top: 0, zIndex: 5,
      }}
    >
      <img
        src={logo}
        alt="Logo"
        style={{ width: 28, height: 28, objectFit: 'contain', borderRadius: 7 }}
      />
      <span style={{ fontFamily: "'Source Serif 4',Georgia,serif", fontSize: 19, color: 'var(--ink)' }}>
        Sales & Commissions import
      </span>
      <span style={{ color: 'var(--muted)', fontSize: 13 }}>Report intake & processing</span>

      <span style={{ marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: 14 }}>
        {me.authEnabled && (
          <span style={{ display: 'flex', flexDirection: 'column', alignItems: 'flex-end', lineHeight: 1.25 }}>
            <span style={{ fontSize: 13, color: 'var(--ink)' }}>{me.displayName}</span>
            <span style={{ fontSize: 11.5, color: 'var(--muted)' }}>{me.clientName}{me.isAdmin ? ' · admin' : ''}</span>
          </span>
        )}
        {me.authEnabled && (
          <button className="pill" style={{ fontSize: 12.5, padding: '5px 13px' }} onClick={onSignOut}>Sign out</button>
        )}
        <button
          className="icon-btn"
          onClick={onOpenSettings}
          title="Settings"
          aria-label="Settings"
        >
          <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round">
            <circle cx="12" cy="12" r="3" />
            <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1Z" />
          </svg>
        </button>
      </span>
    </div>
  );
}
