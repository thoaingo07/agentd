<script setup lang="ts">
const props = defineProps<{ status: string }>()
const styles: Record<string, string> = {
  Open: 'badge-error',
  Investigating: 'badge-info',
  Fixing: 'badge-warning',
  Resolved: 'badge-success',
  Ignored: 'badge-ghost',
}
</script>

<template>
  <span
    class="badge badge-soft badge-sm"
    :class="styles[props.status] ?? 'badge-ghost'"
  >{{ props.status }}</span>
</template>
