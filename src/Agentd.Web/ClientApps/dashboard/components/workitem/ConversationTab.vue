<script setup lang="ts">
// The chat as it happened, both directions, with delivery status. Works after the thread is deleted.
import type { ConversationEntry } from '../../../shared/api/types'
import MarkdownText from '../../../shared/components/MarkdownText.vue'
import { clock } from '../../../shared/utils/format'
import MessageComposer from '../session/MessageComposer.vue'

defineProps<{ entries: ConversationEntry[]; activeJob?: { id: number; state: string } }>()
const statusClass: Record<string, string> = { sent: 'badge-ghost', pending: 'badge-info badge-soft', sending: 'badge-info badge-soft', failed: 'badge-warning', dead: 'badge-error' }
</script>

<template>
  <div class="grid gap-3">
    <p
      v-if="!entries.length"
      class="text-sm text-muted"
    >
      No messages.
    </p>
    <template
      v-for="(e, i) in entries"
      :key="i"
    >
      <div
        v-if="e.direction === 'out'"
        class="chat chat-start"
        :data-direction="e.direction"
      >
        <div class="chat-header flex items-center gap-2 text-xs text-muted">
          agentd · {{ e.provider }} · {{ clock(e.at) }}
          <span
            v-if="e.status"
            class="badge badge-xs"
            :class="statusClass[e.status] ?? 'badge-ghost'"
            :title="e.error ?? undefined"
          >{{ e.status }}</span>
        </div>
        <div
          class="chat-bubble bg-base-200 text-base-content"
          :class="e.kind === 'Question' ? 'border-l-4 border-warning' : ''"
        >
          <MarkdownText :text="e.text" />
        </div>
      </div>
      <div
        v-else
        class="chat chat-end"
        :data-direction="e.direction"
      >
        <div class="chat-header text-xs text-muted">
          {{ e.author ?? 'you' }}<span v-if="e.provider"> · {{ e.provider }}</span> · {{ clock(e.at) }}
        </div>
        <div
          class="chat-bubble bg-primary/15 text-base-content"
          :class="e.kind === 'command' ? 'font-mono text-[13px]' : ''"
        >
          {{ e.text }}
        </div>
      </div>
    </template>
    <MessageComposer
      v-if="activeJob"
      :job-id="activeJob.id"
      :state="activeJob.state"
    />
    <p
      v-else
      class="text-xs text-muted"
    >
      No job of this work item is running; start one with Run work item on the dashboard.
    </p>
  </div>
</template>
