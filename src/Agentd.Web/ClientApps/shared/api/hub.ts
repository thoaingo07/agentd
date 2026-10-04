// The live event connection (/hubs/events). Stores talk to this small interface, so tests inject a fake.
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import type { AgentEvent } from './types'

export const hubPath = '/hubs/events'

export interface EventConnection {
  start(): Promise<void>
  stop(): Promise<void>
  subscribe(stream: string, afterSeq: number): Promise<void>
  unsubscribe(stream: string): Promise<void>
  onEvent(handler: (stream: string, evt: AgentEvent) => void): void
  onReconnecting(handler: () => void): void
  onReconnected(handler: () => void): void
  onClose(handler: () => void): void
}

function signalR(): EventConnection {
  const connection = new HubConnectionBuilder()
    .withUrl(hubPath)
    .withAutomaticReconnect([0, 1000, 2000, 5000, 10000, 30000])
    .configureLogging(LogLevel.Warning)
    .build()
  return {
    start: () => connection.start(),
    stop: () => connection.stop(),
    subscribe: (stream, afterSeq) => connection.invoke('Subscribe', stream, afterSeq),
    unsubscribe: (stream) => connection.invoke('Unsubscribe', stream),
    onEvent: (handler) => connection.on('event', handler),
    onReconnecting: (handler) => connection.onreconnecting(() => handler()),
    onReconnected: (handler) => connection.onreconnected(() => handler()),
    onClose: (handler) => connection.onclose(() => handler()),
  }
}

let factory: () => EventConnection = signalR

export function createEventConnection(): EventConnection {
  return factory()
}

/** Tests: replace the SignalR connection with a fake. */
export function setEventConnectionFactory(f: () => EventConnection): void {
  factory = f
}
