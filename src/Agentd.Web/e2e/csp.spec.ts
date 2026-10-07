import { expect, expectAccessible, seed, serverCspReports, test } from './fixtures'

// Zero enforced CSP violations on every page and interaction (no inline scripts or styles, no eval).
test('every page and interaction runs without CSP violations, and is accessible', async ({ page, request, cspViolations }) => {
  const job = await seed.job(request, 'CSP tour')
  await seed.events(request, job, 3)
  await seed.permission(request, job)

  const response = await page.goto('/')
  expect(response?.headers()['content-security-policy']).toContain("script-src 'self'")
  await expect(page.getByRole('status')).toHaveText(/Live/)
  await expectAccessible(page)

  await page.goto(`/jobs/${job}`)
  await expect(page.getByText('e2e event 3')).toBeVisible()
  await expect(page.getByTestId('permission-request')).toBeVisible()
  await expectAccessible(page)
  for (const tab of ['Diff', 'Details', 'Transcript']) await page.getByRole('tab', { name: tab }).click()
  await page.getByRole('button', { name: 'Cancel' }).click()
  await expect(page.getByRole('dialog')).toBeVisible()
  await page.getByRole('button', { name: 'Back' }).click()

  for (const path of ['/history', '/ideas', '/settings', '/settings/database']) {
    await page.goto(path)
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible()
    await expectAccessible(page)
  }

  expect(await cspViolations()).toEqual([])
  expect(await serverCspReports(request)).toEqual([])
})
