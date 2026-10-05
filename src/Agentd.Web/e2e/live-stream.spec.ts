import type { WebSocketRoute } from '@playwright/test'
import { expect, seed, test } from './fixtures'

// The live stream survives a dropped connection: after reconnecting it replays from the last seq, with no gaps or
// duplicates. The hub's WebSocket is routed through Playwright so the test can drop it and refuse reconnects for a while
// (context.setOffline doesn't close an open WebSocket).
test('events keep their order with no gaps or duplicates across a reconnect', async ({ page, request }) => {
  let offline = false
  const sockets: WebSocketRoute[] = []
  await page.routeWebSocket(/\/hubs\//, (ws) => {
    if (offline) return void ws.close({ code: 1011, reason: 'e2e offline' })
    sockets.push(ws)
    ws.connectToServer()
  })
  const job = await seed.job(request, 'Live stream')
  await page.goto(`/jobs/${job}`)
  await expect(page.getByRole('status')).toHaveText(/Live/)
  const rows = page.locator('[data-seq]').filter({ hasText: /e2e event \d+/ })   // the agent's text, not the job's state rows

  await seed.events(request, job, 30)
  await expect(rows).toHaveCount(30)

  offline = true
  for (const ws of sockets.splice(0)) await ws.close({ code: 1011, reason: 'e2e drop' })
  await expect(page.getByRole('status')).toHaveText(/Reconnecting|Offline/)
  await seed.events(request, job, 20, 31)
  offline = false

  await expect(page.getByRole('status')).toHaveText(/Live/, { timeout: 20_000 })
  await expect(rows).toHaveCount(50)
  const seqs = await rows.evaluateAll((els) => els.map((r) => Number(r.getAttribute('data-seq'))))
  expect(new Set(seqs).size).toBe(50)
  expect([...seqs].sort((a, b) => a - b)).toEqual(seqs)
  await expect(page.getByText('e2e event 50')).toBeVisible()
})
