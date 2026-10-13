import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import DiffFileReview from '../ClientApps/shared/review/DiffFileReview.vue'
import { parseDiff } from '../ClientApps/shared/utils/diff'
import { isGenerated } from '../ClientApps/shared/utils/generated'

// Reading a review (review-sessions.md §2a, step 5): generated files' diffs are hidden until asked.
const lock = parseDiff('diff --git a/src/Agentd.Web/package-lock.json b/src/Agentd.Web/package-lock.json\n--- a/src/Agentd.Web/package-lock.json\n+++ b/src/Agentd.Web/package-lock.json\n@@ -1,1 +1,1 @@\n-"version": "1"\n+"version": "2"\n')[0]!
const finding = { number: 1, severity: 'breaks', file: lock.newPath, line: 1, title: 'Downgrades a package', detail: null, suggestion: null, decision: 'kept', edited: null }

describe('generated files', () => {
  it.each([
    ['package-lock.json', true], ['web/yarn.lock', true], ['go.sum', true], ['app.min.js', true], ['Models/Foo.g.cs', true],
    ['Migrations/AppModelSnapshot.cs', true], ['dist/app.js', true], ['src/Sync.cs', false], ['docs/lock.md', false],
  ])('%s is generated: %s', (path, expected) => {
    expect(isGenerated(path)).toBe(expected)
  })

  it('hides a generated file\'s diff until Show the diff', async () => {
    const view = mount(DiffFileReview, { props: { file: lock, findings: [], comments: [] } })

    expect(view.text()).toContain('generated')
    expect(view.find('table').exists()).toBe(false)
    await view.get('[data-testid=generated-hidden] button').trigger('click')
    expect(view.find('table').exists()).toBe(true)
  })

  it('shows a generated file with a finding in it', () => {
    const view = mount(DiffFileReview, { props: { file: lock, findings: [finding], comments: [] } })

    expect(view.find('[data-testid=generated-hidden]').exists()).toBe(false)
    expect(view.find('table').exists()).toBe(true)
  })
})
