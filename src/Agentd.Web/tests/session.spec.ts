import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { defineComponent, h } from 'vue'
import type { AgentEvent } from '../ClientApps/shared/api/types'
import { renderMarkdown, safeUrl } from '../ClientApps/shared/utils/markdown-lite'
import { duration, humanize } from '../ClientApps/shared/utils/format'
import { toRows, toolSummary } from '../ClientApps/dashboard/components/session/transcript'
import EventList from '../ClientApps/dashboard/components/session/EventList.vue'
import ToolCallCard from '../ClientApps/dashboard/components/session/ToolCallCard.vue'
import MessageComposer from '../ClientApps/dashboard/components/session/MessageComposer.vue'
import PermissionBanner from '../ClientApps/dashboard/components/session/PermissionBanner.vue'

const ev = (seq: number, type: string, payload: object = {}): AgentEvent => ({ seq, jobId: 7, ts: '2026-10-04T10:00:00Z', type, payload })
const md = (text: string) => mount(defineComponent({ render: () => h('div', renderMarkdown(text)) }))

beforeEach(() => setActivePinia(createPinia()))
afterEach(() => vi.unstubAllGlobals())

describe('markdown-lite', () => {
  it('renders the supported subset', () => {
    const w = md('Hello **world** and `code`\n\n- one\n- two\n\n```ts\nconst x = 1\n```\n\nSee [docs](https://example.com/a).')
    expect(w.find('strong').text()).toBe('world')
    expect(w.find('code').text()).toBe('code')
    expect(w.findAll('li').map((l) => l.text())).toEqual(['one', 'two'])
    expect(w.find('pre').text()).toBe('const x = 1')
    expect(w.find('a').attributes()).toMatchObject({ href: 'https://example.com/a', rel: 'noopener noreferrer', target: '_blank' })
  })

  it('keeps hostile input inert', () => {
    const w = md('<script>alert(1)</script> <img src=x onerror=alert(1)> [click](javascript:alert(1)) [d](data:text/html,x)')
    expect(w.find('script').exists()).toBe(false)
    expect(w.find('img').exists()).toBe(false)
    expect(w.find('a').exists()).toBe(false)
    expect(w.text()).toContain('<script>alert(1)</script>')
    expect(w.text()).toContain('click')
    expect(safeUrl('javascript:alert(1)')).toBeNull()
    expect(safeUrl('/relative')).toBeNull()
  })
})

describe('format', () => {
  it('formats durations and event names', () => {
    expect([duration(42), duration(125), duration(3900)]).toEqual(['42s', '2m 05s', '1h 05m'])
    expect(humanize('JobStarted')).toBe('Job started')
    expect(humanize('phase.set')).toBe('Phase set')
  })
})

describe('transcript rows', () => {
  it('pairs a tool call with its result and hides agent bookkeeping', () => {
    const rows = toRows([
      ev(1, 'agent.session', { sessionId: 's' }),
      ev(2, 'agent.tool_call', { id: 't1', name: 'Bash', inputJson: '{"command":"dotnet test"}' }),
      ev(3, 'agent.text', { text: 'Running tests' }),
      ev(4, 'agent.tool_result', { toolUseId: 't1', content: 'ok', isError: false }),
      ev(5, 'DeveloperQuestionAsked', { question: 'v1 or v2?', options: ['v1', 'v2'] }),
      ev(6, 'DeveloperReplied', { reply: 'v2', from: 'tngo', via: { value: 'discord' } }),
      ev(7, 'JobFailed', { reason: 'build broke' }),
      ev(8, 'agent.rate_limit', {}),
      ev(9, 'PlanApproved', { by: 'tngo' }),
    ])
    expect(rows.map((r) => r.kind)).toEqual(['tool', 'text', 'question', 'reply', 'error', 'state'])
    const tool = rows[0] as Extract<(typeof rows)[number], { kind: 'tool' }>
    expect(tool.result?.seq).toBe(4)
    expect(rows[3]).toMatchObject({ reply: 'v2', from: 'tngo', via: 'discord' })
    expect(rows[5]).toMatchObject({ title: 'Plan approved' })
    expect(toolSummary(tool.call)).toEqual({ name: 'Bash', summary: 'dotnet test' })
  })
})

describe('step rows', () => {
  it('shows which step, provider, model and effort the agent runs', () => {
    const rows = toRows([
      ev(1, 'step.started', { step: 'plan', profile: null, model: 'claude-opus-5-5', effort: 'high', line: '📝 Plan · Claude · claude-opus-5-5 · effort high' }),
      ev(2, 'step.started', { step: 'implement', profile: 'deepseek', model: 'deepseek-flash', line: '🔨 Implement · deepseek · deepseek-flash' }),
    ])
    expect(rows.map((r) => (r.kind === 'state' ? r.title : r.kind))).toEqual(['📝 Plan · Claude · claude-opus-5-5 · effort high', '🔨 Implement · deepseek · deepseek-flash'])
    expect(rows.every((r) => r.kind === 'state')).toBe(true)   // the "state" filter
  })
})

