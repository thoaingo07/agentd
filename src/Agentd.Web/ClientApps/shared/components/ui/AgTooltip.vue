<script setup lang="ts">
// Needs one <TooltipProvider> above it (App.vue). The positioner sets coordinates through Vue :style
// bindings (CSSOM), which style-src 'self' allows; no inline <style> or style attribute in markup.
import { TooltipArrow, TooltipPopup, TooltipPortal, TooltipPositioner, TooltipRoot, TooltipTrigger } from 'base-ui-vue'

withDefaults(defineProps<{ content: string; side?: 'top' | 'bottom' | 'left' | 'right' }>(), { side: 'top' })
</script>

<template>
  <TooltipRoot>
    <TooltipTrigger
      as="span"
      class="inline-flex focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
    >
      <slot />
    </TooltipTrigger>
    <TooltipPortal>
      <TooltipPositioner
        :side="side"
        :side-offset="6"
      >
        <TooltipPopup
          class="rounded-field bg-neutral px-2 py-1 text-xs text-neutral-content shadow-sm transition-opacity data-[starting-style]:opacity-0 data-[ending-style]:opacity-0 motion-reduce:transition-none"
        >
          {{ content }}
          <TooltipArrow class="text-neutral" />
        </TooltipPopup>
      </TooltipPositioner>
    </TooltipPortal>
  </TooltipRoot>
</template>
