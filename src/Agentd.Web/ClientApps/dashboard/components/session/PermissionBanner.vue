<script setup lang="ts">
// The agent's open permission requests for a job (tool calls outside its allowlist), answered like the chat's 1–4.
// The first answer wins, from here or the chat thread; no answer before the timeout is a deny.
import type { PermissionChoice, PermissionRequest } from '../../../shared/api/types'
import { AgButton } from '../../../shared/components/ui'

defineProps<{ requests: PermissionRequest[]; repo: string; busy?: boolean }>()
const emit = defineEmits<{ answer: [requestId: number, choice: PermissionChoice] }>()
</script>

<template>
  <div
    v-for="r in requests"
    :key="r.id"
    role="alert"
    class="alert grid gap-2 border-warning bg-warning/10 text-base-content"
    data-testid="permission-request"
  >
    <div class="grid min-w-0 gap-1">
      <strong>🔐 Permission needed (request {{ r.id }})</strong>
      <span class="text-sm">The agent wants to run ({{ r.tool }}):</span>
      <pre class="max-h-40 overflow-auto whitespace-pre-wrap break-all rounded bg-base-100 p-2 font-mono text-[13px]">{{ r.summary }}</pre>
      <span
        v-if="r.ruleKeys.length"
        class="text-xs text-muted"
      >Remembering it allows: <code class="font-mono">{{ r.ruleKeys.join(', ') }}</code></span>
    </div>
    <div class="flex flex-wrap gap-2">
      <AgButton
        size="sm"
        :disabled="busy"
        @click="emit('answer', r.id, 'once')"
      >
        Allow once
      </AgButton>
      <AgButton
        size="sm"
        variant="outline"
        :disabled="busy"
        @click="emit('answer', r.id, 'job')"
      >
        Allow for this job
      </AgButton>
      <AgButton
        size="sm"
        variant="outline"
        :disabled="busy"
        @click="emit('answer', r.id, 'repo')"
      >
        Always allow in {{ repo }}
      </AgButton>
      <AgButton
        size="sm"
        variant="error"
        :disabled="busy"
        @click="emit('answer', r.id, 'deny')"
      >
        Deny
      </AgButton>
    </div>
  </div>
</template>
