import { beforeEach, describe, expect, it } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useUiStore } from '../ClientApps/dashboard/stores/ui'

describe('ui store', () => {
  beforeEach(() => {
    window.localStorage.clear()
    document.documentElement.removeAttribute('data-theme')
    setActivePinia(createPinia())
  })

  it('defaults to the system theme', () => {
    expect(useUiStore().theme).toBe('system')
  })

  it('persists and applies an explicit theme', () => {
    const ui = useUiStore()
    ui.setTheme('agentd-dark')

    expect(window.localStorage.getItem('agentd.theme')).toBe('agentd-dark')
    expect(document.documentElement.getAttribute('data-theme')).toBe('agentd-dark')
  })

  it('system removes the stored value and the attribute', () => {
    const ui = useUiStore()
    ui.setTheme('agentd')
    ui.setTheme('system')

    expect(window.localStorage.getItem('agentd.theme')).toBeNull()
    expect(document.documentElement.hasAttribute('data-theme')).toBe(false)
  })

  it('restores the stored theme on startup', () => {
    window.localStorage.setItem('agentd.theme', 'agentd')
    expect(useUiStore().theme).toBe('agentd')
  })
})
