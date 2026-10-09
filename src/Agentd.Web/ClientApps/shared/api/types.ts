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
export type PermissionRequest = Schemas['PermissionRequestVm']
export type IdeaSummary = Schemas['IdeaSummaryVm']
export type Resources = Schemas['ResourcesVm']
export type MachineResources = Schemas['MachineVm']
export type JobResources = Schemas['JobResourcesVm']
export type IdeaDetail = Schemas['IdeaDetailVm']
export type WorkItemDraft = Schemas['WorkItemDraftVm']
export type PermissionRule = Schemas['PermissionRuleVm']
/** The Web UI's answers, the same as the chat's 1–4. */
export type PermissionChoice = 'once' | 'job' | 'repo' | 'deny'
export type MessageRequest = Schemas['MessageRequest']
export type MessageAccepted = Schemas['MessageAcceptedVm']
export type RunAccepted = Schemas['RunAcceptedVm']
export type SetupSession = Schemas['SetupSessionVm']
export type SecretStatus = Schemas['SecretStatusVm']
export type DatabaseStep = Schemas['DatabaseStepVm']
export type AzureDevOpsStep = Schemas['AzureDevOpsStepVm']
export type StepCheck = Schemas['StepCheckVm']
export type SaveResult = Schemas['SaveResultVm']
export type AzureDevOpsRequest = Schemas['AzureDevOpsRequest']
export type GitKeyStep = Schemas['GitKeyStepVm']
export type ClaudeStep = Schemas['ClaudeStepVm']
export type ChatStep = Schemas['ChatStepVm']
export type ChatRequest = Schemas['ChatRequest']
export type RepositoryEntry = Schemas['RepositoryEntryVm']
export type RepositoryRequest = Schemas['RepositoryRequest']
export type ReviewItem = Schemas['ReviewItemVm']
export type FinishResult = Schemas['FinishVm']
export type ModelsStep = Schemas['ModelsStepVm']
export type ModelProfile = Schemas['ProfileVm']
export type ProfileRequest = Schemas['ProfileRequest']
export type StepModel = Schemas['StepModelVm']
export type AdoConnections = Schemas['AdoConnectionsVm']
export type ReviewSession = Schemas['ReviewSessionVm']
export type ReviewDetail = Schemas['ReviewSessionDetailVm']
export type ReviewFinding = Schemas['ReviewFindingVm']
export type ReviewComment = Schemas['ReviewCommentVm']
export type ReviewDiff = Schemas['ReviewDiffVm']

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
  'Paused',
  'Cancelled',
] as const
export type JobState = (typeof jobStates)[number]

export function isJobState(value: string): value is JobState {
  return (jobStates as readonly string[]).includes(value)
}

export type Config = Schemas['ConfigVm']
export type Repository = Schemas['RepositoryVm']
export type WorkItem = Schemas['WorkItemVm']
export type ConversationEntry = Schemas['ConversationEntryVm']
