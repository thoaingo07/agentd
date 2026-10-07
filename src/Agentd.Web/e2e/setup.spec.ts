import { expect, expectAccessible, serverCspReports, test } from './fixtures'

// The setup wizard (T1b.12) has its own shell and runs under the same CSP. Without the one-time link it explains how to get one.
test('the setup wizard explains the link without a session, with no CSP violations, accessibly', async ({ page, request, cspViolations }) => {
  const response = await page.goto('/setup')

  expect(response?.url()).toMatch(/\/setup\/wizard$/)
  expect(response?.headers()['content-security-policy']).toContain("script-src 'self'")
  await expect(page.getByRole('heading', { level: 1, name: 'Set up agentd' })).toBeVisible()
  await expect(page.getByRole('alert')).toContainText('agentd setup-link')
  await expect(page).toHaveURL(/\/setup\/wizard\/database$/)
  await expectAccessible(page)

  expect(await cspViolations()).toEqual([])
  expect(await serverCspReports(request)).toEqual([])
})
