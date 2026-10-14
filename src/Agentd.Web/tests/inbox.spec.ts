import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { setAntiforgeryUrl } from '../ClientApps/shared/api/http'
import InboxView from '../ClientApps/dashboard/views/InboxView.vue'
import { inboxRefreshDelayMs, useInboxStore } from '../ClientApps/dashboard/stores/inbox'

// The Inbox (docs/architect/inbox.md): threads waiting on you first, a thread opened is read, answers go to the source.
const question = { id: 12, source: 'idea', sourceId: 4, title: '💡 Dark mode', recipient: 'local', kind: 'Question', markdown: 'Create these?',
  options: [{ id: 'idea-create', label: '✅ Create' }, { id: 'idea-discard', label: '🗑 Discard' }], createdAt: '2026-10-14T10:00:00Z' }
const thread = (source: string, sourceId: number, over: Record<string, unknown> = {}) => ({
  source, sourceId, title: `${source} ${sourceId}`, recipient: 'local', latestId: sourceId * 10, latestKind: 'Info', latestPreview: '**Done**: PR !17 is ready.',
  latestAt: '2026-10-14T10:00:00Z', items: 3, unread: 0, waiting: false, question: null, ...over,
})
let posts: { path: string; body: unknown }[]
let asked: string[]

const router = () => createRouter({ history: createMemoryHistory(), routes: [
  { path: '/inbox', name: 'inbox', component: InboxView },
  { path: '/inbox/:source/:id', name: 'inbox-thread', component: InboxView, props: (r) => ({ source: String(r.params.source), id: Number(r.params.id) }) },
  { path: '/jobs/:id', name: 'job', component: { template: '<div />' } },
  { path: '/ideas/:id', name: 'idea', component: { template: '<div />' } },
  { path: '/chats/:id', name: 'chat', component: { template: '<div />' } },
  { path: '/reviews', name: 'reviews', component: { template: '<div />' } },
  { path: '/incidents/:id', name: 'incident', component: { template: '<div />' } },
] })

beforeEach(() => {
  setActivePinia(createPinia())
  setAntiforgeryUrl('/bff/antiforgery')
  window.localStorage.clear()
  posts = []
  asked = []
  vi.stubGlobal('fetch', vi.fn(async (req: Request) => {
    const url = new URL(req.url)
    asked.push(url.pathname + url.search)
    if (req.method === 'POST' && url.pathname !== '/bff/antiforgery') {
      posts.push({ path: url.pathname, body: JSON.parse(await req.text()) })
      return url.pathname.endsWith('/reply') ? new Response(JSON.stringify({ outcome: 'sent' }), { status: 202 }) : new Response(null, { status: 204 })
    }
    if (url.pathname === '/api/inbox') {
      return new Response(JSON.stringify({ counts: { waiting: 1, unread: 2 }, threads: [
        thread('idea', 4, { waiting: true, unread: 2, latestKind: 'Question', latestPreview: 'Create these?', question }),
        thread('job', 7, { unread: 1, title: 'WI-5666 Upgrade Angular' }),
        thread('chat', 2),
      ] }))
    }
    if (url.pathname === '/api/inbox/counts') return new Response(JSON.stringify({ waiting: 0, unread: 1 }))
    if (url.pathname === '/api/inbox/idea/4') return new Response(JSON.stringify([question, { ...question, id: 11, kind: 'Info', markdown: 'Here are **3** work items.', options: [] }]))
    return new Response(JSON.stringify({ token: 't' }))
  }))
})
afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

async function open(path = '/inbox') {
  const r = router()
  await r.push(path)
  const w = mount({ template: '<RouterView />' }, { global: { plugins: [r] }, attachTo: document.body })
  await flushPromises()
  return { w, r }
}

