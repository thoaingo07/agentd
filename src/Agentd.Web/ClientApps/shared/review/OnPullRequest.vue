<script setup lang="ts">
// A Send to a PR as it stands (review-sessions.md §4): each posted finding's status on the PR, and the re-checks agentd
// makes after each push or reply until none is open or the PR closes.
import type { Posted } from '../api/types'
import { ago } from '../utils/format'

defineProps<{ posted: Posted }>()
const mark: Record<string, string> = { fixed: '✅', closed: '⚪' }
</script>

<template>
  <section
    class="ag-pane grid gap-2 p-3 text-sm"
    aria-labelledby="on-pr-title"
    data-testid="on-pr"
  >
    <h2
      id="on-pr-title"
      class="font-semibold"
    >
      On PR !{{ posted.pullRequestId }}
    </h2>
    <p class="ag-meta">
      {{ posted.done ? 'The PR is closed: no more re-checks.'
        : posted.findings.every((f) => f.status !== 'open') ? 'Everything is resolved.'
          : 'Re-checked after each push or reply on a finding, until none is open.' }}
    </p>
    <ul class="grid gap-1">
      <li
        v-for="(f, i) in posted.findings"
        :key="i"
        class="flex min-w-0 items-start gap-2"
        data-testid="on-pr-finding"
      >
        <span aria-hidden="true">{{ mark[f.status] ?? (f.severity === 'breaks' ? '🔴' : '🟠') }}</span>
        <span
          class="min-w-0 flex-1"
          :class="f.status === 'open' ? '' : 'text-muted line-through'"
        >{{ f.title }}</span>
        <span class="ag-meta">{{ f.status }}</span>
      </li>
    </ul>
    <ol
      v-if="posted.rechecks.length"
      class="grid gap-1 border-t border-base-300 pt-2"
      aria-label="Re-checks"
    >
      <li
        v-for="(r, i) in posted.rechecks"
        :key="i"
        class="ag-meta"
        data-testid="recheck"
      >
        🔄 {{ ago(r.at) }} at <code>{{ r.head?.slice(0, 7) }}</code>:
        <template v-if="r.problem">
          not re-checked ({{ r.problem }})
        </template>
        <template v-else>
          {{ r.fixed }} fixed, {{ r.open }} open{{ r.closed ? `, ${r.closed} closed` : '' }}{{ r.added ? `, ${r.added} new` : '' }}
        </template>
      </li>
    </ol>
  </section>
</template>
