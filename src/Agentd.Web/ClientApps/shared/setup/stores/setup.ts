import { defineStore } from 'pinia'
import { ref } from 'vue'
import { ApiError, get, send } from '../../api/http'
import type { AzureDevOpsRequest, AzureDevOpsStep, ChatRequest, ChatStep, ClaudeStep, DatabaseStep, FinishResult, GitKeyStep, ModelsStep, ProfileRequest, RepositoryEntry, RepositoryRequest, ReviewItem, SaveResult, SetupSession, StepCheck, StepModel } from '../../api/types'

/**
 * The wizard's state. Secrets are write-only: they're sent once and never kept here; the server answers with
 * their status ("set · updated … by …") only.
 */
export const useSetupStore = defineStore('setup', () => {
  /** The wizard calls /api/setup (the setup session); the dashboard's Settings call /api/settings (Admins). */
  const base = ref<'/api/setup' | '/api/settings'>('/api/setup')
  /** loading → active (the setup session's cookie works) or missing (no link opened, expired, or setup complete). */
  const session = ref<'loading' | 'active' | 'missing'>('loading')
  const expiresAt = ref<string | null>(null)
  const database = ref<DatabaseStep | null>(null)
  const azureDevOps = ref<AzureDevOpsStep | null>(null)
  const gitKey = ref<GitKeyStep | null>(null)
  const claude = ref<ClaudeStep | null>(null)
  const chat = ref<ChatStep | null>(null)
  const repositories = ref<RepositoryEntry[] | null>(null)
  const models = ref<ModelsStep | null>(null)
  const review = ref<ReviewItem[] | null>(null)
  const completedAt = ref<string | null>(null)
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
    database.value = await get<DatabaseStep>(`${base.value}/database`)
  }

  async function saveDatabase(connectionString: string): Promise<SaveResult> {
    const result = await send<SaveResult>('PUT', `${base.value}/database`, { connectionString })
    await loadDatabase()
    return saved(result)
  }

  /** Tests the given connection string, or the saved one when it's empty. Nothing is saved. */
  function testDatabase(connectionString?: string): Promise<StepCheck> {
    return send<StepCheck>('POST', `${base.value}/database/test`, { connectionString: connectionString?.trim() || null })
  }

  function migrateDatabase(): Promise<StepCheck> {
    return send<StepCheck>('POST', `${base.value}/database/migrate`)
  }

  async function loadAzureDevOps(): Promise<void> {
    azureDevOps.value = await get<AzureDevOpsStep>(`${base.value}/azure-devops`)
  }

  async function saveAzureDevOps(input: AzureDevOpsRequest): Promise<SaveResult> {
    const result = await send<SaveResult>('PUT', `${base.value}/azure-devops`, { ...input, pat: input.pat?.trim() || null, clientSecret: input.clientSecret?.trim() || null })
    await loadAzureDevOps()
    return saved(result)
  }

  /** Tests the given values (an empty PAT or client secret: the saved one). Nothing is saved. */
  function testAzureDevOps(input: AzureDevOpsRequest): Promise<StepCheck> {
    return send<StepCheck>('POST', `${base.value}/azure-devops/test`, { ...input, pat: input.pat?.trim() || null, clientSecret: input.clientSecret?.trim() || null })
  }

  async function loadGitKey(): Promise<void> {
    gitKey.value = await get<GitKeyStep>(`${base.value}/git-key`)
  }

  /** Creates agentd's SSH key (once; it's never replaced from here). Takes effect at once, no restart. */
  async function generateGitKey(): Promise<void> {
    gitKey.value = await send<GitKeyStep>('POST', `${base.value}/git-key`)
  }

  function testGitAccess(url: string): Promise<StepCheck> {
    return send<StepCheck>('POST', `${base.value}/git-key/test`, { url })
  }

  async function loadClaude(): Promise<void> {
    claude.value = await get<ClaudeStep>(`${base.value}/claude`)
  }

  async function saveClaudeToken(token: string): Promise<SaveResult> {
    const result = await send<SaveResult>('PUT', `${base.value}/claude`, { token })
    await loadClaude()
    return saved(result)
  }

  async function removeClaudeToken(): Promise<SaveResult> {
    const result = await send<SaveResult>('DELETE', `${base.value}/claude/token`)
    await loadClaude()
    return saved(result)
  }

  /** A tiny real prompt with the given token, else the saved one, else the server's login. Nothing is saved. */
  function testClaude(token?: string): Promise<StepCheck> {
    return send<StepCheck>('POST', `${base.value}/claude/test`, { token: token?.trim() || null })
  }

  async function loadChat(): Promise<void> {
    chat.value = await get<ChatStep>(`${base.value}/chat`)
  }

  async function saveChat(input: ChatRequest): Promise<SaveResult> {
    const result = await send<SaveResult>('PUT', `${base.value}/chat`, { ...input, botToken: input.botToken?.trim() || null })
    await loadChat()
    return saved(result)
  }

  /** Posts a test message in the channel (an empty token: the saved one). Nothing is saved. */
  function testChat(input: ChatRequest): Promise<StepCheck> {
    return send<StepCheck>('POST', `${base.value}/chat/test`, { ...input, botToken: input.botToken?.trim() || null })
  }

  async function loadRepositories(): Promise<void> {
    repositories.value = await get<RepositoryEntry[]>(`${base.value}/repositories`)
  }

  /** Adds it to agentd.json after reaching it; the daemon clones it at its next start. */
  async function addRepository(input: RepositoryRequest): Promise<RepositoryEntry> {
    const added = await send<RepositoryEntry>('POST', `${base.value}/repositories`, input)
    await loadRepositories()
    restartRequired.value = true
    return added
  }

  function testRepository(url: string): Promise<StepCheck> {
    return send<StepCheck>('POST', `${base.value}/repositories/test`, { url })
  }

  async function loadModels(): Promise<void> {
    models.value = await get<ModelsStep>(`${base.value}/models`)
  }

  /** Adds or changes a provider; an empty key keeps the saved one (it's required the first time). */
  async function saveProfile(input: ProfileRequest): Promise<SaveResult> {
    const result = await send<SaveResult>('PUT', `${base.value}/models/profiles`, { ...input, apiKey: input.apiKey?.trim() || null })
    await loadModels()
    return saved(result)
  }

  async function removeProfile(name: string): Promise<SaveResult> {
    const result = await send<SaveResult>('DELETE', `${base.value}/models/profiles/${encodeURIComponent(name)}`)
    await loadModels()
    return saved(result)
  }

  /** One test prompt through the provider (an empty key: the saved one). Nothing is saved. */
  function testProfile(input: ProfileRequest): Promise<StepCheck> {
    return send<StepCheck>('POST', `${base.value}/models/profiles/test`, { ...input, apiKey: input.apiKey?.trim() || null })
  }

  /** Every step's model, effort and provider; empty values go back to the defaults. */
  async function saveSteps(steps: StepModel[]): Promise<SaveResult> {
    const result = await send<SaveResult>('PUT', `${base.value}/models/steps`, steps)
    await loadModels()
    return saved(result)
  }

  /** Every step's own check against the saved settings (it runs live tests, so it takes a few seconds). */
  async function loadReview(): Promise<void> {
    review.value = null
    review.value = await get<ReviewItem[]>(`${base.value}/review`)
  }

  /** Marks setup complete and kills the setup link and this session. */
  async function finish(): Promise<void> {
    completedAt.value = (await send<FinishResult>('POST', `${base.value}/finish`)).completedAt
    restartRequired.value = true
  }

  return {
    base, session, expiresAt, database, azureDevOps, gitKey, claude, chat, repositories, models, review, completedAt, restartRequired,
    loadSession, loadDatabase, saveDatabase, testDatabase, migrateDatabase, loadAzureDevOps, saveAzureDevOps, testAzureDevOps,
    loadGitKey, generateGitKey, testGitAccess, loadClaude, saveClaudeToken, removeClaudeToken, testClaude,
    loadChat, saveChat, testChat, loadRepositories, addRepository, testRepository,
    loadModels, saveProfile, removeProfile, testProfile, saveSteps, loadReview, finish,
  }
})
