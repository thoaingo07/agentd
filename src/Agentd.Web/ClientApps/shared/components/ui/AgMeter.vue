<script setup lang="ts">
import { MeterIndicator, MeterLabel, MeterRoot, MeterTrack, MeterValue } from 'base-ui-vue'

withDefaults(defineProps<{ label: string; value: number; max?: number }>(), { max: 100 })
</script>

<template>
  <MeterRoot
    :value="value"
    :max="max"
    :format="{ maximumFractionDigits: 0 }"
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
