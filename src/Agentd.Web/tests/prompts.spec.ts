import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import PromptsView from '../ClientApps/dashboard/views/PromptsView.vue'
import PromptView from '../ClientApps/dashboard/views/PromptView.vue'
import PromptsLink from '../ClientApps/dashboard/components/prompts/PromptsLink.vue'
import { useSessionStore } from '../ClientApps/dashboard/stores/session'

// Settings → Prompts (prompt-log.md "Reading it"): the list with filters in the address, and one prompt whole.
const blank = { template: '<div />' }
const router = () => createRouter({ history: createMemoryHistory(), routes: [
  { path: '/', component: blank },
  { path: '/jobs/:id', name: 'job', component: blank },
  { path: '/ideas/:id', name: 'idea', component: blank },
  { path: '/chats/:id', name: 'chat', component: blank },
  { path: '/reviews/:id', name: 'review', component: blank },
  { path: '/settings/prompts', name: 'prompts', component: PromptsView },
  { path: '/settings/prompts/:id', name: 'prompt', component: blank },
] })
const summary = (id: number, over: Record<string, unknown> = {}) => ({
  id, source: 'job', sourceId: 42, step: 'implement', session: null, resume: false, model: 'claude-opus-5-5', effort: 'high', profile: null,
  outcome: 'ok', error: null, startedAt: '2026-10-13T10:00:00Z', finishedAt: '2026-10-13T10:02:05Z', preview: `prompt ${id}`,
  promptChars: 2400, replyChars: 310, hasRules: true, ...over,
})
let asked: URLSearchParams[]
let detail: unknown

beforeEach(() => {
  setActivePinia(createPinia())
  asked = []
  detail = null
  vi.stubGlobal('fetch', vi.fn(async (req: Request) => {
    const url = new URL(req.url)
    if (url.pathname.startsWith('/api/prompts/')) {
      return detail ? new Response(JSON.stringify(detail)) : new Response(JSON.stringify({ title: 'Not found' }), { status: 404 })
    }
    asked.push(url.searchParams)
    return new Response(JSON.stringify(url.searchParams.has('before')
      ? { prompts: [summary(1)], hasMore: false }
      : { prompts: [summary(3, { outcome: null, finishedAt: null }), summary(2, { source: 'review-ask', sourceId: 9, step: null, outcome: 'usage-limit', error: 'Usage limit until 15:00' })], hasMore: true }))
  }))
})
afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

async function list(path = '/settings/prompts') {
  const r = router()
  await r.push(path)
  const w = mount({ template: '<RouterView />' }, { global: { plugins: [r] } })
  await flushPromises()
  return { w, r }
}

