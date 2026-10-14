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
  callouts: [{ kind: 'config', detail: 'Sync:ChunkSize defaults to 5000.' }],
}
const finding = { number: 1, severity: 'breaks', file: 'src/Sync.cs', line: 41, title: 'Unbounded read', detail: null, suggestion: null, decision: 'kept', edited: null }
let session: Record<string, unknown>
let diffText: string
let diffFiles: string[]
let asked: Record<string, unknown>[]
let comments: Record<string, unknown>[]
const json = (body: unknown) => new Response(JSON.stringify(body))
const router = () => createRouter({ history: createMemoryHistory(), routes: [
  { path: '/reviews', name: 'reviews', component: { template: '<div />' } },
  { path: '/reviews/:id', name: 'review', component: { template: '<div />' } },
] })

beforeEach(() => {
  setActivePinia(createPinia())
  setAntiforgeryUrl('/bff/antiforgery')
  window.localStorage.clear()
  asked = []
  comments = []
  diffText = diff
  diffFiles = ['src/Sync.cs', 'docs/sync.md']
  session = { id: 7, repo: 'sysmin', target: 'branch', pullRequestId: null, headRef: 'feature/keyset', baseRef: 'develop', baseCommit: 'a1b2c3d4', headCommit: 'f9e8d7c6',
    status: 'Ready', error: null, model: null, effort: null, summary: 'One real bug.', findings: [finding], createdBy: 'local', sentTo: null, createdAt: '2026-10-09T10:00:00Z',
    chatLink: null, postsFromChat: false, guide }
  vi.stubGlobal('fetch', vi.fn(async (req: Request) => {
    const path = new URL(req.url).pathname
    if (path === '/api/reviews/7/diff') return json({ baseCommit: 'a1b2c3d4', headCommit: 'f9e8d7c6', files: diffFiles, unifiedDiff: diffText, truncated: false })
    if (path === '/api/reviews/7/asks' && req.method === 'POST') {
      asked.push(await req.json() as Record<string, unknown>)
      return json({})
    }
    if (path === '/api/reviews/7/comments' && req.method === 'POST') {
      const body = await req.json() as Record<string, unknown>
      const added = { id: comments.length + 1, file: body.file ?? null, line: body.line ?? null, endLine: null, text: body.text, author: 'local', createdAt: '2026-10-13T08:00:00Z' }
      comments.push(added)
      return json(added)
    }
    if (path.startsWith('/api/reviews/7/comments/') && req.method === 'DELETE') {
      comments = comments.filter((c) => `/api/reviews/7/comments/${c.id}` !== path)
      return new Response(null, { status: 204 })
    }
    if (path === '/api/reviews/7') return json({ session, comments, asks: asked.map((a, i) => ({ id: i + 1, file: a.file ?? null, line: a.line ?? null, endLine: null, question: a.text, answer: null, author: 'local', askedAt: '2026-10-13T07:00:00Z', threadId: null })) })
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
    expect(view.get('[data-testid=guide-callouts]').text()).toContain('Changes a configuration default: Sync:ChunkSize defaults to 5000.')
    expect(view.find('[data-testid=guide-chapter]').exists()).toBe(false)
    const steps = view.get('[aria-label=Chapters]').findAll('button')
    expect(steps.map((b) => b.attributes('title'))).toEqual(['Overview', '1. Keyset paging', '2. Docs'])
    expect(steps[1]!.classes()).toContain('cursor-pointer')
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
    expect(suggestions.text()).toContain('posted only if you keep them')
    await view.findAll('button').find((b) => b.text() === 'All files')!.trigger('click')
    expect(view.find('[data-testid=guided-review]').exists()).toBe(false)
    expect(view.find('#file-docs\\/sync\\.md').exists()).toBe(true)
    view.unmount()
  })

  it('keeping a suggestion makes it your comment (sent, and applied by Fix it); Undo removes it', async () => {
    const view = await open()

    await view.get('[data-testid=suggestions] [data-testid=keep-suggestion]').trigger('click')
    await flushPromises()
    expect(comments).toEqual([expect.objectContaining({ file: 'src/Sync.cs', line: 41,
      text: '✨ Suggestion: Make the chunk size configurable\n\nTables differ.\n\nSuggested change: Read it from options.' })])
    expect(view.get('[data-testid=suggestion-kept]').text()).toContain('Fix it applies it')
    expect(view.find('[data-testid=keep-suggestion]').exists()).toBe(false)

    await view.get('[data-testid=suggestion-kept]').findAll('button').find((b) => b.text() === 'Undo')!.trigger('click')
    await flushPromises()
    expect(comments).toEqual([])
    expect(view.find('[data-testid=keep-suggestion]').exists()).toBe(true)
    view.unmount()
  })

  it('asks for you: explain this chapter, explain this file, why is this finding a problem', async () => {
    const view = await open()
    await view.findAll('button').find((b) => b.text() === 'Start reading')!.trigger('click')

    await view.get('[data-testid=explain-chapter]').trigger('click')
    await flushPromises()
    await view.get('[data-testid=guide-chapter] [data-testid=explain-file]').trigger('click')
    await flushPromises()
    await view.get('[data-testid=guide-chapter] [data-testid=explain-finding]').trigger('click')
    await flushPromises()

    expect(asked).toEqual([
      { text: 'Explain the chapter "Keyset paging": what it does, how its files fit together, and what I should check while reading it.' },
      { file: 'src/Sync.cs', text: 'Explain what changed in this file and why, and how it fits the rest of the change.' },
      { file: 'src/Sync.cs', line: 41, text: 'Why is finding #1 "Unbounded read" a problem? When does it happen, and is the suggested fix right?' },
    ])
    expect(view.findAll('[data-testid=ask]')).toHaveLength(3)
    view.unmount()
  })

  it('a sent review offers no Ask shortcuts', async () => {
    session = { ...session, status: 'Sent' }
    const view = await open()
    await view.findAll('button').find((b) => b.text() === 'Start reading')!.trigger('click')

    expect(view.find('[data-testid=explain-chapter]').exists()).toBe(false)
    expect(view.find('[data-testid=explain-file]').exists()).toBe(false)
    expect(view.find('[data-testid=explain-finding]').exists()).toBe(false)
    view.unmount()
  })

  it('j and k step through the findings on the page', async () => {
    session = { ...session, findings: [finding, { ...finding, number: 2, file: null, line: null, title: 'PR description is empty' }] }
    const view = await open()
    await view.findAll('button').find((b) => b.text() === 'All files')!.trigger('click')
    const cards = view.findAll('[data-finding]')
    expect(cards).toHaveLength(2)

    const key = (k: string) => window.dispatchEvent(new KeyboardEvent('keydown', { key: k }))
    key('j')
    expect(document.activeElement).toBe(cards[0]!.element)
    key('j')
    expect(document.activeElement).toBe(cards[1]!.element)
    key('j')
    expect(document.activeElement, 'stays on the last').toBe(cards[1]!.element)
    key('k')
    expect(document.activeElement).toBe(cards[0]!.element)
    view.unmount()
  })

  it('a chapter is two panes: its files as a tree on the left, the picked file\'s diff on the right', async () => {
    const unchanged = Array.from({ length: 12 }, (_, i) => ` line ${i + 1}\n`).join('')
    diffText = [
      'diff --git a/src/sync/Sync.cs b/src/sync/Sync.cs\n--- a/src/sync/Sync.cs\n+++ b/src/sync/Sync.cs\n@@ -40,14 +40,14 @@\n-var rows = await ReadAllAsync(table);\n+await foreach (var chunk in KeysetAsync(table))\n' + unchanged,
      'diff --git a/src/sync/SyncTests.cs b/src/sync/SyncTests.cs\n--- a/src/sync/SyncTests.cs\n+++ b/src/sync/SyncTests.cs\n@@ -1,1 +1,2 @@\n class SyncTests {\n+  [TestMethod] public void Pages() {}\n',
    ].join('')
    diffFiles = ['src/sync/Sync.cs', 'src/sync/SyncTests.cs']
    session = { ...session, findings: [], guide: { ...guide, notes: [], chapters: [
      { title: 'Keyset paging', why: 'The read loop.', attention: 'core', focus: [], files: [{ path: 'src/sync/Sync.cs', summary: 'Pages by key.' }, { path: 'src/sync/SyncTests.cs', summary: 'Tests the paging.' }] },
    ] } }
    const view = await open()
    await view.findAll('button').find((b) => b.text() === 'Start reading')!.trigger('click')

    const chapter = view.get('[data-testid=guide-chapter]')
    expect(chapter.get('h2').text(), 'the change\'s headline').toBe('Reads the table in keyset chunks instead of all at once.')
    expect(chapter.text()).toContain('1 chapter · from agentd')
    const rows = chapter.findAll('[role=treeitem]').map((r) => r.text().replace(/\s+/g, ' ').trim())
    expect(rows[0], 'one folder row').toMatch(/^src\/sync/)
    expect(chapter.findAll('[data-file]').map((r) => r.attributes('data-file'))).toEqual(['src/sync/Sync.cs', 'src/sync/SyncTests.cs'])
    expect(rows).toHaveLength(3)
    const pane = () => view.get('[data-testid=guide-file]')
    expect(pane().text()).toContain('Pages by key.')
    expect(pane().find('#file-src\\/sync\\/Sync\\.cs').exists()).toBe(true)

    // Long unchanged runs fold; the gap before the hunk says how much is left out.
    expect(pane().get('[data-testid=unmodified-gap]').text()).toBe('39 unmodified lines')
    expect(pane().get('[data-testid=unmodified-fold]').text()).toBe('6 unmodified lines')
    expect(pane().text()).not.toContain('line 6')
    await pane().get('[data-testid=unmodified-fold]').trigger('click')
    expect(pane().text()).toContain('line 6')
    expect(pane().find('[data-testid=unmodified-fold]').exists()).toBe(false)

    await chapter.get('[data-file="src/sync/SyncTests.cs"]').trigger('click')
    expect(pane().text()).toContain('Tests the paging.')
    expect(pane().find('#file-src\\/sync\\/SyncTests\\.cs').exists()).toBe(true)
    expect(pane().find('#file-src\\/sync\\/Sync\\.cs').exists(), 'one file at a time').toBe(false)
    expect(chapter.get('[data-file="src/sync/SyncTests.cs"]').attributes('aria-selected')).toBe('true')
    view.unmount()
  })

  it('the Reviewed checkbox marks the chapter without moving on, and unmarks it', async () => {
    const view = await open()
    await view.findAll('button').find((b) => b.text() === 'Start reading')!.trigger('click')

    await view.get('[data-testid=chapter-reviewed]').setValue(true)
    expect(JSON.parse(window.localStorage.getItem('agentd.guide.7')!)).toEqual([1])
    expect(view.get('[data-testid=guide-chapter]').text(), 'still on chapter 1').toContain('01 / 02')
    await view.get('[data-testid=chapter-reviewed]').setValue(false)
    expect(JSON.parse(window.localStorage.getItem('agentd.guide.7')!)).toEqual([])
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