describe('ToolCallCard', () => {
  it('starts expanded when the result failed', async () => {
    const w = mount(ToolCallCard, {
      props: {
        call: ev(2, 'agent.tool_call', { id: 't1', name: 'Bash', inputJson: '{"command":"dotnet build"}' }),
        result: ev(3, 'agent.tool_result', { toolUseId: 't1', content: 'error CS1002', isError: true }),
      },
    })
    await flushPromises()
    expect(w.find('[data-failed]').exists()).toBe(true)
    expect(w.get('button').attributes('aria-expanded')).toBe('true')
    expect(w.text()).toContain('error CS1002')
  })

  it('cuts long output and shows all on request', async () => {
    const content = Array.from({ length: 250 }, (_, i) => `line ${i}`).join('\n')
    const w = mount(ToolCallCard, { props: { call: ev(2, 'agent.tool_call', { id: 't', name: 'Read' }), result: ev(3, 'agent.tool_result', { toolUseId: 't', content, isError: true }) } })
    expect(w.text()).not.toContain('line 249')
    await w.findAll('button').find((b) => b.text().startsWith('Show all'))!.trigger('click')
    expect(w.text()).toContain('line 249')
  })
})

describe('EventList', () => {
  function scroller(w: ReturnType<typeof mount>) {
    const el = w.get('[aria-label="Transcript"]').element as HTMLElement
    let top = 0
    let height = 1000
    Object.defineProperty(el, 'clientHeight', { configurable: true, get: () => 400 })
    Object.defineProperty(el, 'scrollHeight', { configurable: true, get: () => height })
    Object.defineProperty(el, 'scrollTop', { configurable: true, get: () => top, set: (v: number) => (top = v) })
    return { el, setHeight: (v: number) => (height = v), top: () => top, setTop: (v: number) => (top = v) }
  }

  it('stops following on scroll-up and counts new events', async () => {
    const events = [ev(1, 'agent.text', { text: 'a' })]
    const w = mount(EventList, { props: { events, hasMore: false, loadEarlier: async () => {} }, attachTo: document.body })
    const s = scroller(w)
    s.setTop(100) // 500 px from the bottom
    await w.get('[aria-label="Transcript"]').trigger('scroll')
    expect(w.emitted('update:follow')?.at(-1)).toEqual([false])
    await w.setProps({ follow: false, events: [...events, ev(2, 'agent.text', { text: 'b' }), ev(3, 'agent.text', { text: 'c' })] })
    expect(w.text()).toContain('2 new events ↓')

    await w.findAll('button').find((b) => b.text().includes('new events'))!.trigger('click')
    expect(w.emitted('update:follow')?.at(-1)).toEqual([true])
    w.unmount()
  })

  it('load earlier keeps the reading position', async () => {
    const holder: { s?: ReturnType<typeof scroller> } = {}
    const loadEarlier = vi.fn(async () => {
      holder.s!.setHeight(1600)
    })
    const w = mount(EventList, { props: { events: [ev(5, 'agent.text', { text: 'x' })], hasMore: true, loadEarlier, follow: false }, attachTo: document.body })
    const s = (holder.s = scroller(w))
    await flushPromises() // the initial scroll-to-bottom on mount
    s.setTop(50)

    await w.findAll('button').find((b) => b.text() === 'Load earlier')!.trigger('click')
    await flushPromises()

    expect(loadEarlier).toHaveBeenCalled()
    expect(s.top()).toBe(650)
    w.unmount()
  })
})

describe('MessageComposer', () => {
  it('posts the message (Ctrl+Enter) and is disabled for finished jobs', async () => {
    const calls: Request[] = []
    vi.stubGlobal('fetch', vi.fn(async (req: Request) => {
      calls.push(req)
      return req.url.endsWith('/bff/antiforgery') ? new Response(JSON.stringify({ token: 't' })) : new Response(JSON.stringify({ outcome: 'Resumed' }), { status: 202 })
    }))
    const w = mount(MessageComposer, { props: { jobId: 7, state: 'WaitingForHuman' } })
    await w.get('textarea').setValue('  use v2  ')
    await w.get('textarea').trigger('keydown', { key: 'Enter', ctrlKey: true })
    await flushPromises()

    const post = calls.find((c) => c.method === 'POST')!
    expect(new URL(post.url).pathname).toBe('/api/jobs/7/messages')
    expect(await post.clone().json()).toEqual({ text: 'use v2' })
    expect(w.emitted('sent')).toEqual([['use v2']])

    await w.setProps({ state: 'Done' })
    expect(w.get('textarea').attributes('disabled')).toBeDefined()
  })
})

describe('PermissionBanner', () => {
  it('shows the command as text and answers with the chat\'s four choices', async () => {
    const summary = '<img src=x onerror=alert(1)> && npm install'
    const w = mount(PermissionBanner, { props: { repo: 'sysmin', requests: [{ id: 3, tool: 'Bash', summary, ruleKeys: ['Bash(npm install:*)'], requestedAt: '2026-10-05T10:00:00Z' }] } })

    expect(w.find('pre').text()).toBe(summary)
    expect(w.find('img').exists()).toBe(false)
    const buttons = w.findAll('button')
    expect(buttons.map((b) => b.text())).toEqual(['Allow once', 'Allow for this job', 'Always allow in sysmin', 'Deny'])
    for (const b of buttons) await b.trigger('click')
    expect(w.emitted('answer')).toEqual([[3, 'once'], [3, 'job'], [3, 'repo'], [3, 'deny']])
  })
})
