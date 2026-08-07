import { money0 } from '../utils/format';

interface Props {
  fileCount: number;
  runningCount: number;
  totalNetSales: number;
  totalCommission: number;
}

function StatCard({ label, value, color }: { label: string; value: string | number; color?: string }) {
  return (
    <div className="card" style={{ padding: '14px 16px' }}>
      <div className="lbl">{label}</div>
      <div className="stat" style={color ? { color } : undefined}>{value}</div>
    </div>
  );
}

export function QueueStats({ fileCount, runningCount, totalNetSales, totalCommission }: Props) {
  return (
    <div style={{ display: 'grid', gridTemplateColumns: 'repeat(4,minmax(0,1fr))', gap: 14, marginBottom: 18 }}>
      <StatCard label="Files in batch" value={fileCount} />
      <StatCard label="In flight" value={runningCount} color="var(--blue)" />
      <StatCard label="Batch net sales" value={money0(totalNetSales)} />
      <StatCard label="Batch commissions" value={money0(totalCommission)} />
    </div>
  );
}