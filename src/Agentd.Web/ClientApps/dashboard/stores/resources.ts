import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { get } from '../../shared/api/http'
import type { JobResources, MachineResources, Resources } from '../../shared/api/types'

/** The sampler runs every 5 s; the page refreshes every 10 s while something shows the numbers. */
export const resourcesPollMs = 10_000

/**
 * CPU, RAM and disk use: the machine and each running job (GET /api/resources). Polled only while at least one
 * component holds it (`watch()` returns the matching `unwatch`), so a hidden page costs nothing.
 */
export const useResourcesStore = defineStore('resources', () => {
  const machine = ref<MachineResources | null>(null)
  const jobs = ref<JobResources[]>([])
  let watchers = 0
  let timer: ReturnType<typeof setInterval> | undefined

  const byJob = computed(() => new Map(jobs.value.map((j) => [j.jobId, j])))

  async function load(): Promise<void> {
    const r = await get<Resources>('/api/resources')
    machine.value = r.machine ?? null
    jobs.value = r.jobs
  }

  function watch(): () => void {
    if (watchers++ === 0) {
      void load().catch(() => {})
      timer = setInterval(() => void load().catch(() => {}), resourcesPollMs)
    }

    let stopped = false
    return () => {
      if (stopped) return
      stopped = true
      if (--watchers === 0) clearInterval(timer)
    }
  }

  return { machine, jobs, byJob, load, watch }
})
