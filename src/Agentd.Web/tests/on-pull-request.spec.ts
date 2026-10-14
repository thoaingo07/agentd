import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import OnPullRequest from '../ClientApps/shared/review/OnPullRequest.vue'

// A page review sent to a PR is re-checked after pushes (review-sessions.md §4): the page shows where its findings stand.
const posted = (over: Record<string, unknown> = {}) => ({
  pullRequestId: 3944, head: 'h2aaaaaa', done: false,
  findings: [
    { severity: 'breaks', title: 'Unbounded read', file: 'src/Sync.cs', line: 41, status: 'fixed' },
    { severity: 'breaks', title: 'No rollback', file: null, line: null, status: 'open' },
  ],
  rechecks: [
    { at: '2026-10-14T10:00:00Z', head: 'h2aaaaaa', fixed: 1, open: 1, closed: 0, added: 1, problem: null },
    { at: '2026-10-14T11:00:00Z', head: 'h3bbbbbb', fixed: 0, open: 0, closed: 0, added: 0, problem: 'the reply had no review-recheck block' },
  ],
  ...over,
})

describe('On the PR', () => {
  it('shows each posted finding\'s status and every re-check', () => {
    const w = mount(OnPullRequest, { props: { posted: posted() } })

    expect(w.get('h2').text()).toBe('On PR !3944')
    expect(w.text()).toContain('Re-checked after each push')
    const findings = w.findAll('[data-testid=on-pr-finding]')
    expect(findings[0]!.text()).toContain('✅')
    expect(findings[0]!.find('.line-through').exists()).toBe(true)
    expect(findings[1]!.text()).toContain('🔴')
    const rechecks = w.findAll('[data-testid=recheck]').map((r) => r.text())
    expect(rechecks[0]).toContain('h2aaaaa: 1 fixed, 1 open, 1 new')
    expect(rechecks[1]).toContain('not re-checked (the reply had no review-recheck block)')
  })

  it('says when everything is resolved, or the PR closed', () => {
    const resolved = posted({ findings: [{ severity: 'breaks', title: 'Unbounded read', file: null, line: null, status: 'fixed' }], rechecks: [] })
    expect(mount(OnPullRequest, { props: { posted: resolved } }).text()).toContain('Everything is resolved.')
    expect(mount(OnPullRequest, { props: { posted: posted({ done: true }) } }).text()).toContain('The PR is closed: no more re-checks.')
  })
})