describe('Prompts', () => {
  it('lists prompts newest first with their source, step, model and outcome, and loads older ones', async () => {
    const { w } = await list()

    const rows = w.findAll('[data-testid=prompt]')
    expect(rows).toHaveLength(2)
    expect(rows[0]!.text()).toContain('Job #42')
    expect(rows[0]!.get('a').attributes('href')).toBe('/jobs/42')
    expect(rows[0]!.text()).toContain('implement')
    expect(rows[0]!.text()).toContain('claude-opus-5-5 · high')
    expect(rows[0]!.text()).toContain('running')
    expect(rows[0]!.text()).toContain('2.4k → 310 chars')
    expect(rows[0]!.findAll('a').at(-1)!.attributes('href')).toBe('/settings/prompts/3')
    expect(rows[1]!.text(), 'an ask thread has no page of its own').toContain('Review ask #9')
    expect(rows[1]!.findAll('a')).toHaveLength(1)
    expect(rows[1]!.text()).toContain('usage-limit')
    expect(rows[1]!.text()).toContain('Usage limit until 15:00')

    await w.findAll('button').find((b) => b.text() === 'Older prompts')!.trigger('click')
    await flushPromises()
    expect(asked.at(-1)?.get('before')).toBe('2')
    expect(w.findAll('[data-testid=prompt]')).toHaveLength(3)
  })

  it('reads its filters from the address, and typing a filter updates the address and the list', async () => {
    const { w, r } = await list('/settings/prompts?source=idea&sourceId=5')
    expect(Object.fromEntries(asked.at(-1)!)).toEqual({ source: 'idea', sourceId: '5' })
    expect((w.get('select[aria-label=Source]').element as HTMLSelectElement).value).toBe('idea')

    vi.useFakeTimers()
    await w.get('input[aria-label=Model]').setValue(' sonnet ')
    await w.get('select[aria-label=Outcome]').setValue('error')
    await vi.advanceTimersByTimeAsync(301)
    await flushPromises()
    expect(r.currentRoute.value.query).toEqual({ source: 'idea', sourceId: '5', model: 'sonnet', outcome: 'error' })
    expect(Object.fromEntries(asked.at(-1)!)).toEqual({ source: 'idea', sourceId: '5', model: 'sonnet', outcome: 'error' })

    await w.findAll('button').find((b) => b.text() === 'Clear filters')!.trigger('click')
    await vi.advanceTimersByTimeAsync(301)
    await flushPromises()
    expect(r.currentRoute.value.query).toEqual({})
  })

  it('says when only Admins can read them', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ title: 'Forbidden' }), { status: 403 })))
    const { w } = await list()

    expect(w.get('[role=alert]').text()).toBe('Only Admins can read the prompts.')
  })
})

describe('One prompt', () => {
  it('shows the prompt whole, its system rules, the reply and the settings', async () => {
    detail = { ...summary(3), session: '0f8a1c2e-1111-2222-3333-444455556666', resume: true, profile: 'work', prompt: 'Implement WI-42.\n\nThe whole text.',
      reply: 'Done: PR !17.', rules: 'You are agentd.' }
    const w = mount(PromptView, { props: { id: 3 }, global: { plugins: [router()] } })
    await flushPromises()

    const meta = w.get('[data-testid=prompt-meta]').text()
    expect(meta).toContain('Job #42')
    expect(meta).toContain('profile work')
    expect(meta).toContain('took 2m 05s')
    expect(meta).toContain('session 0f8a1c2e (resumed)')
    expect(w.get('[data-testid=prompt-text] pre').text()).toBe('Implement WI-42.\n\nThe whole text.')
    expect(w.get('[data-testid=prompt-rules] pre').text()).toBe('You are agentd.')
    expect(w.get('[data-testid=prompt-reply] pre').text()).toBe('Done: PR !17.')
  })

  it('a running prompt has no reply yet; a missing one says so', async () => {
    detail = { ...summary(3, { outcome: null, finishedAt: null }), prompt: 'p', reply: null, rules: null }
    const w = mount(PromptView, { props: { id: 3 }, global: { plugins: [router()] } })
    await flushPromises()
    expect(w.get('[data-testid=prompt-reply]').text()).toContain('Still running')
    expect(w.find('[data-testid=prompt-rules]').exists()).toBe(false)

    detail = null
    await w.setProps({ id: 4 })
    await flushPromises()
    expect(w.get('[role=alert]').text()).toBe('agentd has no prompt #4.')
  })
})

describe('Prompts link', () => {
  it('shows Admins the prompts of this job, idea or review, and nobody else', async () => {
    const session = useSessionStore()
    session.user = { name: 'local', roles: ['Admin'], provider: 'none' }
    const w = mount(PromptsLink, { props: { source: 'review-session', sourceId: 7 }, global: { plugins: [router()] } })
    await flushPromises()
    expect(w.get('a').attributes('href')).toBe('/settings/prompts?source=review-session&sourceId=7')

    session.user = { name: 'dev', roles: ['User'], provider: 'none' }
    await flushPromises()
    expect(w.find('a').exists()).toBe(false)
  })
})
