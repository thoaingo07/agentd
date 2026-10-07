import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { setAntiforgeryUrl } from '../ClientApps/shared/api/http'
import { useSetupStore } from '../ClientApps/setup/stores/setup'
import SecretField from '../ClientApps/setup/components/SecretField.vue'
import DatabaseStep from '../ClientApps/setup/steps/DatabaseStep.vue'
import AzureDevOpsStep from '../ClientApps/setup/steps/AzureDevOpsStep.vue'
import GitKeyStep from '../ClientApps/setup/steps/GitKeyStep.vue'
import ClaudeStep from '../ClientApps/setup/steps/ClaudeStep.vue'
import ChatStep from '../ClientApps/setup/steps/ChatStep.vue'
import App from '../ClientApps/setup/App.vue'

const unset = { set: false, updatedAt: null, updatedBy: null }
const set = { set: true, updatedAt: '2026-10-07T10:00:00Z', updatedBy: 'setup' }

type Call = { method: string; path: string; body: unknown; xsrf: string | null }
let calls: Call[]
let routes: Record<string, () => Response>
const json = (body: unknown, status = 200) => () => new Response(JSON.stringify(body), { status })

beforeEach(() => {
  setActivePinia(createPinia())
  setAntiforgeryUrl('/api/setup/antiforgery')
  calls = []
  routes = {
    'GET /api/setup/antiforgery': json({ token: 'setup-xsrf' }),
    'GET /api/setup/session': json({ expiresAt: '2026-10-07T10:30:00Z' }),
    'GET /api/setup/database': json({ connectionString: unset }),
    'PUT /api/setup/database': json({ restartRequired: true }),
    'POST /api/setup/database/test': json({ ok: false, message: 'password authentication failed', fix: 'check the password' }),
    'POST /api/setup/database/migrate': json({ ok: true, message: 'Applied 17 migration(s).', fix: null }),
    'GET /api/setup/azure-devops': json({ organization: 'myorg', project: 'Portal', auth: 'Pat', pat: set }),
    'PUT /api/setup/azure-devops': json({ restartRequired: true }),
    'POST /api/setup/azure-devops/test': json({ ok: true, message: 'Signed in to myorg/Portal.', fix: null }),
    'GET /api/setup/git-key': json({ exists: false, publicKey: null, fingerprint: null, path: null }),
    'POST /api/setup/git-key': json({ exists: true, publicKey: 'ssh-ed25519 AAAAC3 agentd@vps', fingerprint: 'SHA256:abc', path: '/home/a/.agentd/ssh/id_ed25519' }),
    'GET /api/setup/claude': json({ token: unset, server: { installed: true, version: '2.1.300 (Claude Code)', loggedIn: false, method: null, plan: null } }),
    'PUT /api/setup/claude': json({ restartRequired: true }),
    'DELETE /api/setup/claude/token': json({ restartRequired: true }),
    'POST /api/setup/claude/test': json({ ok: true, message: 'Claude answered a test prompt in 2.1 s (the token).', fix: null }),
    'GET /api/setup/chat': json({ enabled: false, guildId: null, channelId: null, botToken: unset, users: [] }),
    'PUT /api/setup/chat': json({ restartRequired: true }),
    'POST /api/setup/chat/test': json({ ok: true, message: 'agentd posted a test message in #agentd.', fix: null }),
    'POST /api/setup/git-key/test': json({ ok: false, message: 'Permission denied (publickey).', fix: 'add the public key to Azure DevOps' }),
  }
  vi.stubGlobal('fetch', vi.fn(async (req: Request) => {
    const path = new URL(req.url).pathname
    const body = req.method === 'GET' ? undefined : await req.clone().text()
    calls.push({ method: req.method, path, body: body ? JSON.parse(body) : undefined, xsrf: req.headers.get('X-XSRF-TOKEN') })
    const route = routes[`${req.method} ${path}`]
    return route ? route() : new Response(JSON.stringify({ title: 'Not found' }), { status: 404 })
  }))
})
afterEach(() => vi.unstubAllGlobals())

