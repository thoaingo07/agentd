import { watch } from 'vue'
import type { Router } from 'vue-router'
import { get } from '../../shared/api/http'
import type { InboxThread } from '../../shared/api/types'
import { useInboxStore } from './inbox'

/** Your choice, kept per browser: notifications are on only once you turn them on (the browser asks first). */
export const notifyKey = 'agentd.notify'

export function notificationsOn(): boolean {
  try {
    return typeof Notification !== 'undefined' && Notification.permission === 'granted' && window.localStorage.getItem(notifyKey) === 'on'
  } catch {
    return false
  }
}

/** Turns notifications on (asking the browser, from a click) or off; returns whether they're on. */
export async function setNotifications(on: boolean): Promise<boolean> {
  if (typeof Notification === 'undefined') return false
  if (on && Notification.permission === 'default') await Notification.requestPermission()
  try {
    window.localStorage.setItem(notifyKey, on && Notification.permission === 'granted' ? 'on' : 'off')
  } catch {
    // private window: on until the page closes is all we can do
  }

  return notificationsOn()
}

/**
 * While agentd is open in a tab: the tab's title says how many threads wait on you ("(2) agentd"), and when one more starts
 * waiting, a browser notification names it (if you turned them on); clicking it opens the thread. Not on first load, only
 * on an increase. Returns the stop function.
 */
export function watchInbox(router: Router): () => void {
  const inbox = useInboxStore()
  const base = document.title.replace(/^\(\d+\) /, '')
  let known: number | null = null
  return watch(() => inbox.counts.waiting, async (waiting) => {
    document.title = waiting > 0 ? `(${waiting}) ${base}` : base
    const before = known
    known = waiting
    if (before === null || waiting <= before || !notificationsOn()) return

    const threads = await get<{ threads: InboxThread[] }>('/api/inbox?view=all').then((p) => p.threads, () => [])
    const t = threads.find((x) => x.waiting && x.unread > 0)
    if (!t) return
    const note = new Notification(`agentd · waiting on you: ${t.title}`, {
      body: (t.question?.markdown ?? t.latestPreview).replace(/[*_`#>]/g, '').slice(0, 180),
      tag: `agentd-inbox-${t.source}-${t.sourceId}`,
    })
    note.onclick = () => {
      window.focus()
      void router.push({ name: 'inbox-thread', params: { source: t.source, id: t.sourceId } })
      note.close()
    }
  }, { immediate: true })
}
