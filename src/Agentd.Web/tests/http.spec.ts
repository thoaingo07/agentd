import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError, get, onUnauthorized, resetXsrf, send, use } from '../ClientApps/shared/api/http'

type Handler = (req: Request) => Response | Promise<Response>
let handler: Handler
const calls: Request[] = []
const removers: (() => void)[] = []

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
}

beforeEach(() => {
  calls.length = 0
  resetXsrf()
  vi.stubGlobal('fetch', vi.fn(async (req: Request) => {
    calls.push(req)
    return handler(req)
  }))
})

afterEach(() => {
  removers.splice(0).forEach((r) => r())
  vi.unstubAllGlobals()
})

describe('send', () => {
  it('adds the in-memory token and recovers from a stale one exactly once', async () => {
    let tokens = 0
    handler = (req) => {
      if (req.url.endsWith('/bff/antiforgery')) return json({ token: `t${++tokens}` })
      return req.headers.get('X-XSRF-TOKEN') === 't2' ? new Response(null, { status: 204 }) : json({ code: 'antiforgery_invalid' }, 400)
    }

    await send('POST', '/api/jobs/7/cancel')

    expect(calls.map((c) => `${c.method} ${new URL(c.url).pathname} ${c.headers.get('X-XSRF-TOKEN') ?? ''}`.trim())).toEqual([
      'GET /bff/antiforgery',
      'POST /api/jobs/7/cancel t1',
      'GET /bff/antiforgery',
      'POST /api/jobs/7/cancel t2',
    ])
    expect(window.localStorage.length + window.sessionStorage.length).toBe(0)
  })

  it('gives up after one retry', async () => {
    handler = (req) => (req.url.endsWith('/bff/antiforgery') ? json({ token: 'x' }) : json({ code: 'antiforgery_invalid' }, 400))

    await expect(send('POST', '/api/jobs/7/retry')).rejects.toMatchObject({ status: 400, code: 'antiforgery_invalid' })
    expect(calls.filter((c) => c.method === 'POST')).toHaveLength(2)
  })

  it('parses ProblemDetails into ApiError and reports 401s', async () => {
    const unauthorized = vi.fn()
    onUnauthorized(unauthorized)
    handler = (req) =>
      req.url.endsWith('/api/jobs/8') ? json({ title: 'Conflict', detail: 'Cannot cancel from state Done.', code: 'invalid_transition' }, 409) : json({}, 401)

    const err = await get('/api/jobs/8').catch((e: unknown) => e)
    await get('/api/dashboard').catch(() => {})

    expect(err).toBeInstanceOf(ApiError)
    expect(err).toMatchObject({ status: 409, code: 'invalid_transition', message: 'Cannot cancel from state Done.' })
    expect(unauthorized).toHaveBeenCalledTimes(1)
  })
})

describe('interceptors', () => {
  it('run in registration order and can replace the request and the response', async () => {
    const order: string[] = []
    handler = (req) => json({ seen: req.headers.get('X-Trace') })
    removers.push(
      use({
        onRequest: (req) => {
          order.push('a:req')
          const headers = new Headers(req.headers)
          headers.set('X-Trace', 'a')
          return new Request(req, { headers })
        },
        onResponse: (res) => (order.push('a:res'), res),
      }),
      use({
        onRequest: (req) => (order.push('b:req'), req),
        onResponse: () => (order.push('b:res'), json({ replaced: true })),
      }),
    )

    const result = await get<{ replaced: boolean }>('/api/dashboard')

    expect(order).toEqual(['a:req', 'b:req', 'a:res', 'b:res'])
    expect(calls[0]!.headers.get('X-Trace')).toBe('a')
    expect(result).toEqual({ replaced: true })
  })

  it('onError gets the parsed error; use() returns an unregister function', async () => {
    const seen: (string | undefined)[] = []
    handler = () => json({ code: 'not_found' }, 404)
    const remove = use({ onError: (err) => void seen.push(err.code) })

    await get('/api/jobs/1').catch(() => {})
    remove()
    await get('/api/jobs/1').catch(() => {})

    expect(seen).toEqual(['not_found'])
  })

  it('a throwing interceptor rejects the call without a retry', async () => {
    handler = () => json({})
    removers.push(use({ onRequest: () => Promise.reject(new Error('blocked')) }))

    await expect(get('/api/dashboard')).rejects.toThrow('blocked')
    expect(calls).toHaveLength(0)
  })
})
