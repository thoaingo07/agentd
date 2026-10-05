import { expect, seed, test } from './fixtures'

test('an unsafe request without the antiforgery token is refused', async ({ request }) => {
  const job = await seed.job(request)

  const res = await request.post(`/api/jobs/${job}/cancel`)

  expect(res.status()).toBe(400)
  expect(((await res.json()) as { code?: string }).code).toBe('antiforgery_invalid')
})

test('the UI sends the token: cancelling from the session page works', async ({ page, request }) => {
  const job = await seed.job(request)
  await page.goto(`/jobs/${job}`)

  await page.getByRole('button', { name: 'Cancel' }).click()
  const [cancel] = await Promise.all([
    page.waitForRequest((r) => r.url().endsWith(`/api/jobs/${job}/cancel`)),
    page.getByRole('button', { name: 'Cancel job' }).click(),
  ])

  expect(cancel.headers()['x-xsrf-token']).toBeTruthy()
  expect((await cancel.response())?.status()).toBe(204)
  await expect(page.locator('header').getByText('Cancelled')).toBeVisible()
})

test('a permission request is answered from the banner', async ({ page, request }) => {
  const job = await seed.job(request)
  await seed.permission(request, job)
  await page.goto(`/jobs/${job}`)

  await page.getByTestId('permission-request').getByRole('button', { name: 'Allow for this job' }).click()

  await expect(page.getByTestId('permission-request')).toHaveCount(0)
  await page.goto('/settings')
  await expect(page.getByTestId('permission-rule').filter({ hasText: `job #${job}` })).toContainText('Bash(npm install:*)')
})
