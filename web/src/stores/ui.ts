import { defineStore } from 'pinia'
import { ref } from 'vue'

export type ThemeChoice = 'system' | 'agentd' | 'agentd-dark'

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

/** UI preferences: currently the theme (System / Light / Dark). */
export const useUiStore = defineStore('ui', () => {
  const theme = ref<ThemeChoice>(readStored())

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

  return { theme, setTheme }
})
