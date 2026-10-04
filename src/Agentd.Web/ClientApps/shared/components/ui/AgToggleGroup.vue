<script setup lang="ts">
import { Toggle, ToggleGroup } from 'base-ui-vue'

defineProps<{ options: { value: string; label: string }[]; label: string }>()
const model = defineModel<string[]>({ required: true })
</script>

<template>
  <ToggleGroup
    :value="model"
    multiple
    :aria-label="label"
    class="join"
    @value-change="(v: string[]) => (model = v)"
  >
    <Toggle
      v-for="option in options"
      :key="option.value"
      :value="option.value"
      class="btn btn-sm join-item btn-ghost data-[pressed]:btn-active data-[pressed]:bg-primary/15 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary"
    >
      {{ option.label }}
    </Toggle>
  </ToggleGroup>
</template>
