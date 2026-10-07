<script setup lang="ts">
import type { StepCheck } from '../../shared/api/types'

// A Test, Save or Migrate outcome: the server's message, and on failure its one-line fix.
defineProps<{ check: StepCheck | null; error?: string | null }>()
</script>

<template>
  <div
    v-if="check || error"
    role="status"
    class="alert text-sm"
    :class="error || (check && !check.ok) ? 'alert-error' : 'alert-success'"
  >
    <div class="grid gap-1">
      <p>{{ error ?? check?.message }}</p>
      <p
        v-if="!error && check?.fix"
        class="text-xs"
      >
        Fix: {{ check.fix }}
      </p>
    </div>
  </div>
</template>
