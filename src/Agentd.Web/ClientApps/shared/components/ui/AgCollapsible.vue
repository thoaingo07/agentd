<script setup lang="ts">
import { CollapsiblePanel, CollapsibleRoot, CollapsibleTrigger } from 'base-ui-vue'
import AgIcon from '../icons/AgIcon.vue'

defineProps<{ title?: string; defaultOpen?: boolean }>()
</script>

<template>
  <CollapsibleRoot
    :default-open="defaultOpen"
    class="rounded-box border border-base-300 bg-base-200"
  >
    <CollapsibleTrigger
      class="group flex w-full items-center gap-2 px-3 py-2 text-left text-sm font-medium focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
    >
      <AgIcon
        name="chevron-down"
        class="-rotate-90 transition-transform group-data-[panel-open]:rotate-0 motion-reduce:transition-none"
      />
      <slot name="title">
        {{ title }}
      </slot>
    </CollapsibleTrigger>
    <CollapsiblePanel
      class="h-[var(--collapsible-panel-height)] overflow-hidden transition-[height] duration-150 ease-out data-[starting-style]:h-0 data-[ending-style]:h-0 motion-reduce:transition-none"
    >
      <div class="border-t border-base-300 px-3 py-2 text-sm">
        <slot />
      </div>
    </CollapsiblePanel>
  </CollapsibleRoot>
</template>
