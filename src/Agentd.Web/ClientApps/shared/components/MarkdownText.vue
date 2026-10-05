<script setup lang="ts">
import { computed } from 'vue'
import { renderMarkdown } from '../utils/markdown-lite'

const props = defineProps<{ text: string }>()
const Blocks = () => renderMarkdown(props.text)
const empty = computed(() => props.text.trim() === '')
</script>

<template>
  <div
    v-if="!empty"
    class="grid gap-2 text-sm leading-6 break-words"
  >
    <Blocks />
  </div>
</template>
