import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { setAntiforgeryUrl } from '../ClientApps/shared/api/http'
import RestartButton from '../ClientApps/dashboard/components/RestartButton.vue'

let available: boolean
let health: boolean[]
let calls: string[]
const reload = vi.fn()
beforeEach(() => {
  setAntiforgeryUrl('/bff/antiforgery')
  calls = []
  health = []
  reload.mockReset()
  vi.stubGlobal('location', { ...window.location, reload })
  vi.stubGlobal('confirm', vi.fn(() => true))
  vi.stubGlobal('fetch', vi.fn(async (input: Request | string) => {
    const req = typeof input === 'string' ? new Request(new URL(input, 'http://localhost')) : input
    const path = new URL(req.url).pathname
    calls.push(`${req.method} ${path}`)
    if (path === '/bff/antiforgery') return new Response(JSON.stringify({ token: 't' }))
    if (path === '/api/settings/restart' && req.method === 'GET' && calls.includes('POST /api/settings/restart')) {
      return health.shift() === false ? new Response(null, { status: 502 }) : new Response(JSON.stringify({ available, reason: null }))
    }
    if (path === '/api/settings/restart' && req.method === 'GET') return new Response(JSON.stringify({ available, reason: available ? null : 'Nothing would start agentd again.' }))
    if (path === '/api/settings/restart') return new Response(JSON.stringify({ available, reason: null }), { status: 202 })
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
    expect(calls.filter((c) => c === 'GET /api/settings/restart')).toHaveLength(5)
    expect(reload).toHaveBeenCalledOnce()
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
