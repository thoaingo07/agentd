<script setup lang="ts">
import { Button } from 'base-ui-vue'

withDefaults(
  defineProps<{
    variant?: 'primary' | 'ghost' | 'outline' | 'error'
    size?: 'sm' | 'md'
    loading?: boolean
    disabled?: boolean
    type?: 'button' | 'submit'
  }>(),
  { variant: 'primary', size: 'md', loading: false, disabled: false, type: 'button' },
)
</script>

<template>
  <!-- While loading the button stays focusable (screen readers keep their place) but can't be pressed. -->
  <Button
    :type="type"
    :disabled="disabled || loading"
    :focusable-when-disabled="loading"
    :aria-busy="loading || undefined"
    class="btn focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary data-[disabled]:cursor-not-allowed"
    :class="[`btn-${variant}`, size === 'sm' ? 'btn-sm' : 'btn-md']"
  >
    <span
      v-if="loading"
      class="loading loading-spinner loading-xs"
      aria-hidden="true"
    />
    <slot />
  </Button>
</template>
