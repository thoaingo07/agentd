import { defineStore } from 'pinia'
import { ref } from 'vue'
import { get, send } from '../../shared/api/http'
import type { OpenPullRequests, ReviewComment, ReviewDetail, ReviewDiff, ReviewSent, ReviewSession } from '../../shared/api/types'
import { parseDiff, type DiffFile } from '../../shared/utils/diff'

/** While the reviewer works, the page checks the session this often (docs/architect/review-sessions.md §2). */
export const reviewPollMs = 3000

/** The Reviews page re-reads the open PRs this often while it's shown (the server keeps each list a minute). */
export const openPrsPollMs = 60_000

/** What to review: a PR, a branch (against its base or a chosen one), or two commits. */
export interface StartReview { repo: string; pullRequestId?: number | null; branch?: string | null; base?: string | null; head?: string | null }

/** Review sessions: my recent ones, and the one open on the page (its detail and pinned diff). */
export const useReviewsStore = defineStore('reviews', () => {
  const mine = ref<ReviewSession[]>([])
  const current = ref<ReviewDetail | null>(null)
  const diff = ref<ReviewDiff | null>(null)
  const files = ref<DiffFile[]>([])
  let timer: ReturnType<typeof setTimeout> | undefined
  const openPrs = ref<OpenPullRequests | null>(null)
  const openPrsError = ref<string | null>(null)
  let openPrsTimer: ReturnType<typeof setInterval> | undefined

  async function loadOpenPrs(): Promise<void> {
    try {
      openPrs.value = await get<OpenPullRequests>('/api/reviews/pull-requests')
      openPrsError.value = null
    } catch {
      openPrsError.value = 'Couldn\'t load the open pull requests.'
    }
  }

  /** Loads the open PRs now and keeps them fresh until stopOpenPrs (skipping while the tab is hidden). */
  function watchOpenPrs(): void {
    stopOpenPrs()
    void loadOpenPrs()
    openPrsTimer = setInterval(() => {
      if (document.visibilityState !== 'hidden') void loadOpenPrs()
    }, openPrsPollMs)
  }

  function stopOpenPrs(): void {
    clearInterval(openPrsTimer)
    openPrsTimer = undefined
  }

  async function loadMine(): Promise<void> {
    mine.value = await get<ReviewSession[]>('/api/reviews')
  }

  function start(input: StartReview): Promise<ReviewSession> {
    return send<ReviewSession>('POST', '/api/reviews', input)
  }

  /** Opens a session: its detail and diff, then re-reads it every few seconds until the reviewer is done. */
  async function open(id: number): Promise<void> {
    close()
    const [detail, change] = await Promise.all([get<ReviewDetail>(`/api/reviews/${id}`), get<ReviewDiff>(`/api/reviews/${id}/diff`)])
    current.value = detail
    diff.value = change
    files.value = change.unifiedDiff ? parseDiff(change.unifiedDiff) : []
    poll(id)
  }

  /** Keep checking while the reviewer works or a question waits for its answer. */
  function poll(id: number): void {
    const waiting = current.value?.session.status === 'Reviewing' || current.value?.session.sentTo === 'fix:running' || current.value?.asks.some((a) => a.answer == null)
    if (!waiting) return
    timer = setTimeout(() => {
      void get<ReviewDetail>(`/api/reviews/${id}`).then((d) => {
        if (current.value?.session.id !== id) return
        current.value = d
        poll(id)
      }).catch(() => poll(id))
    }, reviewPollMs)
  }

  function close(): void {
    clearTimeout(timer)
    current.value = null
    diff.value = null
    files.value = []
  }

  async function refresh(): Promise<void> {
    if (current.value) current.value = await get<ReviewDetail>(`/api/reviews/${current.value.session.id}`)
  }

  /** Keep, drop, or edit (with text) finding <paramref name="n"/> (1-based). */
  async function decide(n: number, decision: 'kept' | 'dropped' | 'edited', text?: string): Promise<void> {
    if (!current.value) return
    await send<void>('PUT', `/api/reviews/${current.value.session.id}/findings/${n}`, { decision, text: text ?? null })
    await refresh()
  }

  async function comment(input: { file?: string | null; line?: number | null; endLine?: number | null; text: string }): Promise<ReviewComment | null> {
    if (!current.value) return null
    const added = await send<ReviewComment>('POST', `/api/reviews/${current.value.session.id}/comments`, input)
    await refresh()
    return added
  }

  /** A question about the selection (or the whole change); the answer arrives later, so this starts polling. */
  async function ask(input: { file?: string | null; line?: number | null; endLine?: number | null; text: string }): Promise<void> {
    if (!current.value) return
    const id = current.value.session.id
    await send<unknown>('POST', `/api/reviews/${id}/asks`, input)
    await refresh()
    clearTimeout(timer)
    poll(id)
  }

  /** A follow-up in a question's thread (the agent keeps the conversation); refused while the last one waits. */
  async function followUp(threadId: number, text: string): Promise<void> {
    if (!current.value) return
    const id = current.value.session.id
    await send<unknown>('POST', `/api/reviews/${id}/asks/${threadId}/follow-ups`, { text })
    await refresh()
    clearTimeout(timer)
    poll(id)
  }

  /** Post a PR's review to the PR, or get it as text to copy. */
  async function sendTo(destination: 'pr' | 'text' | 'fix'): Promise<ReviewSent | null> {
    if (!current.value) return null
    const id = current.value.session.id
    const sent = await send<ReviewSent>('POST', `/api/reviews/${id}/send`, { destination })
    await refresh()
    clearTimeout(timer)
    poll(id)   // Fix it runs in the background
    return sent
  }

  async function removeComment(id: number): Promise<void> {
    if (!current.value) return
    await send<void>('DELETE', `/api/reviews/${current.value.session.id}/comments/${id}`)
    await refresh()
  }

  return { mine, current, diff, files, openPrs, openPrsError, loadMine, loadOpenPrs, watchOpenPrs, stopOpenPrs, start, open, close, decide, comment, ask, followUp, removeComment, sendTo }
})
