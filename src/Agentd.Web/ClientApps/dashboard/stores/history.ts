import { defineStore } from 'pinia'
import { ref } from 'vue'
import { ApiError, get } from '../../shared/api/http'
import type { HistoryPage } from '../../shared/api/types'

export interface HistoryFilters {
  states: string[]
  repo: string
  q: string
  from: string
  to: string
  page: number
}

export const finalStates = ['Done', 'Failed', 'Cancelled']
export const pageSize = 25

/** Filters ↔ URL query, so a history view can be shared and survives a reload. */
export function toQuery(f: HistoryFilters): Record<string, string> {
  const q: Record<string, string> = {}
  if (f.states.length && f.states.length < finalStates.length) q.state = f.states.join(',')
  if (f.repo) q.repo = f.repo
  if (f.q.trim()) q.q = f.q.trim()
  if (f.from) q.from = f.from
  if (f.to) q.to = f.to
  if (f.page > 1) q.page = String(f.page)
  return q
}

export function fromQuery(query: Record<string, unknown>): HistoryFilters {
  const text = (k: string) => (typeof query[k] === 'string' ? (query[k] as string) : '')
  const date = (k: string) => (/^\d{4}-\d{2}-\d{2}$/.test(text(k)) ? text(k) : '')
  const states = text('state').split(',').filter((s) => finalStates.includes(s))
  const page = Number(text('page'))
  return { states: states.length ? states : [...finalStates], repo: text('repo'), q: text('q'), from: date('from'), to: date('to'), page: Number.isInteger(page) && page > 0 ? page : 1 }
}

/** One page of finished jobs, fetched from the server for the current filters. */
export const useHistoryStore = defineStore('history', () => {
  const result = ref<HistoryPage | null>(null)
  const loading = ref(false)
  const error = ref<string | null>(null)
  let latest = 0

  async function load(filters: HistoryFilters): Promise<void> {
    const request = ++latest
    const params = new URLSearchParams({ ...toQuery(filters), state: filters.states.join(','), pageSize: String(pageSize) })
    loading.value = true
    try {
      const page = await get<HistoryPage>(`/api/history?${params}`)
      if (request === latest) {
        result.value = page
        error.value = null
      }
    } catch (err) {
      if (request === latest) error.value = err instanceof ApiError ? err.message : 'Could not load the history.'
    } finally {
      if (request === latest) loading.value = false
    }
  }

  return { result, loading, error, load }
})
