<script setup lang="ts">
import MarkdownText from '../../../shared/components/MarkdownText.vue'
import { clock } from '../../../shared/utils/format'
import ToolCallCard from './ToolCallCard.vue'
import type { Row } from './transcript'

defineProps<{ row: Row }>()
</script>

<template>
  <div
    v-if="row.kind === 'text'"
    class="grid gap-1"
  >
    <span class="text-xs font-medium text-brand-ink">Agent</span>
    <MarkdownText :text="row.text" />
  </div>
  <ToolCallCard
    v-else-if="row.kind === 'tool'"
    :call="row.call"
    :result="row.result"
  />
  <div
    v-else-if="row.kind === 'question'"
    class="chat chat-start"
    role="status"
  >
    <div class="chat-bubble border-l-4 border-warning bg-warning/15 text-base-content">
      <MarkdownText :text="row.question" />
      <ol
        v-if="row.options.length"
        class="mt-1 list-decimal pl-5 text-sm"
      >
        <li
          v-for="o in row.options"
          :key="o"
        >
          {{ o }}
        </li>
      </ol>
    </div>
  </div>
  <div
    v-else-if="row.kind === 'reply'"
    class="chat chat-end"
  >
    <div class="chat-header text-xs text-muted">
      {{ row.from }}<span v-if="row.via"> · {{ row.via }}</span>
    </div>
    <div class="chat-bubble bg-primary/15 text-base-content">
      {{ row.reply }}
    </div>
  </div>
  <div
    v-else-if="row.kind === 'error'"
    class="alert alert-error alert-soft text-sm"
    role="alert"
  >
    {{ row.message }}
  </div>
  <div
    v-else-if="row.kind === 'turn'"
    class="text-xs text-muted"
  >
    Turn finished<span v-if="row.turns"> · {{ row.turns }} steps</span> · {{ clock(row.event.ts) }}
  </div>
  <div
    v-else
    class="grid gap-1"
  >
    <div class="divider my-0 text-xs text-muted">
      {{ row.title }} · {{ clock(row.event.ts) }}
    </div>
    <MarkdownText
      v-if="row.detail"
      :text="row.detail"
      class="text-muted"
    />
  </div>
</template>
