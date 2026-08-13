import logo from '../assets/bbb-logo.png';

export function AppHeader() {
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
        Intake
      </span>
      <span style={{ color: 'var(--muted)', fontSize: 13 }}>Manufacturer report extraction</span>
    </div>
  );
}