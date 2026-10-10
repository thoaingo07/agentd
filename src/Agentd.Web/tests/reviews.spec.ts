import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { setAntiforgeryUrl } from '../ClientApps/shared/api/http'
import ReviewView from '../ClientApps/dashboard/views/ReviewView.vue'
import ReviewsView from '../ClientApps/dashboard/views/ReviewsView.vue'
import { openPrsPollMs, reviewPollMs } from '../ClientApps/dashboard/stores/reviews'

type Call = { method: string; path: string; body: unknown }
let calls: Call[]
let session: Record<string, unknown>
let comments: object[]
let asks: Record<string, unknown>[]
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status })
const diff = 'diff --git a/src/Sync.cs b/src/Sync.cs\n--- a/src/Sync.cs\n+++ b/src/Sync.cs\n@@ -40,2 +40,2 @@\n var page = 0;\n-var rows = await ReadAllAsync(table);\n+await foreach (var chunk in KeysetAsync(table))\n'
const finding = { number: 1, severity: 'breaks', file: 'src/Sync.cs', line: 41, title: 'Unbounded read', detail: 'Large tables load at once.', suggestion: 'Page by key.', decision: 'kept', edited: null }
const router = () => createRouter({ history: createMemoryHistory(), routes: [
  { path: '/reviews', name: 'reviews', component: { template: '<div />' } },
  { path: '/reviews/:id', name: 'review', component: { template: '<div />' } },
] })

beforeEach(() => {
  setActivePinia(createPinia())
  setAntiforgeryUrl('/bff/antiforgery')
  calls = []
  comments = []
  asks = []
  session = { id: 7, repo: 'sysmin', target: 'branch', pullRequestId: null, headRef: 'feature/keyset', baseRef: 'develop', baseCommit: 'a1b2c3d4', headCommit: 'f9e8d7c6',
    status: 'Ready', error: null, model: 'claude-opus-5-5', effort: 'high', summary: 'One real bug.', findings: [finding], createdBy: 'local', sentTo: null, createdAt: '2026-10-09T10:00:00Z' }
  vi.stubGlobal('fetch', vi.fn(async (req: Request) => {
    const path = new URL(req.url).pathname
    const body = req.method === 'GET' ? undefined : await req.text().then((t) => (t ? JSON.parse(t) : undefined))
    calls.push({ method: req.method, path, body })
    if (path === '/bff/antiforgery') return json({ token: 't' })
    if (path === '/api/config') return json({ repositories: [{ name: 'sysmin', organization: 'o', project: 'p' }] })
    if (path === '/api/reviews/pull-requests') {
      const n = calls.filter((c) => c.path === path).length
      return json({ fetchedAt: '2026-10-10T10:00:00Z', failed: n > 1 ? [] : [{ repo: 'docs', reason: 'Azure DevOps answered 401.' }], items: [
        { repo: 'sysmin', id: 3944, title: 'Keyset chunks', author: 'Bob', sourceBranch: 'feature/keyset', targetBranch: 'develop', isDraft: true, createdAt: '2026-10-09T10:00:00Z', url: 'https://dev.azure.com/o/p/_git/sysmin/pullrequest/3944' },
        ...(n > 1 ? [{ repo: 'sysmin', id: 3950, title: 'New one', author: 'Ann', sourceBranch: 'feature/new', targetBranch: 'develop', isDraft: false, createdAt: '2026-10-10T09:00:00Z', url: 'https://dev.azure.com/o/p/_git/sysmin/pullrequest/3950' }] : []),
      ] })
    }
    if (path === '/api/reviews' && req.method === 'GET') return json([session])
    if (path === '/api/reviews' && req.method === 'POST') return json({ ...session, id: 9 }, 201)
    if (path === '/api/reviews/7/diff') return json({ baseCommit: 'a1b2c3d4', headCommit: 'f9e8d7c6', files: ['src/Sync.cs'], unifiedDiff: diff, truncated: false })
    if (path === '/api/reviews/7') return json({ session, comments, asks })
    if (path === '/api/reviews/7/send') {
      const destination = (body as { destination: string }).destination
      session = { ...session, status: 'Sent', sentTo: destination === 'pr' ? 'pr:3944' : destination === 'fix' ? 'fix:running' : 'text' }
      return json(destination === 'pr'
        ? { destination, text: null, url: 'https://dev.azure.com/o/p/_git/sysmin/pullrequest/3944', posted: 3, asPerson: true }
        : { destination, text: '# Review findings (agentd review)\n1. 🔴 **Unbounded read**', url: null, posted: 0, asPerson: false })
    }
    if (path === '/api/reviews/7/asks') {
      const a = { id: 5, author: 'local', askedAt: '2026-10-09T10:02:00Z', endLine: null, answer: null, question: (body as { text: string }).text, file: (body as { file?: string }).file ?? null, line: (body as { line?: number }).line ?? null }
      asks = [...asks, a]
      return json(a, 202)
    }
    if (path.startsWith('/api/reviews/7/findings/')) {
      session = { ...session, findings: [{ ...finding, decision: (body as { decision: string }).decision, edited: (body as { text: string | null }).text }] }
      return new Response(null, { status: 204 })
    }
    if (path === '/api/reviews/7/comments') {
      const c = { id: 1, author: 'local', createdAt: '2026-10-09T10:01:00Z', endLine: null, ...(body as object) }
      comments = [...comments, c]
      return json(c, 201)
    }
    return json({ title: 'Not found' }, 404)
  }))
})
afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

