import { expect, seed, test } from './fixtures'

// An artifact is agent-written HTML on agentd's own origin: its scripts run, but in a sandbox with an opaque origin,
// so they can't read agentd's cookies or storage, call its API, or send data anywhere.
test('an artifact runs its own script but can\'t reach agentd, and the list links to it', async ({ page, request }) => {
  const url = await seed.artifact(request, 'E2E sandbox probe', `<p id="out">waiting</p>
<script>
  const out = []
  out.push('script:ran')
  try { void document.cookie; out.push('cookie:readable') } catch { out.push('cookie:blocked') }
  try { void localStorage.length; out.push('storage:readable') } catch { out.push('storage:blocked') }
  fetch('/api/config').then(() => out.push('api:reached'), () => out.push('api:blocked'))
    .finally(() => { document.getElementById('out').textContent = out.join(' ') })
</script>`)
  expect(url).toMatch(/^\/artifacts\/e2e-sandbox-probe-[0-9a-f]{8}\.html$/)

  const response = await page.goto(url)
  expect(response?.headers()['content-security-policy']).toMatch(/^sandbox allow-scripts allow-popups/)
  await expect(page.locator('#out')).toHaveText('script:ran cookie:blocked storage:blocked api:blocked')

  await page.goto('/artifacts')
  const link = page.getByRole('link', { name: 'E2E sandbox probe' })
  await expect(link).toHaveAttribute('href', url)
})
