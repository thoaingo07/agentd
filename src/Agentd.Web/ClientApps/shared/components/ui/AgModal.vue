<script setup lang="ts">
// Not in base-ui-vue: daisyUI modal on a native <dialog> (focus trap, Esc and inert background come from the browser).
import { ref, watch } from 'vue'

defineProps<{ title: string }>()
const open = defineModel<boolean>('open', { default: false })
const dialog = ref<HTMLDialogElement | null>(null)

watch(
  [open, dialog],
  ([isOpen, el]) => {
    if (!el) return
    if (isOpen && !el.open) el.showModal?.()
    if (!isOpen && el.open) el.close?.()
  },
  { immediate: true },
)
</script>

<template>
  <dialog
    ref="dialog"
    class="modal"
    :aria-label="title"
    @close="open = false"
    @cancel.prevent="open = false"
  >
    <div class="modal-box">
      <h2 class="text-base font-semibold">
        {{ title }}
      </h2>
      <div class="py-3 text-sm">
        <slot />
      </div>
      <div class="modal-action">
        <slot name="actions" />
      </div>
    </div>
    <form
      method="dialog"
      class="modal-backdrop"
    >
      <button
        type="submit"
        aria-label="Close"
      >
        close
      </button>
    </form>
  </dialog>
</template>