const router = () => createRouter({ history: createMemoryHistory(), routes: [{ path: '/:p(.*)*', component: { template: '<div />' } }] })

describe('setup store', () => {
  it('knows when there is no setup session', async () => {
    routes['GET /api/setup/session'] = json({ title: 'Unauthorized' }, 401)
    const setup = useSetupStore()

    await setup.loadSession()

    expect(setup.session).toBe('missing')
  })

  it('sends changes with the setup session antiforgery token and keeps no secret', async () => {
    const setup = useSetupStore()

    await setup.saveDatabase('Host=db;Password=pw-SECRET')

    const put = calls.find((c) => c.method === 'PUT')!
    expect(put).toMatchObject({ path: '/api/setup/database', body: { connectionString: 'Host=db;Password=pw-SECRET' }, xsrf: 'setup-xsrf' })
    expect(calls.some((c) => c.path === '/bff/antiforgery')).toBe(false)
    expect(setup.restartRequired).toBe(true)
    expect(JSON.stringify(setup.$state)).not.toContain('SECRET')
  })

  it('tests the saved connection string when the field is empty', async () => {
    await useSetupStore().testDatabase('  ')

    expect(calls.find((c) => c.path === '/api/setup/database/test')?.body).toEqual({ connectionString: null })
  })
})

describe('SecretField', () => {
  it('shows only the status of a set secret until Replace', async () => {
    const field = mount(SecretField, { props: { label: 'Token', status: set, modelValue: '' } })

    expect(field.find('input').exists()).toBe(false)
    expect(field.text()).toContain('Set')
    expect(field.text()).toContain('by setup')
    await field.get('button').trigger('click')
    expect(field.find('input[type="password"]').exists()).toBe(true)
  })

  it('is a password input when nothing is set', () => {
    const field = mount(SecretField, { props: { label: 'Token', status: unset, modelValue: '' } })

    expect(field.get('input').attributes('type')).toBe('password')
    expect(field.get('label').attributes('for')).toBe(field.get('input').attributes('id'))
  })
})

