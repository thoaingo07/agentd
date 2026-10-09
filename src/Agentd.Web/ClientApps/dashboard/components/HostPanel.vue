<script setup lang="ts">
import { computed, onBeforeUnmount } from 'vue'
import { AgMeter } from '../../shared/components/ui'
import { bytes, clock } from '../../shared/utils/format'
import { useResourcesStore } from '../stores/resources'

// The machine agentd runs on: CPU, load, RAM, swap and every disk (GET /api/resources, refreshed every 10 s).
const resources = useResourcesStore()
onBeforeUnmount(resources.watch())
const m = computed(() => resources.machine)
const d = computed(() => resources.machine?.details ?? null)
/** "16 cores · load 0.77 / 1.43 / 0.90" */
const cpu = computed(() => {
  const l = d.value
  if (!l) return ''
  const load = l.load1 != null ? ` · load ${[l.load1, l.load5, l.load15].map((v) => (v ?? 0).toFixed(2)).join(' / ')}` : ''
  return `${l.cores} cores${load}`
})
const used = (total: number, free: number) => (total > 0 ? Math.round(((total - free) / total) * 100) : 0)

function uptime(seconds: number): string {
  const days = Math.floor(seconds / 86_400)
  const hours = Math.floor((seconds % 86_400) / 3_600)
  return days > 0 ? `${days} d ${hours} h` : `${hours} h ${Math.floor((seconds % 3_600) / 60)} min`
}
</script>

<template>
  <section
    id="host"
    aria-labelledby="host-title"
    class="grid gap-3"
  >
    <h2
      id="host-title"
      class="text-base font-semibold"
    >
      Host
    </h2>
    <p
      v-if="!m"
      class="text-sm text-muted"
    >
      No numbers yet: the daemon samples the machine every few seconds (Linux only).
    </p>
    <template v-else>
      <div
        v-if="m.lowMemory || m.lowDisk"
        class="alert alert-warning text-sm"
        role="status"
      >
        ⚠️ {{ m.lowMemory ? 'Memory' : 'Disk space' }} is running low on this machine.
      </div>
      <div class="grid gap-4 sm:grid-cols-2">
        <div class="grid gap-1">
          <AgMeter
            label="CPU"
            :value="Math.min(100, m.cpuPercent)"
            percent
          />
          <p class="text-xs text-muted tabular-nums">
            {{ cpu }}
          </p>
        </div>
        <div class="grid gap-1">
          <AgMeter
            label="RAM"
            :value="used(m.memoryTotal, m.memoryAvailable)"
            percent
          />
          <p class="text-xs text-muted tabular-nums">
            {{ bytes(m.memoryAvailable) }} free of {{ bytes(m.memoryTotal) }}
          </p>
        </div>
        <div
          v-if="d && d.swapTotal > 0"
          class="grid gap-1"
        >
          <AgMeter
            label="Swap"
            :value="used(d.swapTotal, d.swapFree)"
            percent
          />
          <p class="text-xs text-muted tabular-nums">
            {{ bytes(d.swapTotal - d.swapFree) }} used of {{ bytes(d.swapTotal) }}
          </p>
        </div>
        <div
          v-for="disk in d?.disks.length ? d.disks : [{ mount: 'agentd home', total: m.diskTotal, free: m.diskFree, home: true }]"
          :key="disk.mount"
          class="grid gap-1"
          data-testid="disk"
        >
          <AgMeter
            :label="`Disk ${disk.mount}`"
            :value="used(disk.total, disk.free)"
            percent
          />
          <p class="text-xs text-muted tabular-nums">
            {{ bytes(disk.free) }} free of {{ bytes(disk.total) }}{{ disk.home ? " · agentd's home" : '' }}
          </p>
        </div>
      </div>
      <p class="text-xs text-muted">
        {{ d?.uptimeSeconds != null ? `Up ${uptime(d.uptimeSeconds)} · ` : '' }}updated {{ clock(m.at) }}
      </p>
    </template>
  </section>
</template>
