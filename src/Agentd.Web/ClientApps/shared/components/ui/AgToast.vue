<script setup lang="ts">
// Not in base-ui-vue: daisyUI alert. Auto-dismisses after `timeout` ms (0 = stays).
import { onBeforeUnmount, onMounted } from 'vue'
import AgIcon from '../icons/AgIcon.vue'

export interface ToastItem {
  id: number
  kind: 'info' | 'success' | 'warning' | 'error'
  message: string
}

const props = withDefaults(defineProps<{ toast: ToastItem; timeout?: number }>(), { timeout: 5000 })
const emit = defineEmits<{ dismiss: [id: number] }>()
let timer: ReturnType<typeof setTimeout> | undefined

onMounted(() => {
  if (props.timeout > 0) timer = setTimeout(() => emit('dismiss', props.toast.id), props.timeout)
})
onBeforeUnmount(() => clearTimeout(timer))
</script>

<template>
  <div
    class="alert alert-soft text-sm"
    :class="`alert-${toast.kind}`"
    :role="toast.kind === 'error' ? 'alert' : 'status'"
  >
    <span>{{ toast.message }}</span>
    <button
      type="button"
      class="btn btn-ghost btn-xs"
      aria-label="Dismiss"
      @click="emit('dismiss', toast.id)"
    >
      <AgIcon name="x" />
    </button>
  </div>
</template>
