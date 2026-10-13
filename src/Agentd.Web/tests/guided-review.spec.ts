import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { setAntiforgeryUrl } from '../ClientApps/shared/api/http'
import ReviewView from '../ClientApps/dashboard/views/ReviewView.vue'

// The guided review (review-sessions.md §2a): overview first, then one chapter at a time with its own files and notes.
const diff = [
  'diff --git a/src/Sync.cs b/src/Sync.cs\n--- a/src/Sync.cs\n+++ b/src/Sync.cs\n@@ -40,2 +40,2 @@\n var page = 0;\n-var rows = await ReadAllAsync(table);\n+await foreach (var chunk in KeysetAsync(table))\n',
  'diff --git a/docs/sync.md b/docs/sync.md\n--- a/docs/sync.md\n+++ b/docs/sync.md\n@@ -1,1 +1,1 @@\n-old\n+new\n',
].join('')
const guide = {
  overview: 'Reads the table in keyset chunks instead of all at once.',
  before: 'The whole table was read into memory.',
  after: 'It reads 5000 rows at a time.',
  chapters: [
    { title: 'Keyset paging', why: 'The heart: the read loop.', attention: 'core', focus: ['the cursor moves forward'], files: [{ path: 'src/Sync.cs', summary: 'Pages by key.' }] },
    { title: 'Docs', why: null, attention: 'skim', focus: [], files: [{ path: 'docs/sync.md', summary: null }] },
  ],
  notes: [{ file: 'src/Sync.cs', line: 41, text: 'The cursor is the last key read.', critical: true }],
  suggestions: [{ file: 'src/Sync.cs', line: 41, title: 'Make the chunk size configurable', detail: 'Tables differ.', suggestion: 'Read it from options.' }],
}
const finding = { number: 1, severity: 'breaks', file: 'src/Sync.cs', line: 41, title: 'Unbounded read', detail: null, suggestion: null, decision: 'kept', edited: null }
let session: Record<string, unknown>
const json = (body: unknown) => new Response(JSON.stringify(body))
const router = () => createRouter({ history: createMemoryHistory(), routes: [
  { path: '/reviews', name: 'reviews', component: { template: '<div />' } },
  { path: '/reviews/:id', name: 'review', component: { template: '<div />' } },
] })

beforeEach(() => {
  setActivePinia(createPinia())
  setAntiforgeryUrl('/bff/antiforgery')
  window.localStorage.clear()
  session = { id: 7, repo: 'sysmin', target: 'branch', pullRequestId: null, headRef: 'feature/keyset', baseRef: 'develop', baseCommit: 'a1b2c3d4', headCommit: 'f9e8d7c6',
    status: 'Ready', error: null, model: null, effort: null, summary: 'One real bug.', findings: [finding], createdBy: 'local', sentTo: null, createdAt: '2026-10-09T10:00:00Z',
    chatLink: null, postsFromChat: false, guide }
  vi.stubGlobal('fetch', vi.fn(async (req: Request) => {
    const path = new URL(req.url).pathname
    if (path === '/api/reviews/7/diff') return json({ baseCommit: 'a1b2c3d4', headCommit: 'f9e8d7c6', files: ['src/Sync.cs', 'docs/sync.md'], unifiedDiff: diff, truncated: false })
    if (path === '/api/reviews/7') return json({ session, comments: [], asks: [] })
    return json({ token: 't' })
  }))
})
afterEach(() => vi.unstubAllGlobals())

async function open() {
  const view = mount(ReviewView, { props: { id: 7 }, global: { plugins: [router()] }, attachTo: document.body })
  await flushPromises()
  return view
}

describe('guided review', () => {
  it('opens on the overview: what it does, before and after, and the chapters core first', async () => {
    const view = await open()

    const overview = view.get('[data-testid=guide-overview]')
    expect(overview.text()).toContain('Reads the table in keyset chunks')
    expect(overview.text()).toContain('Before')
    expect(overview.text()).toContain('It reads 5000 rows at a time.')
    const cards = view.findAll('[data-testid=guide-chapter-card]').map((c) => c.text())
    expect(cards[0]).toContain('Keyset paging')
    expect(cards[0]).toContain('Core')
    expect(cards[0]).toContain('1 finding')
    expect(cards[1]).toContain('Skim')
    expect(view.find('[data-testid=guide-chapter]').exists()).toBe(false)
    view.unmount()
  })

  it('shows one chapter at a time: its story, its own files with notes and findings, and the way on', async () => {
    const view = await open()
    await view.findAll('button').find((b) => b.text() === 'Start reading')!.trigger('click')

    const chapter = view.get('[data-testid=guide-chapter]')
    expect(view.text()).toContain('While reading, check')
    expect(view.text()).toContain('the cursor moves forward')
    expect(view.text()).toContain('1 spot needs care')
    expect(chapter.text()).toContain('Pages by key.')
    expect(chapter.find('#file-src\\/Sync\\.cs').exists()).toBe(true)
    expect(chapter.find('#file-docs\\/sync\\.md').exists()).toBe(false)
    expect(chapter.get('[data-testid=guide-note]').text()).toContain('The cursor is the last key read.')
    expect(chapter.text()).toContain('Unbounded read')

    await chapter.findAll('button').find((b) => b.text() === 'Mark reviewed and continue')!.trigger('click')
    expect(view.get('[data-testid=guide-chapter]').find('#file-docs\\/sync\\.md').exists()).toBe(true)
    expect(JSON.parse(window.localStorage.getItem('agentd.guide.7')!)).toEqual([1])

    window.dispatchEvent(new KeyboardEvent('keydown', { key: '[' }))
    await flushPromises()
    expect(view.get('[data-testid=guide-chapter]').find('#file-src\\/Sync\\.cs').exists()).toBe(true)
    window.dispatchEvent(new KeyboardEvent('keydown', { key: '[' }))
    await flushPromises()
    expect(view.get('[data-testid=guide-overview]').text()).toContain('✓')
    expect(view.findAll('button').some((b) => b.text() === 'Continue')).toBe(true)
    view.unmount()
  })

  it('lists suggestions apart from findings, and All files shows the plain list', async () => {
    const view = await open()

    const suggestions = view.get('[data-testid=suggestions]')
    expect(suggestions.text()).toContain('Make the chunk size configurable')
    expect(suggestions.text()).toContain("they aren't posted")
    await view.findAll('button').find((b) => b.text() === 'All files')!.trigger('click')
    expect(view.find('[data-testid=guided-review]').exists()).toBe(false)
    expect(view.find('#file-docs\\/sync\\.md').exists()).toBe(true)
    view.unmount()
  })

  it('a review without a guide has no Guided view', async () => {
    session = { ...session, guide: null }
    const view = await open()

    expect(view.find('[data-testid=guided-review]').exists()).toBe(false)
    expect(view.findAll('button').some((b) => b.text() === 'Guided')).toBe(false)
    view.unmount()
  })
})
