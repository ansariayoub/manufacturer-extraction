export function money(n: number | null | undefined): string {
  if (n === null || n === undefined) return '—';
  return '$' + n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

export function money0(n: number | null | undefined): string {
  if (n === null || n === undefined) return '—';
  return '$' + Math.round(n).toLocaleString('en-US');
}

export function fileSizeLabel(bytes: number): string {
  const kb = Math.round(bytes / 1024);
  return kb.toLocaleString('en-US') + ' KB';
}

export function timeLabel(iso: string): string {
  return new Date(iso).toLocaleTimeString('en-US', {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  });
}

export const MONTHS = [
  'January', 'February', 'March', 'April', 'May', 'June',
  'July', 'August', 'September', 'October', 'November', 'December',
];

export function monthLabel(month: string): string {
  return MONTHS[parseInt(month, 10) - 1] ?? month;
}

export function fileExt(name: string): string {
  return (name.split('.').pop() || '').toUpperCase();
}

export const STATUS_LABEL: Record<string, string> = {
  Queued: 'Queued',
  Extracting: 'Phase 1 · extracting',
  Mapping: 'Phase 2 · mapping',
  Done: 'Complete',
  Failed: 'Failed · no table detected',
};

export const STATUS_COLOR: Record<string, string> = {
  Queued: 'var(--muted)',
  Extracting: 'var(--blue)',
  Mapping: 'var(--blue)',
  Done: 'var(--blue-deep)',
  Failed: 'var(--orange)',
};