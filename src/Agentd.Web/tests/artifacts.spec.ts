import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { toRows } from '../ClientApps/dashboard/components/session/transcript'
import ArtifactsView from '../ClientApps/dashboard/views/ArtifactsView.vue'

const router = () => createRouter({ history: createMemoryHistory(), routes: [
  { path: '/', component: { template: '<div />' } },
  { path: '/jobs/:id', name: 'job', component: { template: '<div />' } },
  { path: '/ideas/:id', name: 'idea', component: { template: '<div />' } },
  { path: '/chats/:id', name: 'chat', component: { template: '<div />' } },
  { path: '/reviews/:id', name: 'review', component: { template: '<div />' } },
] })

beforeEach(() => {
  setActivePinia(createPinia())
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify([
    { name: 'sync-report-1a2b3c4d', title: 'Sync report', url: '/artifacts/sync-report-1a2b3c4d.html', source: 'job', sourceId: 12, author: 'job #12', createdAt: '2026-10-10T10:00:00Z', updatedAt: '2026-10-10T11:00:00Z' },
    { name: 'diagram-00ff00ff', title: 'Diagram', url: '/artifacts/diagram-00ff00ff.html', source: 'idea', sourceId: 7, author: 'idea #7', createdAt: '2026-10-10T09:00:00Z', updatedAt: '2026-10-10T09:00:00Z' },
    { name: 'pr-notes-12345678', title: 'PR notes', url: '/artifacts/pr-notes-12345678.html', source: 'review', sourceId: 3, author: 'review #3', createdAt: '2026-10-10T08:00:00Z', updatedAt: '2026-10-10T08:00:00Z' },
  ]))))
})
afterEach(() => vi.unstubAllGlobals())

describe('Artifacts', () => {
  it('lists the pages, each opening on its own (new tab, no opener), with the agent that published it', async () => {
    const w = mount(ArtifactsView, { global: { plugins: [router()] } })
    await flushPromises()

    const rows = w.findAll('[data-testid=artifact]')
    const link = rows[0]!.get('a')
    expect([link.text(), link.attributes('href'), link.attributes('target'), link.attributes('rel')]).toEqual(['Sync report', '/artifacts/sync-report-1a2b3c4d.html', '_blank', 'noopener noreferrer'])
    expect(rows[0]!.findAll('a')[1]!.attributes('href')).toBe('/jobs/12')
    expect(rows[1]!.findAll('a')[1]!.attributes('href')).toBe('/ideas/7')
    expect(rows[2]!.findAll('a')).toHaveLength(1)   // a PR review thread has no page here
    expect(rows[2]!.text()).toContain('PR review #3')
  })

  it('a job\'s transcript says when it published or updated one, with a link', () => {
    const ev = (seq: number, created: boolean) => ({ seq, jobId: 12, ts: '2026-10-10T10:00:00Z', type: 'artifact.published', payload: { name: 'sync-report-1a2b3c4d', title: 'Sync report', created } })

    const rows = toRows([ev(1, true), ev(2, false)])

    expect(rows.map((r) => (r.kind === 'state' ? r.title : ''))).toEqual(['Artifact published: Sync report', 'Artifact updated: Sync report'])
    expect(rows[0]!.kind === 'state' && rows[0]!.detail).toBe(`[Open it](${location.origin}/artifacts/sync-report-1a2b3c4d.html)`)
  })
})
