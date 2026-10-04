import { defineStore } from 'pinia'
import { ref } from 'vue'
import { get, onUnauthorized, resetXsrf } from '../../shared/api/http'

export interface SessionUser {
  name: string
  roles: string[]
  provider: string
}

/** Who is signed in (/bff/user): the local Admin in Mode None, the Access email behind Cloudflare. */
export const useSessionStore = defineStore('session', () => {
  const user = ref<SessionUser | null>(null)

  async function load(): Promise<void> {
    user.value = await get<SessionUser>('/bff/user')
  }

  function handleUnauthorized(): void {
    resetXsrf()
    // Behind Cloudflare Access an expired sign-in shows up as 401: reloading runs Access's login.
    // In Mode None there is nothing to do (Phase 5 adds a login page).
    if (user.value?.provider === 'cloudflare') window.location.reload()
  }

  onUnauthorized(handleUnauthorized)
  return { user, load, handleUnauthorized }
})
