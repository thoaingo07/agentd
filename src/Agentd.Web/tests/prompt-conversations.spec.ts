import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import PromptConversationsView from '../ClientApps/dashboard/views/PromptConversationsView.vue'
import PromptConversationView from '../ClientApps/dashboard/views/PromptConversationView.vue'

// Settings → Prompts by conversation (prompt-log.md "Reading it"): grouped by kind; one opens into every turn, each like
// a work item's Transcript.
const blank = { template: '<div />' }
const router = () => createRouter({ history: createMemoryHistory(), routes: [
  { path: '/', component: blank },
  { path: '/settings/prompts', name: 'prompts', component: blank },
  { path: '/settings/prompts/all', name: 'prompts-all', component: blank },
  { path: '/settings/prompts/:source/:sourceId', name: 'prompt-conversation', component: blank },
  { path: '/settings/prompts/:id', name: 'prompt', component: blank },
  { path: '/jobs/:id', name: 'job', component: blank },
  { path: '/ideas/:id', name: 'idea', component: blank },
  { path: '/chats/:id', name: 'chat', component: blank },
  { path: '/reviews/:id', name: 'review', component: blank },
] })
const conversation = (source: string, sourceId: number, over: Record<string, unknown> = {}) => ({
  source, sourceId, turns: 3, firstAt: '2026-10-14T09:00:00Z', lastAt: '2026-10-14T10:00:00Z', lastId: sourceId * 10, lastOutcome: 'ok', running: false,
  models: ['claude-opus-5-5'], preview: `Review pull request !${sourceId}`, ...over,
})
const turn = (id: number, over: Record<string, unknown> = {}) => ({
  id, source: 'idea', sourceId: 7, step: null, session: null, resume: id > 1, model: 'sonnet', effort: 'high', profile: null, outcome: 'ok', error: null,
  startedAt: '2026-10-14T10:00:00Z', finishedAt: '2026-10-14T10:01:05Z', prompt: `prompt ${id}`, reply: `reply **${id}**`, rules: 'You brainstorm with the team.', ...over,
})
let asked: string[]

beforeEach(() => {
  setActivePinia(createPinia())
  asked = []
  vi.stubGlobal('fetch', vi.fn(async (req: Request) => {
    const url = new URL(req.url)
    asked.push(url.pathname + url.search)
    const json = (b: unknown, status = 200) => new Response(JSON.stringify(b), { status })
    if (url.pathname === '/api/prompts/conversations') {
      return json({ hasMore: !url.searchParams.has('before'), conversations: url.searchParams.has('before')
        ? [conversation('chat', 2)]
        : [conversation('idea', 7, { running: true }), conversation('job', 41, { lastOutcome: 'error' }), conversation('idea', 5)] })
    }
    if (url.pathname === '/api/prompts/conversations/idea/7') return json([turn(1), turn(2, { outcome: null, finishedAt: null, reply: null, error: null })])
    if (url.pathname === '/api/prompts/conversations/idea/9') return json({ title: 'Not found' }, 404)
    if (url.pathname === '/api/prompts/1/transcript') {
      return json([
        { seq: 1, jobId: null, ts: '2026-10-14T10:00:01Z', type: 'agent.text', payload: { text: 'Reading the repo first.' } },
        { seq: 2, jobId: null, ts: '2026-10-14T10:00:02Z', type: 'agent.tool_call', payload: { id: 't1', name: 'Read', input: { file_path: 'src/Theme.ts' } } },
        { seq: 3, jobId: null, ts: '2026-10-14T10:00:03Z', type: 'agent.tool_result', payload: { toolUseId: 't1', content: 'export const theme = {}', isError: false } },
      ])
    }
    if (url.pathname === '/api/prompts/2/transcript') return json([])
    return json({})
  }))
})
afterEach(() => vi.unstubAllGlobals())

describe('Prompts by conversation', () => {
  it('groups conversations by kind with their turns, models and outcome, and loads older ones', async () => {
    const w = mount(PromptConversationsView, { global: { plugins: [router()] } })
    await flushPromises()

    const groups = w.findAll('[data-testid=conversation-group]')
    expect(groups.map((g) => g.attributes('aria-label'))).toEqual(['Job', 'Idea'])
    expect(groups[1]!.findAll('[data-testid=conversation]').map((c) => c.attributes('href'))).toEqual(['/settings/prompts/idea/7', '/settings/prompts/idea/5'])
    const running = groups[1]!.get('[data-testid=conversation]')
    expect(running.text()).toContain('Idea #7')
    expect(running.text()).toContain('3 turns · claude-opus-5-5')
    expect(running.text()).toContain('running')
    expect(groups[0]!.text()).toContain('error')

    await w.findAll('button').find((b) => b.text() === 'Older conversations')!.trigger('click')
    await flushPromises()
    expect(asked.at(-1)).toBe('/api/prompts/conversations?limit=100&before=50')
    expect(w.findAll('[data-testid=conversation-group]').map((g) => g.attributes('aria-label'))).toEqual(['Job', 'Idea', 'Chat'])

    await w.get('select[aria-label=Kind]').setValue('idea')
    await flushPromises()
    expect(asked.at(-1)).toBe('/api/prompts/conversations?limit=100&source=idea')
  })

  it('a conversation shows every turn: the prompt with its rules, the transcript like a work item\'s, the reply', async () => {
    const w = mount(PromptConversationView, { props: { source: 'idea', sourceId: 7 }, global: { plugins: [router()] } })
    await flushPromises()

    const turns = w.findAll('[data-testid=turn]')
    expect(turns).toHaveLength(2)
    expect(turns[0]!.text()).toContain('Turn 1')
    expect(turns[0]!.text()).toContain('sonnet · high')
    expect(turns[0]!.text()).toContain('took 1m 05s')
    expect(turns[0]!.get('[data-testid=turn-prompt] pre').text()).toBe('prompt 1')
    expect(turns[0]!.findAll('[data-testid=turn-prompt] pre')[1]!.text()).toBe('You brainstorm with the team.')
    const transcript = turns[0]!.get('[data-testid=turn-transcript]')
    expect(transcript.text()).toContain('Reading the repo first.')
    expect(transcript.text()).toContain('Read')
    expect(turns[0]!.get('[data-testid=turn-reply]').text()).toContain('reply 1')
    expect(turns[1]!.text()).toContain('running')
    expect(turns[1]!.text()).toContain('resumed')
    expect(turns[1]!.get('[data-testid=turn-transcript]').text()).toContain('No transcript kept for this turn.')
    expect(w.get('header a').attributes('href')).toBe('/ideas/7')
  })

  it('a conversation with no prompts says so', async () => {
    const w = mount(PromptConversationView, { props: { source: 'idea', sourceId: 9 }, global: { plugins: [router()] } })
    await flushPromises()

    expect(w.get('[role=alert]').text()).toBe('No prompts were sent for idea #9.')
  })
})
