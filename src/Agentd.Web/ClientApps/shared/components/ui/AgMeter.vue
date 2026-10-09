<script setup lang="ts">
import { MeterIndicator, MeterLabel, MeterRoot, MeterTrack, MeterValue } from 'base-ui-vue'

// `percent`: the value is 0–100 and shows as "35%"; otherwise it shows as a plain number (e.g. agent slots).
withDefaults(defineProps<{ label: string; value: number; max?: number; percent?: boolean }>(), { max: 100, percent: false })
</script>

<template>
  <MeterRoot
    :value="value"
    :max="max"
    :format="percent ? undefined : { maximumFractionDigits: 0 }"
    class="grid grid-cols-[1fr_auto] items-center gap-1 text-xs"
  >
    <MeterLabel class="text-muted">
      {{ label }}
    </MeterLabel>
    <MeterValue class="tabular-nums" />
    <MeterTrack class="col-span-2 h-1.5 overflow-hidden rounded-full bg-base-300">
      <MeterIndicator class="h-full bg-primary transition-[width] motion-reduce:transition-none" />
    </MeterTrack>
  </MeterRoot>
</template>
