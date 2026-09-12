import logo from '../assets/bbb-logo.png';

interface Props {
  onOpenSettings: () => void;
}

export function AppHeader({ onOpenSettings }: Props) {
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

      <button
        className="icon-btn"
        style={{ marginLeft: 'auto' }}
        onClick={onOpenSettings}
        title="Settings"
        aria-label="Settings"
      >
        <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round">
          <circle cx="12" cy="12" r="3" />
          <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1Z" />
        </svg>
      </button>
    </div>
  );
}
