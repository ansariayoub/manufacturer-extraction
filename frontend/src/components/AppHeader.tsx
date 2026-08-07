export function AppHeader() {
  return (
    <div
      style={{
        display: 'flex', alignItems: 'center', gap: 14, padding: '14px 40px',
        background: 'var(--surface)', borderBottom: '1px solid var(--line)',
        position: 'sticky', top: 0, zIndex: 5,
      }}
    >
      <span
        style={{
          display: 'flex', alignItems: 'center', justifyContent: 'center',
          width: 28, height: 28, borderRadius: 7, background: 'var(--blue-soft)',
          color: 'var(--blue-deep)', fontFamily: "'Source Serif 4',Georgia,serif",
          fontWeight: 600, fontSize: 14,
        }}
      >
        B
      </span>
      <span style={{ fontFamily: "'Source Serif 4',Georgia,serif", fontSize: 19, color: 'var(--ink)' }}>
        Intake
      </span>
      <span style={{ color: 'var(--muted)', fontSize: 13 }}>Manufacturer report extraction</span>
      <span
        style={{
          display: 'flex', alignItems: 'center', gap: 8, marginLeft: 'auto',
          padding: '6px 14px', border: '1px solid var(--line)', borderRadius: 999,
          fontSize: 13, color: 'var(--blue-deep)', background: 'var(--blue-soft)',
        }}
      >
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
          <path d="M3 21h18" /><path d="M5 21V7l7-4 7 4v14" /><path d="M9 9h.01M15 9h.01M9 13h.01M15 13h.01" />
        </svg>
        BBB Techs
      </span>
    </div>
  );
}