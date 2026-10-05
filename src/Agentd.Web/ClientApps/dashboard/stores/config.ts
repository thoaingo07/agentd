import { defineStore } from 'pinia'
import { ref } from 'vue'
import { get } from '../../shared/api/http'
import type { Config } from '../../shared/api/types'

/** The read-only settings summary (/api/config), loaded once. */
export const useConfigStore = defineStore('config', () => {
  const config = ref<Config | null>(null)

  async function load(): Promise<Config> {
    config.value ??= await get<Config>('/api/config')
    return config.value
  }

  /** The Azure DevOps link for a work item in a registered repository. */
  function workItemUrl(repo: string, workItemId: number): string | null {
    const r = config.value?.repositories.find((x) => x.name === repo)
    return r ? `https://dev.azure.com/${encodeURIComponent(r.organization)}/${encodeURIComponent(r.project)}/_workitems/edit/${workItemId}` : null
  }

  return { config, load, workItemUrl }
})
