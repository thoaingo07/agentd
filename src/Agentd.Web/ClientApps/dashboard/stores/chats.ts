import { defineStore } from 'pinia'
import { ref } from 'vue'
import { ApiError, get, send } from '../../shared/api/http'
import type { AgentEvent, ChatDetail, ChatSummary } from '../../shared/api/types'

/** Debounce for re-reading after chat events. */
export const chatRefreshDelayMs = 300
/** While the agent is answering, the open chat is re-read this often (its answer also arrives as an event). */
export const chatThinkingPollMs = 3000

/**
 * Code questions (like `!chat`): the list, the open chat, and asking / following up / closing from the Web UI. Kept
 * fresh from the "all" stream's `chat.message` events (ids only).
 */
export const useChatsStore = defineStore('chats', () => {
  const list = ref<ChatSummary[] | null>(null)
  const current = ref<ChatDetail | null>(null)
  const currentId = ref<number | null>(null)
  const missing = ref(false)
  let timer: ReturnType<typeof setTimeout> | undefined
  let thinking: ReturnType<typeof setTimeout> | undefined

  async function load(): Promise<void> {
    list.value = await get<ChatSummary[]>('/api/chats')
  }

  async function open(id: number): Promise<void> {
    if (currentId.value !== id) current.value = null
    currentId.value = id
    missing.value = false
    try {
      const chat = await get<ChatDetail>(`/api/chats/${id}`)
      if (currentId.value !== id) return
      current.value = chat
      clearTimeout(thinking)
      if (chat.thinking) thinking = setTimeout(() => void open(id).catch(() => {}), chatThinkingPollMs)
    } catch (err) {
      if (err instanceof ApiError && err.status === 404) missing.value = true
      else throw err
    }
  }

  /** Asks a question here (no chat thread); returns the chat's id. */
  async function start(input: { text: string; repo?: string | null; model?: string | null; effort?: string | null }): Promise<number> {
    return (await send<{ id: number }>('POST', '/api/chats', input)).id
  }

  /** A follow-up in the open chat, handled like one in its thread. */
  async function message(text: string): Promise<void> {
    const id = currentId.value
    if (id === null) return
    await send('POST', `/api/chats/${id}/messages`, { text })
    await open(id)
  }

  async function closeChat(): Promise<void> {
    const id = currentId.value
    if (id === null) return
    await send('POST', `/api/chats/${id}/close`)
    await open(id)
  }

  function close(): void {
    clearTimeout(thinking)
    currentId.value = null
    current.value = null
  }

  function apply(evt: AgentEvent): void {
    if (evt.type !== 'chat.message' || timer) return
    const id = Number((evt.payload as Record<string, unknown>).chatId)
    timer = setTimeout(() => {
      timer = undefined
      if (list.value) void load().catch(() => {})
      if (currentId.value === id) void open(id).catch(() => {})
    }, chatRefreshDelayMs)
  }

  return { list, current, currentId, missing, load, open, start, message, closeChat, close, apply }
})
