<script setup lang="ts">
// Design system §2.3: every state has a label and an icon, never color alone.
import { computed } from 'vue'
import type { JobState } from '../../api/types'
import AgIcon, { type IconName } from '../icons/AgIcon.vue'

const props = defineProps<{ state: JobState }>()

const map: Record<JobState, { cls: string; label: string; icon: IconName | 'dot' }> = {
  Queued: { cls: 'badge-ghost', label: 'Queued', icon: 'clock' },
  Preparing: { cls: 'badge-info badge-soft', label: 'Preparing', icon: 'folder-plus' },
  Running: { cls: 'badge-primary', label: 'Running', icon: 'dot' },
  WaitingForHuman: { cls: 'badge-warning', label: 'Waiting for you', icon: 'message-question' },
  Publishing: { cls: 'badge-accent badge-soft', label: 'Publishing', icon: 'git-pull-request' },
  InReview: { cls: 'badge-accent badge-soft', label: 'In review', icon: 'eye' },
  Done: { cls: 'badge-success', label: 'Done', icon: 'check' },
  Failed: { cls: 'badge-error', label: 'Failed', icon: 'x-octagon' },
  Paused: { cls: 'badge-neutral badge-soft', label: 'Paused', icon: 'pause' },
  Cancelled: { cls: 'badge-ghost text-muted', label: 'Cancelled', icon: 'slash' },
}
const view = computed(() => map[props.state])
</script>

<template>
  <span
    class="badge badge-sm gap-1 whitespace-nowrap"
    :class="view.cls"
    :data-state="state"
  >
    <span
      v-if="view.icon === 'dot'"
      class="size-2 rounded-full bg-current animate-pulse motion-reduce:animate-none"
      aria-hidden="true"
    />
    <AgIcon
      v-else
      :name="view.icon"
      :size="12"
    />
    {{ view.label }}
  </span>
</template>
