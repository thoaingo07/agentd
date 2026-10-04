<script setup lang="ts">
import { TabsIndicator, TabsList, TabsPanel, TabsRoot, TabsTab } from 'base-ui-vue'

export interface AgTab {
  value: string
  label: string
  disabled?: boolean
  /** Keep the panel mounted while hidden (state that must survive a switch, e.g. the transcript). */
  keepMounted?: boolean
}

defineProps<{ tabs: AgTab[] }>()
const model = defineModel<string>({ required: true })
</script>

<template>
  <TabsRoot
    :value="model"
    @value-change="(v: unknown) => (model = String(v))"
  >
    <TabsList class="relative flex gap-1 border-b border-base-300">
      <TabsTab
        v-for="tab in tabs"
        :key="tab.value"
        :value="tab.value"
        :disabled="tab.disabled"
        class="px-3 py-2 text-sm text-muted data-[active]:text-base-content data-[disabled]:opacity-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
      >
        {{ tab.label }}
      </TabsTab>
      <TabsIndicator
        class="absolute bottom-0 left-[var(--active-tab-left)] h-0.5 w-[var(--active-tab-width)] bg-primary transition-[left,width] duration-150 motion-reduce:transition-none"
      />
    </TabsList>
    <TabsPanel
      v-for="tab in tabs"
      :key="tab.value"
      :value="tab.value"
      :keep-mounted="tab.keepMounted"
      class="pt-3 focus-visible:outline-none"
    >
      <slot :name="tab.value" />
    </TabsPanel>
  </TabsRoot>
</template>
