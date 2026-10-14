import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { setAntiforgeryUrl } from '../ClientApps/shared/api/http'
import RestartButton from '../ClientApps/dashboard/components/RestartButton.vue'

let available: boolean
let running: { source: string; id: number; label: string }[]
let health: boolean[]
let calls: string[]
const reload = vi.fn()
beforeEach(() => {
  setAntiforgeryUrl('/bff/antiforgery')
  calls = []
  health = []
  running = []
  reload.mockReset()
  vi.stubGlobal('location', { ...window.location, reload })
  vi.stubGlobal('confirm', vi.fn(() => true))
  vi.stubGlobal('fetch', vi.fn(async (input: Request | string) => {
    const req = typeof input === 'string' ? new Request(new URL(input, 'http://localhost')) : input
    const path = new URL(req.url).pathname
    calls.push(`${req.method} ${path}${new URL(req.url).search}`)
    if (path === '/bff/antiforgery') return new Response(JSON.stringify({ token: 't' }))
    if (path === '/api/settings/restart' && req.method === 'GET' && calls.includes('POST /api/settings/restart')) {
      return health.shift() === false ? new Response(null, { status: 502 }) : new Response(JSON.stringify({ available, reason: null, running: [] }))
    }
    if (path === '/api/settings/restart' && req.method === 'GET') return new Response(JSON.stringify({ available, reason: available ? null : 'Nothing would start agentd again.', running }))
    if (path === '/api/settings/restart') return new Response(JSON.stringify({ available, reason: null, running }), { status: 202 })
    return new Response(null, { status: 404 })
  }))
})
afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

describe('Restart now', () => {
  it('restarts after confirming, waits for agentd to go away and come back, then reloads', async () => {
    available = true
    health = [true, false, false, true]
    const w = mount(RestartButton)
    await flushPromises()
    vi.useFakeTimers()

    await w.get('button').trigger('click')
    await flushPromises()
    expect(w.get('[role=status]').text()).toContain('Restarting agentd')
    for (let i = 0; i < 4; i++) await vi.advanceTimersByTimeAsync(2000)

    expect(calls).toContain('POST /api/settings/restart')
    expect(confirm).toHaveBeenCalledWith('Restart agentd now? Nothing is running.')
    expect(calls.filter((c) => c === 'GET /api/settings/restart'), 'on mount, at the click, then 4 polls').toHaveLength(6)
    expect(reload).toHaveBeenCalledOnce()
  })

  it('while work runs it lists it, and restarts only when told to anyway', async () => {
    available = true
    const w = mount(RestartButton)
    await flushPromises()
    expect(w.find('[data-testid=restart-running]').exists()).toBe(false)

    running = [{ source: 'job', id: 41, label: 'Job 41 (WI-5702) is running' }, { source: 'idea', id: 4, label: 'Idea 4: the agent is answering' }]
    vi.mocked(confirm).mockReturnValueOnce(false)
    await w.get('button').trigger('click')
    await flushPromises()
    expect(vi.mocked(confirm).mock.calls[0]![0]).toBe('Running now:\n• Job 41 (WI-5702) is running\n• Idea 4: the agent is answering\n\nRestart anyway? They stop and pick up where they were once agentd is back.')
    expect(calls.some((c) => c.startsWith('POST /api/settings/restart')), 'cancelled: nothing stops').toBe(false)
    expect(w.get('[data-testid=restart-running]').text()).toBe('Running now: Job 41 (WI-5702) is running; Idea 4: the agent is answering')

    await w.get('button').trigger('click')
    await flushPromises()
    expect(calls).toContain('POST /api/settings/restart?force=true')
  })

  it('without a supervisor it says how to restart instead', async () => {
    available = false
    const w = mount(RestartButton)
    await flushPromises()

    expect(w.find('button').exists()).toBe(false)
    expect(w.text()).toContain('agentd daemon restart')
    expect(w.text()).toContain('Nothing would start agentd again.')
  })
})
