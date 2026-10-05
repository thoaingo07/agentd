<script setup lang="ts">
import { computed } from 'vue'
import { useConnectionStore } from '../stores/connection'

const connection = useConnectionStore()
const view = computed(
  () =>
    ({
      connecting: { label: 'Connecting…', dot: 'bg-base-300' },
      live: { label: 'Live', dot: 'bg-primary' },
      reconnecting: { label: 'Reconnecting…', dot: 'bg-warning' },
      offline: { label: 'Offline', dot: 'bg-error' },
    })[connection.status],
)
</script>

<template>
  <span
    class="flex items-center gap-2 text-xs text-muted"
    role="status"
    aria-live="polite"
  >
    <span
      class="size-2 rounded-full"
      :class="view.dot"
      aria-hidden="true"
    />
    {{ view.label }}
  </span>
</template>