describe('reviews', () => {
  it('starts a branch review against a chosen base and opens it', async () => {
    const r = router()
    const view = mount(ReviewsView, { global: { plugins: [r] } })
    await flushPromises()
    await view.get('input[placeholder="feature/keyset-chunks"]').setValue(' feature/keyset ')
    await view.get('input[placeholder="its base branch"]').setValue('release/1.2')
    await view.get('form').trigger('submit')
    await flushPromises()

    expect(calls.find((c) => c.method === 'POST')?.body).toEqual({ repo: 'sysmin', pullRequestId: null, branch: 'feature/keyset', base: 'release/1.2', head: null })
    expect(r.currentRoute.value.fullPath).toBe('/reviews/9')
    expect(view.text()).toContain('feature/keyset')
  })

  it('lists the open pull requests, keeps them fresh, and reviews one', async () => {
    vi.useFakeTimers()
    const r = router()
    const view = mount(ReviewsView, { global: { plugins: [r] } })
    await flushPromises()

    const rows = view.findAll('[data-testid=open-pr]')
    expect(rows).toHaveLength(1)
    expect(rows[0]!.text()).toContain('!3944')
    expect(rows[0]!.text()).toContain('Keyset chunks')
    expect(rows[0]!.text()).toContain('draft')
    expect(view.get('[data-testid=open-prs]').text()).toContain('docs: Azure DevOps answered 401.')

    await vi.advanceTimersByTimeAsync(openPrsPollMs)
    await flushPromises()
    expect(view.findAll('[data-testid=open-pr]')).toHaveLength(2)
    expect(view.get('[data-testid=open-prs]').text()).not.toContain('docs:')

    await view.findAll('[data-testid=open-pr]')[0]!.findAll('button').find((b) => b.text() === 'Review')!.trigger('click')
    await flushPromises()
    expect(calls.find((c) => c.method === 'POST')?.body).toEqual({ repo: 'sysmin', pullRequestId: 3944 })
    expect(r.currentRoute.value.fullPath).toBe('/reviews/9')

    view.unmount()
    const before = calls.filter((c) => c.path === '/api/reviews/pull-requests').length
    await vi.advanceTimersByTimeAsync(openPrsPollMs * 2)
    expect(calls.filter((c) => c.path === '/api/reviews/pull-requests')).toHaveLength(before)
  })

  it('shows findings at their line in the diff and keeps, edits and drops them', async () => {
    const view = mount(ReviewView, { props: { id: 7 }, global: { plugins: [router()] } })
    await flushPromises()

    expect(view.get('[data-testid=review-summary]').text()).toContain('🔴 1 🟠 0')
    const card = view.get('section#file-src\\/Sync\\.cs article')
    expect(card.text()).toContain('#1 Unbounded read')
    expect(card.text()).toContain('Fix: Page by key.')

    await card.findAll('button').find((b) => b.text() === 'Drop')!.trigger('click')
    await flushPromises()
    expect(calls.find((c) => c.method === 'PUT')).toEqual({ method: 'PUT', path: '/api/reviews/7/findings/1', body: { decision: 'dropped', text: null } })
    expect(view.get('article').attributes('data-decision')).toBe('dropped')

    await view.get('article').findAll('button').find((b) => b.text() === 'Edit')!.trigger('click')
    await view.get('textarea[aria-label="Your text for this finding"]').setValue('Page it by the primary key.')
    await view.get('article').findAll('button').find((b) => b.text() === 'Save')!.trigger('click')
    await flushPromises()
    expect(view.get('article').text()).toContain('Page it by the primary key.')
  })

  it('comments on a line from its number', async () => {
    const view = mount(ReviewView, { props: { id: 7 }, global: { plugins: [router()] } })
    await flushPromises()

    await view.get('button[aria-label="Comment on line 40"]').trigger('click')
    await view.get('textarea[aria-label="Your comment or question on line 40"]').setValue('make it config')
    await view.findAll('button').find((b) => b.text() === 'Comment')!.trigger('click')
    await flushPromises()

    expect(calls.find((c) => c.path === '/api/reviews/7/comments')?.body).toEqual({ file: 'src/Sync.cs', line: 40, text: 'make it config' })
    expect(view.get('[data-testid=line-comment]').text()).toContain('make it config')
  })

  it('says reviewing until the findings arrive', async () => {
    vi.useFakeTimers()
    session = { ...session, status: 'Reviewing', findings: [], summary: null }
    const view = mount(ReviewView, { props: { id: 7 }, global: { plugins: [router()] } })
    await flushPromises()
    expect(view.get('[role=status]').text()).toContain('Reviewing…')

    session = { ...session, status: 'Ready', findings: [finding], summary: 'One real bug.' }
    await vi.advanceTimersByTimeAsync(reviewPollMs + 1)
    await flushPromises()

    expect(view.text()).toContain('#1 Unbounded read')
    expect(view.find('[role=status]').exists()).toBe(false)
  })

  it('asks about a line and shows the answer when it arrives', async () => {
    vi.useFakeTimers()
    const view = mount(ReviewView, { props: { id: 7 }, global: { plugins: [router()] } })
    await flushPromises()

    await view.get('button[aria-label="Comment on line 41"]').trigger('click')
    await view.get('textarea[aria-label="Your comment or question on line 41"]').setValue('why a loop here?')
    await view.findAll('button').find((b) => b.text() === 'Ask')!.trigger('click')
    await flushPromises()
    expect(calls.find((c) => c.path === '/api/reviews/7/asks')?.body).toEqual({ file: 'src/Sync.cs', line: 41, text: 'why a loop here?' })
    expect(view.get('[data-testid=ask]').text()).toContain('thinking…')

    asks = [{ ...asks[0], answer: 'It pages by **key**.' }]
    await vi.advanceTimersByTimeAsync(reviewPollMs + 1)
    await flushPromises()
    const answer = view.get('[data-testid=ask]')
    expect(answer.text()).toContain('src/Sync.cs:41 why a loop here?')
    expect(answer.find('strong').text()).toBe('key')
  })

  it('posts a PR review to the PR under your name', async () => {
    session = { ...session, target: 'pr', pullRequestId: 3944 }
    const view = mount(ReviewView, { props: { id: 7 }, global: { plugins: [router()] } })
    await flushPromises()

    await view.get('[data-testid=send]').findAll('button').find((b) => b.text() === 'Post to the PR')!.trigger('click')
    await flushPromises()

    expect(calls.find((c) => c.path === '/api/reviews/7/send')?.body).toEqual({ destination: 'pr' })
    expect(view.get('[role=status]').text()).toContain('3 thread(s), under your name')
    expect(view.get('a[href="https://dev.azure.com/o/p/_git/sysmin/pullrequest/3944"]').text()).toBe('Open it')
    expect(view.find('[data-testid=send]').exists()).toBe(false)
  })

  it('copies a branch review as text, and the laptop page has no server Send', async () => {
    const view = mount(ReviewView, { props: { id: 7 }, global: { plugins: [router()] } })
    await flushPromises()
    expect(view.get('[data-testid=send]').text()).not.toContain('Post to the PR')

    await view.get('[data-testid=send]').findAll('button').find((b) => b.text() === 'Copy as text')!.trigger('click')
    await flushPromises()
    expect((view.get('textarea[aria-label="The review as text"]').element as HTMLTextAreaElement).value).toContain('Unbounded read')

    const local = mount(ReviewView, { props: { id: 7, local: true }, global: { plugins: [router()] } })
    await flushPromises()
    expect(local.find('[data-testid=send]').exists()).toBe(false)
  })

  it('fixes on the branch after you confirm, and links round 2 when it is pushed', async () => {
    vi.useFakeTimers()
    vi.stubGlobal('confirm', vi.fn(() => true))
    const view = mount(ReviewView, { props: { id: 7 }, global: { plugins: [router()] } })
    await flushPromises()

    await view.get('[data-testid=send]').findAll('button').find((b) => b.text() === 'Fix it (push to feature/keyset)')!.trigger('click')
    await flushPromises()
    expect(calls.find((c) => c.path === '/api/reviews/7/send')?.body).toEqual({ destination: 'fix' })
    expect(view.get('[data-testid=fix]').text()).toContain('Fixing on feature/keyset')

    session = { ...session, sentTo: 'fix:9:c0ffee1' }
    await vi.advanceTimersByTimeAsync(reviewPollMs + 1)
    await flushPromises()
    expect(view.get('[data-testid=fix]').text()).toContain('Pushed c0ffee1 to feature/keyset')
    expect(view.get('[data-testid=fix] a').text()).toBe('Round 2: review #9')
  })
})
