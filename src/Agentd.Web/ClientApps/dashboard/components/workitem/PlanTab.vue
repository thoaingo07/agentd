<script setup lang="ts">
// Plan estimate against actual, and the 5-hour / weekly usage reported over each run.
import { computed } from 'vue'
import type { AgentEvent, JobDetail, JobSummary } from '../../../shared/api/types'
import { AgMeter } from '../../../shared/components/ui'
import { clock, duration, percent } from '../../../shared/utils/format'
import { sections, usageSamples } from './sections'

const props = defineProps<{ jobs: JobSummary[]; events: AgentEvent[]; details: Map<number, JobDetail> }>()
const rows = computed(() =>
  sections(props.jobs, props.events).map((s) => {
    const samples = usageSamples(s.events)
    const detail = props.details.get(s.job.id)
    const peak = samples.reduce((m, x) => Math.max(m, x.fiveHour ?? 0), 0)
    return { ...s, samples, estimate: detail?.estimate ?? null, peak, last: samples.at(-1) }
  }),
)
</script>

<template>
  <div class="grid gap-6">
    <section
      v-for="r in rows"
      :key="r.job.id"
      class="grid gap-2 text-sm"
    >
      <h2 class="text-base font-semibold">
        {{ r.label }}
      </h2>
      <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1">
        <dt class="text-muted">
          Plan
        </dt><dd>{{ r.job.planStatus }}</dd>
        <dt class="text-muted">
          Time
        </dt>
        <dd data-time>
          {{ duration(r.job.elapsedSeconds) }} actual<span v-if="r.estimate"> · ~{{ r.estimate.minutes }} min estimated</span>
        </dd>
        <dt class="text-muted">
          5-hour usage
        </dt>
        <dd data-usage>
          peak {{ percent(r.peak) }}<span v-if="r.estimate"> · ~{{ r.estimate.usagePercent }}% estimated</span>
          <span v-if="r.last?.weekly != null"> · week {{ percent(r.last.weekly) }}</span>
        </dd>
      </dl>
      <AgMeter
        v-if="r.samples.length"
        class="max-w-sm"
        label="Peak of the 5-hour window"
        :value="Math.round(r.peak * 100)"
        :max="100"
      />
      <details
        v-if="r.samples.length"
        class="text-xs text-muted"
      >
        <summary>{{ r.samples.length }} usage readings</summary>
        <ul class="mt-1 font-mono tabular-nums">
          <li
            v-for="(x, i) in r.samples"
            :key="i"
          >
            {{ clock(x.at) }} · 5h {{ percent(x.fiveHour) }} · week {{ percent(x.weekly) }}
          </li>
        </ul>
      </details>
    </section>
  </div>
</template>
