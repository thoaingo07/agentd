import { defineStore } from 'pinia'
import { reactive, ref } from 'vue'
import { createEventConnection, type EventConnection } from '../../shared/api/hub'
import type { AgentEvent } from '../../shared/api/types'
import { useEventsStore } from './events'
import { useIdeasStore } from './ideas'
import { useJobsStore } from './jobs'
import { useWorkItemsStore } from './workItems'

export type ConnectionStatus = 'connecting' | 'live' | 'reconnecting' | 'offline'

/** Backoff for restarting after the automatic reconnect gave up. */
export const restartDelaysMs = [1000, 2000, 5000, 10000, 30000]

/**
 * The live connection. Each stream ("all" or a job id) remembers the last seq it delivered; after a
 * reconnect every stream resubscribes from there, and the hub replays what was missed. Consumers
 * deduplicate against their own high-water marks; there is no global dedupe. Several consumers can share a stream (the
 * work item page's timeline and its run's transcript): it's counted, replayed from the earliest seq any of them needs,
 * and only unsubscribed when the last one leaves.
 */
export const useConnectionStore = defineStore('connection', () => {
  const status = ref<ConnectionStatus>('offline')
  const subscriptions = reactive(new Map<string, number>())
  const holders = new Map<string, number>()
  let connection: EventConnection | null = null
  let restarts = 0

  function route(stream: string, evt: AgentEvent): void {
    subscriptions.set(stream, Math.max(subscriptions.get(stream) ?? 0, evt.seq))
    if (stream === 'all') {
      useJobsStore().apply(evt)
      useIdeasStore().apply(evt)
    } else {
      useEventsStore().append(evt)
      useWorkItemsStore().append(evt)
    }
  }

  async function resubscribeAll(): Promise<void> {
    for (const [stream, lastSeq] of subscriptions) await connection!.subscribe(stream, lastSeq)
  }

  async function connect(): Promise<void> {
    status.value = 'connecting'
    try {
      await connection!.start()
      status.value = 'live'
      restarts = 0
      await resubscribeAll()
    } catch {
      scheduleRestart()
    }
  }

  function scheduleRestart(): void {
    status.value = 'offline'
    const delay = restartDelaysMs[Math.min(restarts++, restartDelaysMs.length - 1)]
    setTimeout(() => void connect(), delay)
  }

  async function start(): Promise<void> {
    if (connection) return
    connection = createEventConnection()
    connection.onEvent(route)
    connection.onReconnecting(() => (status.value = 'reconnecting'))
    connection.onReconnected(() => {
      status.value = 'live'
      void resubscribeAll()
    })
    connection.onClose(scheduleRestart)
    await connect()
  }

  async function subscribe(stream: string, afterSeq: number): Promise<void> {
    const held = holders.get(stream) ?? 0
    holders.set(stream, held + 1)
    const current = subscriptions.get(stream)
    if (held > 0 && current !== undefined && current <= afterSeq) return   // already streaming from early enough
    subscriptions.set(stream, held > 0 && current !== undefined ? Math.min(current, afterSeq) : afterSeq)
    if (status.value === 'live') await connection!.subscribe(stream, subscriptions.get(stream)!)
  }

  async function unsubscribe(stream: string): Promise<void> {
    const held = (holders.get(stream) ?? 1) - 1
    if (held > 0) {
      holders.set(stream, held)
      return
    }

    holders.delete(stream)
    subscriptions.delete(stream)
    if (status.value === 'live') await connection!.unsubscribe(stream)
  }

  return { status, subscriptions, start, subscribe, unsubscribe, route }
})
