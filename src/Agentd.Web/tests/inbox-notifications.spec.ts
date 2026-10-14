import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { useInboxStore } from '../ClientApps/dashboard/stores/inbox'
import { notificationsOn, notifyKey, setNotifications, watchInbox } from '../ClientApps/dashboard/stores/inboxNotifications'

// A browser notification when a thread starts waiting on you, once you turn them on; the tab title counts them.
class FakeNotification {
  static permission: NotificationPermission = 'default'
  static shown: FakeNotification[] = []
  static requestPermission = vi.fn(async () => (FakeNotification.permission = 'granted'))
  onclick: (() => void) | null = null
  closed = false
  constructor(public title: string, public options: NotificationOptions) {
    FakeNotification.shown.push(this)
  }

  close() {
    this.closed = true
  }
}

const router = () => createRouter({ history: createMemoryHistory(), routes: [
  { path: '/', component: { template: '<div />' } },
  { path: '/inbox/:source/:id', name: 'inbox-thread', component: { template: '<div />' } },
] })

beforeEach(() => {
  setActivePinia(createPinia())
  window.localStorage.clear()
  document.title = 'agentd'
  FakeNotification.permission = 'default'
  FakeNotification.shown = []
  vi.stubGlobal('Notification', FakeNotification)
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ counts: { waiting: 2, unread: 2 }, threads: [
    { source: 'job', sourceId: 41, title: 'WI-5666 Upgrade Angular', recipient: null, latestId: 52, latestKind: 'Question', latestPreview: 'Approve the plan?',
      latestAt: '2026-10-14T10:00:00Z', items: 3, unread: 1, waiting: true,
      question: { id: 52, source: 'job', sourceId: 41, title: 'x', recipient: null, kind: 'Question', markdown: '📝 **Plan**: bump Angular. Approve?', options: [], createdAt: '2026-10-14T10:00:00Z' } },
  ] }))))
})
afterEach(() => vi.unstubAllGlobals())

describe('Inbox notifications', () => {
  it('are off until turned on from a click, which asks the browser once', async () => {
    expect(notificationsOn()).toBe(false)
    expect(await setNotifications(true)).toBe(true)
    expect(FakeNotification.requestPermission).toHaveBeenCalledOnce()
    expect(window.localStorage.getItem(notifyKey)).toBe('on')
    expect(await setNotifications(false)).toBe(false)
  })

  it('a thread starting to wait notifies (not the first count), names it, and a click opens it; the title counts', async () => {
    await setNotifications(true)
    const r = router()
    const inbox = useInboxStore()
    inbox.counts = { waiting: 1, unread: 1 }
    const stop = watchInbox(r)
    await flushPromises()
    expect(document.title).toBe('(1) agentd')
    expect(FakeNotification.shown, 'what was already waiting when the page opened').toHaveLength(0)

    inbox.counts = { waiting: 2, unread: 2 }
    await flushPromises()
    expect(FakeNotification.shown).toHaveLength(1)
    const note = FakeNotification.shown[0]!
    expect(note.title).toBe('agentd · waiting on you: WI-5666 Upgrade Angular')
    expect(note.options.body).toBe('📝 Plan: bump Angular. Approve?')
    note.onclick!()
    await flushPromises()
    expect(r.currentRoute.value.fullPath).toBe('/inbox/job/41')
    expect(note.closed).toBe(true)

    inbox.counts = { waiting: 0, unread: 0 }
    await flushPromises()
    expect(document.title).toBe('agentd')
    stop()
  })

  it('stay quiet when turned off, even as threads start waiting', async () => {
    FakeNotification.permission = 'granted'
    const inbox = useInboxStore()
    inbox.counts = { waiting: 0, unread: 0 }
    const stop = watchInbox(router())
    inbox.counts = { waiting: 3, unread: 3 }
    await flushPromises()

    expect(FakeNotification.shown).toHaveLength(0)
    expect(document.title).toBe('(3) agentd')
    stop()
  })
})
