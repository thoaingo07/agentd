import { defineStore } from 'pinia'
import { ref } from 'vue'
import { get, send } from '../../shared/api/http'
import type { AgentEvent, Watch, WatchDetail } from '../../shared/api/types'

/** Debounce for re-reading after a burst of watch notices. */
export const watchRefreshDelayMs = 300

/**
 * PRs agentd watches (like `!watch`): the list, one watch's notices, and the 👀 thread's answers (push, discard,
 * unwatch). Kept fresh from the "all" stream's `watch.message` events (ids only).
 */
export const useWatchesStore = defineStore('watches', () => {
  const list = ref<Watch[] | null>(null)
  const opened = ref<WatchDetail | null>(null)
  let timer: ReturnType<typeof setTimeout> | undefined

  async function load(): Promise<void> {
    list.value = await get<Watch[]>('/api/watches')
  }

  /** The active watch on a PR, if any. */
  const of = (repo: string, pullRequestId: number) => list.value?.find((w) => w.repo === repo && w.pullRequestId === pullRequestId)

  /** Watches a PR from here (no chat thread); returns agentd's reply. */
  async function watch(repo: string, pullRequestId: number): Promise<string> {
    const { message } = await send<{ message: string }>('POST', '/api/watches', { repo, pullRequestId })
    await load()
    return message
  }

  async function answer(id: number, action: 'push' | 'discard' | 'unwatch'): Promise<string> {
    const { message } = await send<{ message: string }>('POST', `/api/watches/${id}/${action}`)
    await load()
    if (opened.value?.watch.id === id) await open(id)
    return message
  }

  /** One watch with everything the monitor said (what its 👀 thread shows); null closes it. */
  async function open(id: number | null): Promise<void> {
    opened.value = id === null ? null : await get<WatchDetail>(`/api/watches/${id}`)
  }

  function apply(evt: AgentEvent): void {
    if (evt.type !== 'watch.message' || timer || list.value === null) return
    const id = Number((evt.payload as Record<string, unknown>).watchId)
    timer = setTimeout(() => {
      timer = undefined
      void load().catch(() => {})
      if (opened.value?.watch.id === id) void open(id).catch(() => {})
    }, watchRefreshDelayMs)
  }

  return { list, opened, load, of, watch, answer, open, apply }
})
