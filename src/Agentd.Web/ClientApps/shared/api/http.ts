// The only place that calls fetch (eslint enforces it). Same-origin cookies, the antiforgery token kept
// in memory (never in storage), ProblemDetails → ApiError, and interceptors registered with use().
// See docs/security §2.4 and T3.7.
import type { Problem } from './types'

export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code?: string,
    message?: string,
    readonly problem?: Problem,
  ) {
    super(message ?? `Request failed (${status})`)
    this.name = 'ApiError'
  }

  static async from(res: Response): Promise<ApiError> {
    let problem: Problem | undefined
    try {
      problem = (await res.clone().json()) as Problem
    } catch {
      // Not JSON (a proxy error page, an empty body).
    }
    return new ApiError(res.status, problem?.code, problem?.detail ?? problem?.title ?? res.statusText, problem)
  }
}

export interface Middleware {
  onRequest?(req: Request): Request | Promise<Request>
  onResponse?(res: Response, req: Request): Response | Promise<Response>
  onError?(err: ApiError, req: Request): void | Promise<void>
}

const middlewares: Middleware[] = []

/** Registers an interceptor (run in registration order); returns a function that removes it. */
export function use(m: Middleware): () => void {
  middlewares.push(m)
  return () => {
    const i = middlewares.indexOf(m)
    if (i >= 0) middlewares.splice(i, 1)
  }
}

let unauthorized: () => void = () => {}

/** Called on every 401 (the session store: a no-op in Mode None, a reload behind Cloudflare Access). */
export function onUnauthorized(handler: () => void): void {
  unauthorized = handler
}

let xsrf: string | null = null

/** Forget the antiforgery token (after login/logout, or a user change). */
export function resetXsrf(): void {
  xsrf = null
}

async function ensureToken(force: boolean): Promise<string> {
  if (xsrf && !force) return xsrf
  xsrf = (await request<{ token: string }>('GET', '/bff/antiforgery')).token
  return xsrf
}

async function request<T>(method: string, url: string, body?: unknown, token?: string): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' }
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  if (token) headers['X-XSRF-TOKEN'] = token
  // Absolute against the page: same origin, and Request implementations without a base URL accept it.
  let req = new Request(new URL(url, document.baseURI), {
    method,
    credentials: 'same-origin',
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  for (const m of [...middlewares]) if (m.onRequest) req = await m.onRequest(req)

  let res = await fetch(req.clone())
  for (const m of [...middlewares]) if (m.onResponse) res = await m.onResponse(res, req)

  if (!res.ok) {
    const err = await ApiError.from(res)
    if (err.status === 401) unauthorized()
    for (const m of [...middlewares]) await m.onError?.(err, req)
    throw err
  }
  const text = res.status === 204 ? '' : await res.text()
  return (text.length === 0 ? undefined : JSON.parse(text)) as T
}

export function get<T>(url: string): Promise<T> {
  return request<T>('GET', url)
}

/**
 * An unsafe request with the antiforgery header. A stale token (400 antiforgery_invalid) is refetched
 * and the request retried exactly once; that is the only retry anywhere in the client.
 */
export async function send<T = void>(method: 'POST' | 'PUT' | 'PATCH' | 'DELETE', url: string, body?: unknown): Promise<T> {
  try {
    return await request<T>(method, url, body, await ensureToken(false))
  } catch (err) {
    if (err instanceof ApiError && err.status === 400 && err.code === 'antiforgery_invalid') {
      return await request<T>(method, url, body, await ensureToken(true))
    }
    throw err
  }
}
