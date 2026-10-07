import { defineStore } from 'pinia'
import { ref } from 'vue'
import { ApiError, get, send } from '../../shared/api/http'
import type { AzureDevOpsRequest, AzureDevOpsStep, ChatRequest, ChatStep, ClaudeStep, DatabaseStep, GitKeyStep, SaveResult, SetupSession, StepCheck } from '../../shared/api/types'

/**
 * The wizard's state. Secrets are write-only: they're sent once and never kept here; the server answers with
 * their status ("set · updated … by …") only.
 */
export const useSetupStore = defineStore('setup', () => {
  /** loading → active (the setup session's cookie works) or missing (no link opened, expired, or setup complete). */
  const session = ref<'loading' | 'active' | 'missing'>('loading')
  const expiresAt = ref<string | null>(null)
  const database = ref<DatabaseStep | null>(null)
  const azureDevOps = ref<AzureDevOpsStep | null>(null)
  const gitKey = ref<GitKeyStep | null>(null)
  const claude = ref<ClaudeStep | null>(null)
  const chat = ref<ChatStep | null>(null)
  /** A saved step only applies after `agentd daemon restart`. */
  const restartRequired = ref(false)

  async function loadSession(): Promise<void> {
    try {
      expiresAt.value = (await get<SetupSession>('/api/setup/session')).expiresAt ?? null
      session.value = 'active'
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) {
        session.value = 'missing'
        return
      }
      throw err
    }
  }

  function saved(result: SaveResult): SaveResult {
    restartRequired.value ||= result.restartRequired
    return result
  }

  async function loadDatabase(): Promise<void> {
    database.value = await get<DatabaseStep>('/api/setup/database')
  }

  async function saveDatabase(connectionString: string): Promise<SaveResult> {
    const result = await send<SaveResult>('PUT', '/api/setup/database', { connectionString })
    await loadDatabase()
    return saved(result)
  }

  /** Tests the given connection string, or the saved one when it's empty. Nothing is saved. */
  function testDatabase(connectionString?: string): Promise<StepCheck> {
    return send<StepCheck>('POST', '/api/setup/database/test', { connectionString: connectionString?.trim() || null })
  }

  function migrateDatabase(): Promise<StepCheck> {
    return send<StepCheck>('POST', '/api/setup/database/migrate')
  }

  async function loadAzureDevOps(): Promise<void> {
    azureDevOps.value = await get<AzureDevOpsStep>('/api/setup/azure-devops')
  }

  async function saveAzureDevOps(input: AzureDevOpsRequest): Promise<SaveResult> {
    const result = await send<SaveResult>('PUT', '/api/setup/azure-devops', { ...input, pat: input.pat?.trim() || null })
    await loadAzureDevOps()
    return saved(result)
  }

  /** Tests the given values (an empty PAT: the saved one). Nothing is saved. */
  function testAzureDevOps(input: AzureDevOpsRequest): Promise<StepCheck> {
    return send<StepCheck>('POST', '/api/setup/azure-devops/test', { ...input, pat: input.pat?.trim() || null })
  }

  async function loadGitKey(): Promise<void> {
    gitKey.value = await get<GitKeyStep>('/api/setup/git-key')
  }

  /** Creates agentd's SSH key (once; it's never replaced from here). Takes effect at once, no restart. */
  async function generateGitKey(): Promise<void> {
    gitKey.value = await send<GitKeyStep>('POST', '/api/setup/git-key')
  }

  function testGitAccess(url: string): Promise<StepCheck> {
    return send<StepCheck>('POST', '/api/setup/git-key/test', { url })
  }

  async function loadClaude(): Promise<void> {
    claude.value = await get<ClaudeStep>('/api/setup/claude')
  }

  async function saveClaudeToken(token: string): Promise<SaveResult> {
    const result = await send<SaveResult>('PUT', '/api/setup/claude', { token })
    await loadClaude()
    return saved(result)
  }

  async function removeClaudeToken(): Promise<SaveResult> {
    const result = await send<SaveResult>('DELETE', '/api/setup/claude/token')
    await loadClaude()
    return saved(result)
  }

  /** A tiny real prompt with the given token, else the saved one, else the server's login. Nothing is saved. */
  function testClaude(token?: string): Promise<StepCheck> {
    return send<StepCheck>('POST', '/api/setup/claude/test', { token: token?.trim() || null })
  }

  async function loadChat(): Promise<void> {
    chat.value = await get<ChatStep>('/api/setup/chat')
  }

  async function saveChat(input: ChatRequest): Promise<SaveResult> {
    const result = await send<SaveResult>('PUT', '/api/setup/chat', { ...input, botToken: input.botToken?.trim() || null })
    await loadChat()
    return saved(result)
  }

  /** Posts a test message in the channel (an empty token: the saved one). Nothing is saved. */
  function testChat(input: ChatRequest): Promise<StepCheck> {
    return send<StepCheck>('POST', '/api/setup/chat/test', { ...input, botToken: input.botToken?.trim() || null })
  }

  return {
    session, expiresAt, database, azureDevOps, gitKey, claude, chat, restartRequired,
    loadSession, loadDatabase, saveDatabase, testDatabase, migrateDatabase, loadAzureDevOps, saveAzureDevOps, testAzureDevOps,
    loadGitKey, generateGitKey, testGitAccess, loadClaude, saveClaudeToken, removeClaudeToken, testClaude,
    loadChat, saveChat, testChat,
  }
})
