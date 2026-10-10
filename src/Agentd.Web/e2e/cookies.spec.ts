import { expect, seed, test } from './fixtures'

test('no cookie is readable from JavaScript, and every agentd cookie is HttpOnly and SameSite=Strict', async ({ page, request, context }) => {
  const job = await seed.job(request)
  await page.goto(`/jobs/${job}`)
  await page.getByRole('button', { name: 'Cancel' }).click()
  await page.getByRole('button', { name: 'Cancel job' }).click()   // an unsafe request: the antiforgery cookie is set by now
  await expect(page.locator('header').getByText('Cancelled', { exact: true })).toBeVisible()

  expect(await page.evaluate(() => document.cookie)).toBe('')
  const cookies = await context.cookies()
  expect(cookies.length).toBeGreaterThan(0)
  for (const c of cookies) expect({ name: c.name, httpOnly: c.httpOnly, sameSite: c.sameSite }).toEqual({ name: c.name, httpOnly: true, sameSite: 'Strict' })
})
