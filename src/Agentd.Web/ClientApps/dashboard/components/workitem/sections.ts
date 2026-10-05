import type { AgentEvent, JobSummary } from '../../../shared/api/types'

export interface JobSection {
  job: JobSummary
  label: string
  events: AgentEvent[]
}

/** "Run 1", "Run 2 (rework)", … with " · hand-off" when the run carried the knowledge hand-off. */
export function label(index: number, job: JobSummary): string {
  const run = index === 0 ? 'Run 1' : `Run ${index + 1} (rework)`
  return job.handoff !== 'None' ? `${run} · hand-off` : run
}

/** Events grouped by job, in the order the jobs ran. */
export function sections(jobs: JobSummary[], events: AgentEvent[]): JobSection[] {
  return jobs.map((job, i) => ({ job, label: label(i, job), events: events.filter((e) => e.jobId === job.id) }))
}

export interface UsageSample {
  at: string
  fiveHour: number | null
  weekly: number | null
}

/** The 5-hour and weekly utilization the CLI reported over a job's life (agent.rate_limit). */
export function usageSamples(events: AgentEvent[]): UsageSample[] {
  return events
    .filter((e) => e.type === 'agent.rate_limit')
    .map((e) => {
      const u = ((e.payload as Record<string, unknown>).utilization ?? {}) as Record<string, unknown>
      const n = (v: unknown) => (typeof v === 'number' ? v : null)
      return { at: e.ts, fiveHour: n(u.five_hour), weekly: n(u.seven_day) }
    })
}
