<script setup lang="ts">
import { storeToRefs } from 'pinia'
import { useUiStore, type ThemeChoice } from '../stores/ui'

const ui = useUiStore()
const { theme } = storeToRefs(ui)

const options: { value: ThemeChoice; label: string }[] = [
  { value: 'system', label: 'System' },
  { value: 'agentd', label: 'Light' },
  { value: 'agentd-dark', label: 'Dark' },
]
</script>

<template>
  <div
    class="join"
    role="group"
    aria-label="Theme"
  >
    <button
      v-for="option in options"
      :key="option.value"
      type="button"
      class="btn join-item btn-xs"
      :class="theme === option.value ? 'btn-primary' : 'btn-ghost'"
      :aria-pressed="theme === option.value"
      @click="ui.setTheme(option.value)"
    >
      {{ option.label }}
    </button>
  </div>
</template>
