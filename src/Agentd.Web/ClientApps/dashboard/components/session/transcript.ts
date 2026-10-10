// Turns the raw event window into transcript rows: tool calls paired with their results, events
// grouped into the filter categories of the Session view.
import type { AgentEvent } from '../../../shared/api/types'
import { humanize } from '../../../shared/utils/format'
import type { EventCategory } from '../../stores/ui'

export type Row =
  | { kind: 'text'; key: number; event: AgentEvent; text: string }
  | { kind: 'tool'; key: number; call: AgentEvent; result?: AgentEvent }
  | { kind: 'question'; key: number; event: AgentEvent; question: string; options: string[] }
  | { kind: 'reply'; key: number; event: AgentEvent; reply: string; from: string; via?: string }
  | { kind: 'state'; key: number; event: AgentEvent; title: string; detail?: string }
  | { kind: 'turn'; key: number; event: AgentEvent; turns?: number }
  | { kind: 'error'; key: number; event: AgentEvent; message: string }

export const categoryOf: Record<Row['kind'], EventCategory> = {
  text: 'text',
  tool: 'tools',
  question: 'messages',
  reply: 'messages',
  state: 'state',
  turn: 'state',
  error: 'errors',
}

type Payload = Record<string, unknown>
const str = (v: unknown): string => (typeof v === 'string' ? v : '')
/** Domain events serialize value objects as { value }. */
const val = (v: unknown): string => (v && typeof v === 'object' && 'value' in v ? String((v as Payload).value) : str(v))

/** Agent noise that adds nothing to a reader (session bookkeeping, usage polling, unknown system lines). */
const hidden = new Set(['agent.session', 'agent.rate_limit', 'agent.other', 'agent.unparseable'])

/** The same event(s) behind both rows: the earlier row can stay, so its component doesn't render again. */
function same(a: Row, b: Row): boolean {
  if (a.kind !== b.kind) return false
  if (a.kind === 'tool') return a.call === (b as typeof a).call && a.result === (b as typeof a).result
  return a.event === (b as Exclude<Row, { kind: 'tool' }>).event
}

/**
 * The rows of a transcript. With the previous rows, unchanged ones are reused as they were: a live transcript renders
 * only its new rows (and a tool call whose result just came).
 */
export function toRows(events: readonly AgentEvent[], previous?: ReadonlyMap<number, Row>): Row[] {
  const rows: Row[] = []
  const calls = new Map<string, Extract<Row, { kind: 'tool' }>>()
  for (const event of events) {
    const p = (event.payload ?? {}) as Payload
    switch (event.type) {
      case 'agent.text':
        rows.push({ kind: 'text', key: event.seq, event, text: str(p.text) })
        break
      case 'agent.tool_call': {
        const row = { kind: 'tool' as const, key: event.seq, call: event }
        calls.set(str(p.id), row)
        rows.push(row)
        break
      }
      case 'agent.tool_result': {
        const call = calls.get(str(p.toolUseId))
        if (call) call.result = event
        else rows.push({ kind: 'tool', key: event.seq, call: event, result: event })
        break
      }
      case 'agent.result':
        rows.push({ kind: 'turn', key: event.seq, event, turns: typeof p.turns === 'number' ? p.turns : undefined })
        break
      case 'DeveloperQuestionAsked':
        rows.push({ kind: 'question', key: event.seq, event, question: str(p.question), options: Array.isArray(p.options) ? p.options.map(String) : [] })
        break
      case 'DeveloperReplied':
        rows.push({ kind: 'reply', key: event.seq, event, reply: str(p.reply), from: str(p.from), via: val(p.via) || undefined })
        break
      case 'JobFailed':
        rows.push({ kind: 'error', key: event.seq, event, message: str(p.reason) || 'The job failed.' })
        break
      case 'phase.set':
        rows.push({ kind: 'state', key: event.seq, event, title: `Phase: ${str(p.phase)}`, detail: str(p.summary) || undefined })
        break
      case 'progress.reported':
        rows.push({ kind: 'state', key: event.seq, event, title: str(p.message) || 'Progress' })
        break
      case 'step.started':
        rows.push({ kind: 'state', key: event.seq, event, title: str(p.line) || 'Step started' })
        break
      case 'PullRequestCreated':
        rows.push({ kind: 'state', key: event.seq, event, title: 'Pull request opened', detail: val(p.url) })
        break
      default:
        if (!hidden.has(event.type)) rows.push({ kind: 'state', key: event.seq, event, title: humanize(event.type) })
    }
  }
  if (!previous) return rows
  return rows.map((r) => {
    const before = previous.get(r.key)
    return before && same(before, r) ? before : r
  })
}

/** One line for a tool call header: the most telling argument (command, file, pattern, …). */
export function toolSummary(call: AgentEvent): { name: string; summary: string } {
  const p = (call.payload ?? {}) as Payload
  const name = str(p.name).replace(/^mcp__agentd__/, 'agentd:')
  let input: Payload = {}
  try {
    input = JSON.parse(str(p.inputJson) || '{}') as Payload
  } catch {
    // Not JSON: leave the summary empty.
  }
  const summary = str(input.command) || str(input.file_path) || str(input.path) || str(input.pattern) || str(input.url) || str(input.description) || str(input.phase) || str(input.question)
  return { name, summary: summary.length > 120 ? `${summary.slice(0, 117)}…` : summary }
}
