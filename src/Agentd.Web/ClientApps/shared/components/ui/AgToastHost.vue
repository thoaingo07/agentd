<script setup lang="ts">
import AgToast, { type ToastItem } from './AgToast.vue'

defineProps<{ toasts: ToastItem[] }>()
const emit = defineEmits<{ dismiss: [id: number] }>()
</script>

<template>
  <div
    class="toast toast-end toast-bottom z-50"
    aria-live="polite"
  >
    <AgToast
      v-for="t in toasts"
      :key="t.id"
      :toast="t"
      @dismiss="emit('dismiss', $event)"
    />
  </div>
</template>
