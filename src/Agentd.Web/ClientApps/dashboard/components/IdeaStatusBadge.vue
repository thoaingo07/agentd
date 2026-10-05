<script setup lang="ts">
const props = defineProps<{ status: string }>()
const styles: Record<string, string> = {
  Brainstorming: 'badge-info',
  Proposed: 'badge-warning',
  Created: 'badge-success',
  Discarded: 'badge-neutral',
  Closed: 'badge-ghost',
}
</script>

<template>
  <span
    class="badge badge-soft badge-sm"
    :class="styles[props.status] ?? 'badge-ghost'"
  >{{ props.status }}</span>
</template>
