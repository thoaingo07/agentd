import { expect, seed, test } from './fixtures'

// A long run's transcript opens on its newest events, takes a burst of live ones, and pages back as the reader
// scrolls up, without replaying the whole run.
test('a long transcript opens on the newest page, streams a burst, and loads earlier pages on scrolling up', async ({ page, request }) => {
  const job = await seed.job(request, 'Long transcript')
  await seed.events(request, job, 250)
  await page.goto(`/jobs/${job}`)
  await expect(page.getByRole('status')).toHaveText(/Live/)
  const rows = page.locator('[data-seq]').filter({ hasText: /e2e event \d+/ })

  await expect(page.getByText('e2e event 250', { exact: true })).toBeVisible()
  expect(await rows.count()).toBeLessThanOrEqual(100)

  await seed.events(request, job, 150, 251)
  await expect(page.getByText('e2e event 400', { exact: true })).toBeVisible()

  const transcript = page.locator('[aria-label="Transcript"]')
  for (let i = 0; i < 10 && !(await page.getByText('e2e event 1', { exact: true }).count()); i++) {
    await transcript.evaluate((el) => (el.scrollTop = 0))
    await page.waitForTimeout(300)
  }

  await expect(page.getByText('e2e event 1', { exact: true })).toBeAttached()
  await expect(rows).toHaveCount(400)
  const seqs = await rows.evaluateAll((els) => els.map((r) => Number(r.getAttribute('data-seq'))))
  expect([...seqs].sort((a, b) => a - b)).toEqual(seqs)
})
