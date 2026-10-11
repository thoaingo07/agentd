import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { setAntiforgeryUrl } from '../ClientApps/shared/api/http'
import { useConnectionStore } from '../ClientApps/dashboard/stores/connection'
import { chatRefreshDelayMs, useChatsStore } from '../ClientApps/dashboard/stores/chats'
import ChatView from '../ClientApps/dashboard/views/ChatView.vue'
import ChatsView from '../ClientApps/dashboard/views/ChatsView.vue'

const router = () => createRouter({ history: createMemoryHistory(), routes: [
  { path: '/', component: { template: '<div />' } },
  { path: '/chats', name: 'chats', component: { template: '<div />' } },
  { path: '/chats/:id', name: 'chat', component: { template: '<div />' } },
] })
let chat: Record<string, unknown>
let posts: { path: string; body: unknown }[]

beforeEach(() => {
  setActivePinia(createPinia())
  setAntiforgeryUrl('/bff/antiforgery')
  posts = []
  chat = { id: 4, author: 'dev@example.com', status: 'Open', repos: ['sysmin'], model: null, effort: null, inChat: false, thinking: true, messages: [
    { direction: 'in', author: 'dev@example.com', text: 'what does the <b>sync</b> do?', at: '2026-10-27T10:00:00Z' },
    { direction: 'out', author: 'agent', text: 'It pages by **key**.', at: '2026-10-27T10:01:00Z' },
  ] }
  vi.stubGlobal('fetch', vi.fn(async (req: Request) => {
    const path = new URL(req.url).pathname
    if (path === '/bff/antiforgery') return new Response(JSON.stringify({ token: 't' }))
    if (req.method === 'POST') {
      const text = await req.text()
      posts.push({ path, body: text ? JSON.parse(text) : null })
      if (path === '/api/chats') return new Response(JSON.stringify({ id: 9 }), { status: 201 })
      if (path === '/api/chats/4/close') chat = { ...chat, status: 'Closed', thinking: false }
      return new Response(null, { status: 202 })
    }
    if (path === '/api/config') return new Response(JSON.stringify({ repositories: [{ name: 'sysmin', organization: 'o', project: 'p' }, { name: 'portal', organization: 'o', project: 'p' }] }))
    if (path === '/api/chats') return new Response(JSON.stringify([{ id: 4, author: 'dev@example.com', status: 'Open', repos: ['sysmin'], model: null, effort: null, inChat: true, title: 'what does the sync do?', messages: 2, createdAt: '2026-10-27T10:00:00Z', updatedAt: '2026-10-27T10:01:00Z' }]))
    if (path === '/api/chats/4') return new Response(JSON.stringify(chat))
    return new Response(JSON.stringify({ title: 'Not found' }), { status: 404 })
  }))
})
afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

describe('Chat', () => {
  it('lists questions and asks a new one about one repository', async () => {
    const r = router()
    const w = mount(ChatsView, { global: { plugins: [r] } })
    await flushPromises()
    expect(w.get('[data-testid=chat-row]').text()).toContain('#4 what does the sync do?')
    expect(w.get('[data-testid=chat-row]').text()).toContain('in chat')

    await w.get('textarea[aria-label="Your question"]').setValue(' where is the login token checked? ')
    await w.get('select[aria-label="Repository"]').setValue('portal')
    await w.get('form').trigger('submit')
    await flushPromises()

    expect(posts).toEqual([{ path: '/api/chats', body: { text: 'where is the login token checked?', repo: 'portal', model: null, effort: null } }])
    expect(r.currentRoute.value.fullPath).toBe('/chats/9')
  })

  it('shows the conversation (people as text, the agent as Markdown), thinking, follow-ups and close', async () => {
    const w = mount(ChatView, { props: { id: 4 }, global: { plugins: [router()] } })
    await flushPromises()

    expect(w.find('[data-direction=in] b').exists()).toBe(false)
    expect(w.get('[data-direction=out] strong').text()).toBe('key')
    expect(w.get('[role=status]').text()).toContain('thinking…')

    await w.get('textarea[aria-label="Follow up"]').setValue('and who calls it?')
    await w.get('form').trigger('submit')
    await flushPromises()
    await w.findAll('button').find((b) => b.text() === 'Close chat')!.trigger('click')
    await flushPromises()

    expect(posts.map((p) => p.path)).toEqual(['/api/chats/4/messages', '/api/chats/4/close'])
    expect(posts[0]!.body).toEqual({ text: 'and who calls it?' })
    expect(w.text()).toContain('This chat is closed.')
    expect(w.find('textarea').exists()).toBe(false)
  })

  it('a chat.message event re-reads the open chat once per burst', async () => {
    vi.useFakeTimers()
    const chats = useChatsStore()
    await chats.open(4)
    vi.mocked(fetch).mockClear()

    const connection = useConnectionStore()
    connection.route('all', { seq: 1, jobId: null, ts: '2026-10-27T10:00:00Z', type: 'chat.message', payload: { chatId: 4 } })
    connection.route('all', { seq: 2, jobId: null, ts: '2026-10-27T10:00:00Z', type: 'chat.message', payload: { chatId: 4 } })
    await vi.advanceTimersByTimeAsync(chatRefreshDelayMs + 1)

    expect(vi.mocked(fetch).mock.calls.map(([r]) => new URL((r as Request).url).pathname)).toEqual(['/api/chats/4'])
  })
})
