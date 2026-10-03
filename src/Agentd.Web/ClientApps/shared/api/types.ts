// Friendly names for the generated BFF contract (schema.d.ts, from openapi.json; run `npm run gen:api`).
// Don't hand-write DTO interfaces that duplicate these.
import type { components } from './schema'

type Schemas = components['schemas']

export type JobSummary = Schemas['JobSummaryVm']
export type JobDetail = Schemas['JobDetailVm']
export type Dashboard = Schemas['DashboardVm']
export type AgentEvent = Schemas['EventVm']
export type EventPage = Schemas['EventPageVm']
export type HistoryPage = Schemas['HistoryPageVm']
export type Diff = Schemas['DiffVm']
export type Estimate = Schemas['EstimateVm']
export type Usage = Schemas['UsageVm']
export type Conversation = Schemas['ConversationVm']
export type MessageRequest = Schemas['MessageRequest']
export type MessageAccepted = Schemas['MessageAcceptedVm']
export type RunAccepted = Schemas['RunAcceptedVm']

/** Every BFF error is ProblemDetails; agentd adds a machine-readable `code` (not_found, invalid_transition, …). */
export type Problem = Schemas['ProblemDetails'] & { code?: string }
export type ValidationProblem = Schemas['HttpValidationProblemDetails']

/** Job states as the BFF sends them (enums travel as strings). */
export const jobStates = [
  'Queued',
  'Preparing',
  'Running',
  'WaitingForHuman',
  'Publishing',
  'InReview',
  'Done',
  'Failed',
  'Cancelled',
] as const
export type JobState = (typeof jobStates)[number]

export function isJobState(value: string): value is JobState {
  return (jobStates as readonly string[]).includes(value)
}