describe('steps', () => {
  it('database: Test shows the failure and its fix; Save clears the field', async () => {
    const step = mount(DatabaseStep, { global: { plugins: [router()] } })
    await flushPromises()

    await step.findAll('button').find((b) => b.text() === 'Test')!.trigger('click')
    await flushPromises()
    expect(step.text()).toContain('password authentication failed')
    expect(step.text()).toContain('Fix: check the password')

    await step.get('input').setValue('Host=db;Password=pw-SECRET')
    await step.findAll('button').find((b) => b.text() === 'Save')!.trigger('click')
    await flushPromises()
    expect(step.text()).toContain('Connection string saved.')
    expect(calls.some((c) => c.method === 'PUT' && c.path === '/api/setup/database')).toBe(true)
  })

  it('azure devops: prefills the saved settings and hides the token for az login', async () => {
    const step = mount(AzureDevOpsStep, { global: { plugins: [router()] } })
    await flushPromises()

    const [organization, project] = step.findAll('input:not([type="radio"])')
    expect((organization!.element as HTMLInputElement).value).toBe('myorg')
    expect((project!.element as HTMLInputElement).value).toBe('Portal')
    expect(step.text()).toContain('Personal access token')

    await step.get('input[value="AzCli"]').setValue(true)
    expect(step.text()).not.toContain('Personal access token')
    await step.findAll('button').find((b) => b.text() === 'Test')!.trigger('click')
    await flushPromises()
    expect(calls.find((c) => c.path === '/api/setup/azure-devops/test')?.body).toEqual({ organization: 'myorg', project: 'Portal', auth: 'AzCli', pat: null })
    expect(step.text()).toContain('Signed in to myorg/Portal.')
  })

  it('git: generates the key, shows the public half with a link to add it, and tests a clone URL', async () => {
    const step = mount(GitKeyStep, { global: { plugins: [router()] } })
    await flushPromises()

    await step.findAll('button').find((b) => b.text().includes('Generate'))!.trigger('click')
    await flushPromises()
    expect((step.get('textarea').element as HTMLTextAreaElement).value).toBe('ssh-ed25519 AAAAC3 agentd@vps')
    expect(step.text()).toContain('SHA256:abc')
    expect(step.find('a[href="https://dev.azure.com/myorg/_usersSettings/keys"]').exists()).toBe(true)

    await step.get('input').setValue('git@ssh.dev.azure.com:v3/myorg/Portal/sysmin')
    await step.findAll('button').find((b) => b.text() === 'Test')!.trigger('click')
    await flushPromises()
    expect(calls.find((c) => c.path === '/api/setup/git-key/test')?.body).toEqual({ url: 'git@ssh.dev.azure.com:v3/myorg/Portal/sysmin' })
    expect(step.text()).toContain('Permission denied (publickey).')
    expect(step.text()).toContain('Fix: add the public key to Azure DevOps')
  })

  it('claude: shows the server, tests and saves a token, then offers to remove it', async () => {
    const step = mount(ClaudeStep, { global: { plugins: [router()] } })
    await flushPromises()
    expect(step.text()).toContain('2.1.300 (Claude Code)')
    expect(step.text()).toContain('not logged in')

    await step.get('input').setValue('sk-ant-oat01-SECRET-0123456789')
    await step.findAll('button').find((b) => b.text() === 'Test')!.trigger('click')
    await flushPromises()
    expect(calls.find((c) => c.path === '/api/setup/claude/test')?.body).toEqual({ token: 'sk-ant-oat01-SECRET-0123456789' })
    expect(step.text()).toContain('Claude answered a test prompt')

    routes['GET /api/setup/claude'] = json({ token: set, server: { installed: true, version: '2.1.300 (Claude Code)', loggedIn: false, method: null, plan: null } })
    await step.findAll('button').find((b) => b.text() === 'Save')!.trigger('click')
    await flushPromises()
    expect(step.text()).toContain('Token saved.')
    expect(step.find('input').exists()).toBe(false)
    expect(step.findAll('button').some((b) => b.text() === 'Remove the token')).toBe(true)
    expect(JSON.stringify(useSetupStore().$state)).not.toContain('SECRET')
  })

  it('chat: sends a test message and saves you as a user; off hides the Discord fields', async () => {
    const step = mount(ChatStep, { global: { plugins: [router()] } })
    await flushPromises()
    const inputs = step.findAll('input:not([type="checkbox"])')
    await inputs[0]!.setValue('bot-SECRET')
    await inputs[1]!.setValue('770517485715193877')
    await inputs[2]!.setValue('1555955347544608809')
    await inputs[3]!.setValue('tngo')
    await inputs[4]!.setValue('710392908099878953')

    await step.findAll('button').find((b) => b.text() === 'Send a test message')!.trigger('click')
    await flushPromises()
    expect(step.text()).toContain('agentd posted a test message in #agentd.')
    await step.findAll('button').find((b) => b.text() === 'Save')!.trigger('click')
    await flushPromises()
    expect(calls.find((c) => c.method === 'PUT' && c.path === '/api/setup/chat')?.body).toEqual({
      enabled: true, guildId: '770517485715193877', channelId: '1555955347544608809', botToken: 'bot-SECRET', userName: 'tngo', userDiscordId: '710392908099878953',
    })
    expect(JSON.stringify(useSetupStore().$state)).not.toContain('SECRET')

    await step.get('input[type="checkbox"]').setValue(false)
    expect(step.text()).not.toContain('Bot token')
    expect(step.findAll('button').some((b) => b.text() === 'Send a test message')).toBe(false)
  })

  it('without a session the wizard explains how to get the link', async () => {
    routes['GET /api/setup/session'] = json({ title: 'Unauthorized' }, 401)
    const app = mount(App, { global: { plugins: [router()] } })
    await flushPromises()

    expect(app.text()).toContain('agentd setup-link')
    expect(app.find('nav').exists()).toBe(false)
  })
})
