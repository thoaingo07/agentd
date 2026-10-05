import { defineStore } from 'pinia'
import { ref } from 'vue'
import { ApiError, get, send } from '../../shared/api/http'
import type { PermissionRule } from '../../shared/api/types'
import { useUiStore } from './ui'

/** Remembered approvals ("allow for this job", "always allow in this repo"), for the Settings page. Admins revoke them. */
export const usePermissionsStore = defineStore('permissions', () => {
  const rules = ref<PermissionRule[]>([])
  const revoking = ref(new Set<number>())

  async function load(): Promise<void> {
    rules.value = await get<PermissionRule[]>('/api/permissions/rules')
  }

  async function revoke(id: number): Promise<void> {
    revoking.value = new Set(revoking.value).add(id)
    try {
      await send('DELETE', `/api/permissions/rules/${id}`)
      rules.value = rules.value.filter((r) => r.id !== id)
    } catch (err) {
      // 404: already revoked elsewhere; drop it from the list too.
      if (err instanceof ApiError && err.status === 404) rules.value = rules.value.filter((r) => r.id !== id)
      else useUiStore().toast(err instanceof ApiError ? err.message : `Couldn't revoke rule ${id}.`, 'error')
    } finally {
      const next = new Set(revoking.value)
      next.delete(id)
      revoking.value = next
    }
  }

  return { rules, revoking, load, revoke }
})
