import AxeBuilder from '@axe-core/playwright'
import { test as base, expect, type APIRequestContext, type Page } from '@playwright/test'

declare global {
  interface Window {
    __cspViolations: { directive: string; blocked: string; disposition: string }[]
  }
}

let nextWorkItem = 9000 + Math.floor(Math.random() * 900_000)

/** Seeding through the E2E-only `/__test/*` endpoints. */
export const seed = {
  async job(request: APIRequestContext, title = 'E2E job', waiting = false): Promise<number> {
    const res = await request.post('/__test/jobs', { data: { workItemId: nextWorkItem++, title, waiting } })
    expect(res.ok()).toBe(true)
    return ((await res.json()) as { id: number }).id
  },
  async events(request: APIRequestContext, jobId: number, count: number, from = 1): Promise<void> {
    expect((await request.post(`/__test/jobs/${jobId}/events`, { data: { count, from } })).ok()).toBe(true)
  },
  async permission(request: APIRequestContext, jobId: number): Promise<void> {
    expect((await request.post(`/__test/jobs/${jobId}/permissions`)).ok()).toBe(true)
  },
}

/** Enforced CSP reports the server received (report-only Trusted Types reports are printed, not failed, in Phase 3). */
export async function serverCspReports(request: APIRequestContext): Promise<{ directive: string; disposition: string }[]> {
  const reports = (await (await request.get('/__test/csp-reports')).json()) as { directive: string; blockedUrl: string; disposition: string }[]
  const reportOnly = reports.filter((r) => r.disposition === 'report')
  if (reportOnly.length) console.log(`report-only CSP reports: ${reportOnly.map((r) => `${r.directive} ${r.blockedUrl}`).join('; ')}`)
  return reports.filter((r) => r.disposition !== 'report')
}

/** No serious or critical accessibility violations on the current page. */
export async function expectAccessible(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).analyze()
  const bad = results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical')
  expect(bad.map((v) => `${v.id}: ${v.nodes.map((n) => n.target.join(' ')).join(', ')}`)).toEqual([])
}

/** Every page collects the browser's CSP violations in `window.__cspViolations`. */
export const test = base.extend<{ cspViolations: () => Promise<Window['__cspViolations']> }>({
  page: async ({ page }, use) => {
    await page.addInitScript(() => {
      window.__cspViolations = []
      document.addEventListener('securitypolicyviolation', (e) =>
        window.__cspViolations.push({ directive: e.effectiveDirective, blocked: e.blockedURI, disposition: e.disposition }))
    })
    await use(page)
  },
  cspViolations: async ({ page }, use) => {
    await use(async () => (await page.evaluate(() => window.__cspViolations)).filter((v) => v.disposition === 'enforce'))
  },
})

export { expect }
