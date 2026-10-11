/** 0:42, 12m, 1h 05m: compact durations for headers and tables. */
export function duration(seconds: number): string {
  const s = Math.max(0, Math.floor(seconds))
  if (s < 60) return `${s}s`
  const m = Math.floor(s / 60)
  if (m < 60) return `${m}m ${String(s % 60).padStart(2, '0')}s`
  return `${Math.floor(m / 60)}h ${String(m % 60).padStart(2, '0')}m`
}

/** 14:05:09 in the viewer's locale. */
export function clock(iso: string): string {
  return new Date(iso).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' })
}

/** 7 Oct 2026, 14:05 in the viewer's locale. */
export function dateTime(iso: string): string {
  return new Date(iso).toLocaleString(undefined, { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' })
}

/** "JobStarted" → "Job started", "phase.set" → "Phase set". */
/** "just now", "5 min ago", "2h ago", "yesterday", "3d ago"; older than a week: the date. */
export function ago(iso: string, now: number = Date.now()): string {
  const minutes = Math.floor((now - new Date(iso).getTime()) / 60_000)
  if (minutes < 1) return 'just now'
  if (minutes < 60) return `${minutes} min ago`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours}h ago`
  const days = Math.floor(hours / 24)
  if (days === 1) return 'yesterday'
  return days < 7 ? `${days}d ago` : new Date(iso).toLocaleDateString()
}

export function humanize(type: string): string {
  const words = type.replace(/[._]/g, ' ').replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase()
  return words.charAt(0).toUpperCase() + words.slice(1)
}

/** 0.62 → "62%". */
export function percent(fraction: number | null | undefined): string {
  return fraction == null ? '—' : `${Math.round(fraction * 100)}%`
}

/** 1536 → "2 KB", 2.1e9 → "2 GB" (binary units, short; matches the chat's wording). */
export function bytes(n: number | null | undefined): string {
  if (n == null) return '—'
  if (n >= 2 ** 30) return `${(n / 2 ** 30).toFixed(1).replace(/\.0$/, '')} GB`
  if (n >= 2 ** 20) return `${Math.round(n / 2 ** 20)} MB`
  return `${Math.floor(Math.max(0, n) / 1024)} KB`
}

