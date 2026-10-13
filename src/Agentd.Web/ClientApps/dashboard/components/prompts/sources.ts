import type { RouteLocationRaw } from 'vue-router'

/** What sent a prompt (prompt-log.md): the label the Prompts page shows, in the order of its filter. */
export const promptSources: { value: string; label: string }[] = [
  { value: 'job', label: 'Job' },
  { value: 'idea', label: 'Idea' },
  { value: 'chat', label: 'Chat' },
  { value: 'review', label: 'Chat review' },
  { value: 'review-session', label: 'Review' },
  { value: 'review-ask', label: 'Review ask' },
  { value: 'review-fix', label: 'Review fix' },
  { value: 'follow-up', label: 'Follow-up' },
]

export const promptOutcomes = ['running', 'ok', 'error', 'usage-limit', 'timeout', 'cancelled'] as const

export const sourceLabel = (source: string) => promptSources.find((s) => s.value === source)?.label ?? source

/** The page a prompt came from, when its source has one; a chat review, an ask thread, a fix or a follow-up doesn't. */
export function sourcePage(source: string, id: number): RouteLocationRaw | null {
  switch (source) {
    case 'job': return { name: 'job', params: { id } }
    case 'idea': return { name: 'idea', params: { id } }
    case 'chat': return { name: 'chat', params: { id } }
    case 'review-session': return { name: 'review', params: { id } }
    default: return null
  }
}

/** Outcome badge colors; a running prompt has no outcome yet. */
export const outcomeTone: Record<string, string> = {
  ok: 'badge-success',
  error: 'badge-error',
  'usage-limit': 'badge-warning',
  timeout: 'badge-warning',
  cancelled: 'badge-ghost',
}