describe('Inbox', () => {
  it('groups threads: waiting on you, new, earlier', async () => {
    const { w } = await open()

    const groups = w.findAll('[role=group][data-section]')
    expect(groups.map((g) => g.attributes('data-section'))).toEqual(['waiting', 'new', 'earlier'])
    expect(groups[0]!.text()).toContain('idea 4')
    expect(groups[0]!.text()).toContain('❓ Create these?')
    expect(groups[0]!.get('.ag-count.is-urgent').text()).toBe('2')
    expect(groups[1]!.text()).toContain('WI-5666 Upgrade Angular')
    expect(groups[2]!.text()).toContain('Done: PR !17 is ready.')
    expect(w.text()).toContain('1 waiting on you · 2 unread')
    w.unmount()
  })

  it('opening a thread shows its messages oldest first, marks it read, and its buttons answer the source', async () => {
    const { w } = await open('/inbox/idea/4')

    const messages = w.findAll('[data-testid=inbox-message]')
    expect(messages.map((m) => m.text())).toEqual([expect.stringContaining('Here are 3 work items.'), expect.stringContaining('Create these?')])
    expect(messages[1]!.classes()).toContain('is-question')
    expect(posts[0]).toEqual({ path: '/api/inbox/idea/4/read', body: { until: 12 } })
    expect(w.get('[data-testid=inbox-open] a').attributes('href')).toBe('/ideas/4')
    expect(w.get('[data-section=waiting]').text(), 'read, but open: it keeps its place').toContain('idea 4')

    await w.get('[aria-label=Answer]').findAll('button')[0]!.trigger('click')
    await flushPromises()
    expect(posts.at(-1)).toEqual({ path: '/api/inbox/idea/4/reply', body: { text: '✅ Create' } })
    expect(w.get('[role=status]').text()).toBe('Sent.')

    await w.get('textarea[aria-label=Reply]').setValue(' only the portal pages ')
    await w.get('[data-testid=inbox-open] form').trigger('submit')
    await flushPromises()
    expect(posts.at(-1)).toEqual({ path: '/api/inbox/idea/4/reply', body: { text: 'only the portal pages' } })
    w.unmount()
  })

  it('Mine asks only for threads addressed to you; j moves to the next thread', async () => {
    const { w, r } = await open()

    await w.findAll('[role=radio]').find((b) => b.text() === 'Mine')!.trigger('click')
    await flushPromises()
    expect(asked).toContain('/api/inbox?view=mine')

    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'j' }))
    await flushPromises()
    expect(r.currentRoute.value.fullPath).toBe('/inbox/idea/4')
    w.unmount()
  })

  it("an incident's thread offers Fix it and Ignore, answers its source, and opens its Incidents page", async () => {
    const analysis = { id: 30, source: 'incident', sourceId: 5, title: '🚨 orders-api: NullReferenceException', recipient: null, kind: 'Question',
      markdown: '🔎 **Investigation of incident #5**', options: [{ id: 'incident-fix', label: '🛠 Fix it' }, { id: 'incident-ignore', label: '🙈 Ignore' }],
      createdAt: '2026-10-14T10:00:00Z' }
    const fetch = vi.mocked(globalThis.fetch).getMockImplementation()!
    vi.stubGlobal('fetch', vi.fn(async (req: Request) => {
      const url = new URL(req.url)
      if (url.pathname === '/api/inbox' && req.method === 'GET') {
        return new Response(JSON.stringify({ counts: { waiting: 1, unread: 1 }, threads: [
          thread('incident', 5, { title: analysis.title, waiting: true, unread: 1, latestKind: 'Question', latestPreview: 'Investigation', question: analysis }),
        ] }))
      }
      if (url.pathname === '/api/inbox/incident/5') return new Response(JSON.stringify([analysis]))
      return fetch(req)
    }))

    const { w } = await open('/inbox/incident/5')
    expect(w.get('[data-testid=inbox-open] h2').text()).toBe('🚨 orders-api: NullReferenceException')
    expect(w.get('[data-testid=inbox-open] a').attributes('href')).toBe('/incidents/5')
    expect(w.get('[aria-label=Answer]').findAll('button').map((b) => b.text())).toEqual(['🛠 Fix it', '🙈 Ignore'])

    await w.get('[aria-label=Answer]').findAll('button')[0]!.trigger('click')
    await flushPromises()
    expect(posts.at(-1)).toEqual({ path: '/api/inbox/incident/5/reply', body: { text: '🛠 Fix it' } })
    w.unmount()
  })

  it('inbox and job events re-read the counts once per burst', async () => {
    vi.useFakeTimers()
    const inbox = useInboxStore()
    for (const type of ['inbox.added', 'job.state', 'inbox.added']) inbox.apply({ seq: 1, jobId: null, ts: '2026-10-14T10:00:00Z', type, payload: {} })
    inbox.apply({ seq: 2, jobId: null, ts: '2026-10-14T10:00:00Z', type: 'chat.message', payload: {} })

    await vi.advanceTimersByTimeAsync(inboxRefreshDelayMs + 1)
    expect(asked.filter((a) => a === '/api/inbox/counts')).toHaveLength(1)
    expect(inbox.counts).toEqual({ waiting: 0, unread: 1 })
  })
})
