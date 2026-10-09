<script setup lang="ts">
import { ref } from 'vue'
import type { ReviewFinding } from '../api/types'
import { AgButton } from '../components/ui'

// One finding: 🔴 breaks / 🟠 performance, why and the fix, and the person's Keep · Edit · Drop.
const props = defineProps<{ finding: ReviewFinding; readonly?: boolean }>()
const emit = defineEmits<{ decide: [decision: 'kept' | 'dropped' | 'edited', text?: string] }>()
const editing = ref(false)
const text = ref('')

function edit(): void {
  text.value = props.finding.edited ?? [props.finding.title, props.finding.detail, props.finding.suggestion && `Fix: ${props.finding.suggestion}`].filter(Boolean).join('\n')
  editing.value = true
}

function save(): void {
  if (!text.value.trim()) return
  emit('decide', 'edited', text.value.trim())
  editing.value = false
}
</script>

<template>
  <article
    class="grid gap-1 rounded-box border border-base-300 bg-base-100 p-3 text-sm"
    :class="finding.decision === 'dropped' ? 'opacity-60' : ''"
    :data-decision="finding.decision"
  >
    <header class="flex flex-wrap items-baseline gap-2">
      <span :aria-label="finding.severity === 'breaks' ? 'can break the app' : 'performance'">{{ finding.severity === 'breaks' ? '🔴' : '🟠' }}</span>
      <strong :class="finding.decision === 'dropped' ? 'line-through' : ''">#{{ finding.number }} {{ finding.title }}</strong>
      <span
        v-if="finding.file"
        class="font-mono text-xs text-muted"
      >{{ finding.file }}{{ finding.line ? `:${finding.line}` : '' }}</span>
      <span
        v-if="finding.decision !== 'kept'"
        class="badge badge-sm"
      >{{ finding.decision }}</span>
    </header>
    <p
      v-if="finding.decision === 'edited' && !editing"
      class="whitespace-pre-wrap"
    >
      {{ finding.edited }}
    </p>
    <template v-else-if="!editing">
      <p
        v-if="finding.detail"
        class="whitespace-pre-wrap"
      >
        {{ finding.detail }}
      </p>
      <p
        v-if="finding.suggestion"
        class="whitespace-pre-wrap"
      >
        <span class="font-medium">Fix:</span> {{ finding.suggestion }}
      </p>
    </template>
    <div
      v-if="editing"
      class="grid gap-2"
    >
      <textarea
        v-model="text"
        class="textarea w-full font-mono text-xs"
        rows="4"
        aria-label="Your text for this finding"
      />
      <div class="flex gap-2">
        <AgButton
          size="sm"
          @click="save"
        >
          Save
        </AgButton>
        <AgButton
          size="sm"
          variant="ghost"
          @click="editing = false"
        >
          Cancel
        </AgButton>
      </div>
    </div>
    <div
      v-else-if="!readonly"
      class="flex gap-2"
    >
      <AgButton
        size="sm"
        :variant="finding.decision === 'kept' ? 'primary' : 'outline'"
        @click="emit('decide', 'kept')"
      >
        Keep
      </AgButton>
      <AgButton
        size="sm"
        variant="outline"
        @click="edit"
      >
        Edit
      </AgButton>
      <AgButton
        size="sm"
        :variant="finding.decision === 'dropped' ? 'error' : 'outline'"
        @click="emit('decide', 'dropped')"
      >
        Drop
      </AgButton>
    </div>
  </article>
</template>
