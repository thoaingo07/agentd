import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import { blockDiff, parseDiff } from '../ClientApps/shared/utils/diff'
import DiffView from '../ClientApps/dashboard/components/session/DiffView.vue'
import ToolCallCard from '../ClientApps/dashboard/components/session/ToolCallCard.vue'

const sample = [
  'diff --git a/src/App.cs b/src/App.cs',
  'index 1111111..2222222 100644',
  '--- a/src/App.cs',
  '+++ b/src/App.cs',
  '@@ -1,4 +1,4 @@',
  ' using System;',
  '-var x = 1;',
  '+var x = 2;',
  ' // middle',
  ' return x;',
  '@@ -20,2 +20,3 @@ class App',
  ' a',
  '+b',
  ' c',
  '\\ No newline at end of file',
  'diff --git a/docs/old.md b/docs/new.md',
  'similarity index 90%',
  'rename from docs/old.md',
  'rename to docs/new.md',
  'diff --git a/AGENTS.md b/AGENTS.md',
  'new file mode 100644',
  '--- /dev/null',
  '+++ b/AGENTS.md',
  '@@ -0,0 +1,2 @@',
  '+# AGENTS',
  '+hello',
  'diff --git a/gone.txt b/gone.txt',
  'deleted file mode 100644',
  '--- a/gone.txt',
  '+++ /dev/null',
  '@@ -1 +0,0 @@',
  '-bye',
  'diff --git a/logo.png b/logo.png',
  'Binary files a/logo.png and b/logo.png differ',
  '',
].join('\n')

describe('parseDiff', () => {
  const files = parseDiff(sample)

  it('finds every file with its status', () => {
    expect(files.map((f) => [f.status, f.oldPath, f.newPath, f.binary])).toEqual([
      ['modified', 'src/App.cs', 'src/App.cs', false],
      ['renamed', 'docs/old.md', 'docs/new.md', false],
      ['added', 'AGENTS.md', 'AGENTS.md', false],
      ['deleted', 'gone.txt', 'gone.txt', false],
      ['modified', 'logo.png', 'logo.png', true],
    ])
  })

  it('numbers lines across multiple hunks and counts changes', () => {
    const app = files[0]!
    expect(app.hunks).toHaveLength(2)
    expect(app.hunks[0]!.lines.map((l) => [l.kind, l.oldNo, l.newNo])).toEqual([
      ['context', 1, 1],
      ['del', 2, null],
      ['add', null, 2],
      ['context', 3, 3],
      ['context', 4, 4],
    ])
    expect(app.hunks[1]!.lines.map((l) => [l.kind, l.oldNo, l.newNo, l.text])).toEqual([
      ['context', 20, 20, 'a'],
      ['add', null, 21, 'b'],
      ['context', 21, 22, 'c'],
    ])
    expect([app.additions, app.deletions]).toEqual([2, 1])
  })

  it('marks the no-newline line', () => {
    expect(files[0]!.hunks[1]!.lines.at(-1)).toMatchObject({ text: 'c', noNewline: true })
  })

  it('builds before/after blocks for Edit and Write', () => {
    expect(blockDiff('a\nb', 'c').map((l) => `${l.kind}:${l.text}`)).toEqual(['del:a', 'del:b', 'add:c'])
    expect(blockDiff('', 'new').map((l) => l.kind)).toEqual(['add'])
  })
})

describe('DiffView', () => {
  it('lists the files and shows the selected one with tinted lines', async () => {
    const w = mount(DiffView, { props: { diff: { baseRef: 'origin/develop', headRef: 'ai/5613-x', files: ['src/App.cs'], unifiedDiff: sample, truncated: false } } })
    expect(w.findAll('[aria-label="Changed files"] button')).toHaveLength(5)
    expect(w.find('[data-kind="add"]').classes()).toContain('bg-success/10')
    expect(w.find('[data-kind="del"]').classes()).toContain('bg-error/10')

    await w.findAll('[aria-label="Changed files"] button')[1]!.trigger('click')
    expect(w.text()).toContain('docs/old.md → docs/new.md')
  })

  it('falls back to the file list when the diff is too big', () => {
    const w = mount(DiffView, { props: { diff: { baseRef: 'b', headRef: 'h', files: ['a.cs', 'b.cs'], unifiedDiff: null, truncated: true } } })
    expect(w.text()).toContain('too large')
    expect(w.text()).toContain('b.cs')
  })
})

describe('ToolCallCard inline diff', () => {
  it('shows an Edit as removed and added lines, expanded', () => {
    const call = { seq: 1, jobId: 7, ts: '2026-10-04T00:00:00Z', type: 'agent.tool_call', payload: { id: 't', name: 'Edit', inputJson: JSON.stringify({ file_path: 'a.cs', old_string: 'x = 1', new_string: 'x = 2' }) } }
    const w = mount(ToolCallCard, { props: { call } })
    expect(w.get('button').attributes('aria-expanded')).toBe('true')
    expect(w.find('[data-inline-diff]').exists()).toBe(true)
    expect(w.findAll('[data-kind]').map((r) => `${r.attributes('data-kind')}:${r.text().replace(/^[+-]\s*/, '')}`)).toEqual(['del:x = 1', 'add:x = 2'])
  })
})
