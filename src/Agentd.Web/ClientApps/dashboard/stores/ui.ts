import { defineStore } from 'pinia'
import { reactive, ref } from 'vue'
import type { ToastItem } from '../../shared/components/ui'

export type ThemeChoice = 'system' | 'agentd' | 'agentd-dark'

/** Transcript filter categories (Session view). */
export type EventCategory = 'text' | 'tools' | 'messages' | 'state' | 'errors'

const storageKey = 'agentd.theme'

function readStored(): ThemeChoice {
  try {
    const value = window.localStorage.getItem(storageKey)
    return value === 'agentd' || value === 'agentd-dark' ? value : 'system'
  } catch {
    return 'system'
  }
}

function apply(choice: ThemeChoice): void {
  const root = document.documentElement
  if (choice === 'system') {
    root.removeAttribute('data-theme')
  } else {
    root.setAttribute('data-theme', choice)
  }
}

/** UI preferences and transient UI state: theme, toasts, per-job transcript filters and follow mode. */
export const useUiStore = defineStore('ui', () => {
  const theme = ref<ThemeChoice>(readStored())
  const toasts = ref<ToastItem[]>([])
  const filters = reactive(new Map<number, Set<EventCategory>>())
  const follow = reactive(new Map<number, boolean>())
  let nextToast = 1

  function setTheme(choice: ThemeChoice): void {
    theme.value = choice
    try {
      if (choice === 'system') {
        window.localStorage.removeItem(storageKey)
      } else {
        window.localStorage.setItem(storageKey, choice)
      }
    } catch {
      // Ignore storage failures; the attribute still applies for this session.
    }
    apply(choice)
  }

  function toast(message: string, kind: ToastItem['kind'] = 'info'): void {
    toasts.value = [...toasts.value.slice(-4), { id: nextToast++, kind, message }]
  }

  function dismiss(id: number): void {
    toasts.value = toasts.value.filter((t) => t.id !== id)
  }

  return { theme, setTheme, toasts, toast, dismiss, filters, follow }
})
