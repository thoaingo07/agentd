import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import type { AgentEvent, IdeaDetail, IdeaSummary } from '../ClientApps/shared/api/types'
import { useConnectionStore } from '../ClientApps/dashboard/stores/connection'
import { ideaRefreshDelayMs, useIdeasStore } from '../ClientApps/dashboard/stores/ideas'
import IdeaView from '../ClientApps/dashboard/views/IdeaView.vue'
import IdeasView from '../ClientApps/dashboard/views/IdeasView.vue'

const summary = (id: number, status = 'Brainstorming', createdWorkItems: number[] = []): IdeaSummary => ({
  id, repo: 'sysmin', title: `Idea ${id}`, author: 'tngo', status, model: 'sonnet', effort: null, drafts: 0, createdWorkItems, messages: 2,
  createdAt: '2026-10-05T10:00:00Z', updatedAt: '2026-10-05T11:00:00Z',
})
const detail: IdeaDetail = {
  idea: { ...summary(4, 'Created', [5701, 5702]), drafts: 3 },
  drafts: [
    { type: 'User Story', title: 'Dark mode', description: 'As a user…', acceptanceCriteria: null, estimate: 3, parent: null, tags: [] },
    { type: 'Task', title: 'Theme tokens', description: null, acceptanceCriteria: null, estimate: 2, parent: 0, tags: [] },
    { type: 'Task', title: 'Toggle', description: null, acceptanceCriteria: null, estimate: 1, parent: 0, tags: [] },
  ],
  messages: [
    { direction: 'in', author: 'tngo', text: '<b>dark</b> mode please', at: '2026-10-05T10:00:00Z' },
    { direction: 'out', author: 'agentd', text: 'Which **pages**?', at: '2026-10-05T10:01:00Z' },
  ],
}
const ev = (seq: number, type: string, ideaId: number): AgentEvent => ({ seq, jobId: null, ts: '2026-10-05T10:00:00Z', type, payload: { ideaId } })
const router = () => createRouter({
  history: createMemoryHistory(),
  routes: [
    { path: '/', component: { template: '<div />' } },
    { path: '/ideas', name: 'ideas', component: { template: '<div />' } },
    { path: '/ideas/:id', name: 'idea', component: { template: '<div />' } },
    { path: '/workitems/:id', name: 'work-item', component: { template: '<div />' } },
  ],
})

let routes: Record<string, unknown>
beforeEach(() => {
  setActivePinia(createPinia())
  routes = { '/api/ideas/4': detail, '/api/ideas': [summary(5), summary(4, 'Created', [5701, 5702])] }
  vi.stubGlobal('fetch', vi.fn(async (req: Request) => {
    const path = new URL(req.url).pathname
    return path in routes ? new Response(JSON.stringify(routes[path])) : new Response(JSON.stringify({ title: 'Not found' }), { status: 404 })
  }))
})
afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

describe('Ideas', () => {
  it('lists ideas with their status and the work items they created', async () => {
    const w = mount(IdeasView, { global: { plugins: [router()] } })
    await flushPromises()

    const rows = w.findAll('[data-testid=idea-row]')
    expect(rows.map((r) => r.find('a').text())).toEqual(['#5 Idea 5', '#4 Idea 4'])
    expect(rows[1]!.text()).toContain('Created')
    expect(rows[1]!.findAll('a').slice(1).map((a) => a.attributes('href'))).toEqual(['/workitems/5701', '/workitems/5702'])
  })

  it('shows drafts as stories with their tasks, and the conversation (people as plain text, agentd as Markdown)', async () => {
    const w = mount(IdeaView, { props: { id: 4 }, global: { plugins: [router()] } })
    await flushPromises()

    const drafts = w.findAll('[data-testid=draft]')
    expect(drafts).toHaveLength(1)
    expect(drafts[0]!.findAll('li').map((li) => li.text())).toEqual(['Theme tokens 2 h', 'Toggle 1 h'])
    expect(w.find('[data-direction=in]').text()).toContain('<b>dark</b> mode please')
    expect(w.find('[data-direction=in] b').exists()).toBe(false)
    expect(w.find('[data-direction=out] strong').text()).toBe('pages')
  })

  it('an unknown idea says so', async () => {
    const w = mount(IdeaView, { props: { id: 9 }, global: { plugins: [router()] } })
    await flushPromises()

    expect(w.text()).toContain('There is no idea #9.')
  })

  it('idea events on the "all" stream re-read the list and the open idea once per burst', async () => {
    vi.useFakeTimers()
    const ideas = useIdeasStore()
    await ideas.load()
    await ideas.open(4)
    vi.mocked(fetch).mockClear()
    routes['/api/ideas/4'] = { ...detail, messages: [...detail.messages, { direction: 'out', author: 'agentd', text: 'Done.', at: '2026-10-05T10:02:00Z' }] }

    const connection = useConnectionStore()
    connection.route('all', ev(1, 'idea.message', 4))
    connection.route('all', ev(2, 'idea.updated', 4))
    connection.route('all', ev(3, 'JobStarted', 4))
    await vi.advanceTimersByTimeAsync(ideaRefreshDelayMs + 1)

    expect(vi.mocked(fetch).mock.calls.map(([r]) => new URL((r as Request).url).pathname).sort()).toEqual(['/api/ideas', '/api/ideas/4'])
    expect(ideas.current?.messages).toHaveLength(3)
  })
})
