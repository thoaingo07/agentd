import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import DiffFileReview from '../ClientApps/shared/review/DiffFileReview.vue'
import { parseDiff } from '../ClientApps/shared/utils/diff'

// View file (review-sessions.md §2): a changed file whole at the review's commit, its added lines marked.
const changed = parseDiff('diff --git a/src/Sync.cs b/src/Sync.cs\n--- a/src/Sync.cs\n+++ b/src/Sync.cs\n@@ -2,3 +2,4 @@\n line two\n-var rows = ReadAll();\n+var cursor = Keyset();\n+flush();\n line five\n')[0]!
const removed = parseDiff('diff --git a/src/Old.cs b/src/Old.cs\ndeleted file mode 100644\n--- a/src/Old.cs\n+++ /dev/null\n@@ -1,2 +0,0 @@\n-class Old {\n-}\n')[0]!
let asked: string[]

beforeEach(() => {
  asked = []
  vi.stubGlobal('fetch', vi.fn(async (req: Request) => {
    const url = new URL(req.url)
    asked.push(url.pathname + url.search)
    const text = url.searchParams.get('ref') === 'base' ? 'class Old {\n}' : 'line one\nline two\nvar cursor = Keyset();\nflush();\nline five\nline six'
    return new Response(JSON.stringify({ path: url.searchParams.get('path'), text, truncated: false, binary: false }))
  }))
})
afterEach(() => vi.unstubAllGlobals())

describe('View file', () => {
  it('shows the whole file at the head commit with the added lines marked, then goes back to the diff', async () => {
    const w = mount(DiffFileReview, { props: { file: changed, findings: [], comments: [], readonly: true, sessionId: 7 }, attachTo: document.body })
    expect(w.find('table').exists()).toBe(true)

    await w.get('[data-testid=view-file]').trigger('click')
    await flushPromises()

    expect(asked).toEqual(['/api/reviews/7/file?path=src%2FSync.cs&ref=head'])
    const lines = w.findAll('.ag-code-line')
    expect(lines).toHaveLength(6)
    expect(lines.filter((l) => l.classes().includes('is-hit')).map((l) => l.attributes('data-line'))).toEqual(['3', '4'])
    expect(w.find('table').exists(), 'the diff is out of the way').toBe(false)
    expect(w.get('[data-testid=view-file]').text()).toContain('Back to the diff')

    await w.get('[data-testid=view-file]').trigger('click')
    expect(w.find('[data-testid=whole-file]').exists()).toBe(false)
    expect(w.find('table').exists()).toBe(true)
    await w.get('[data-testid=view-file]').trigger('click')
    await flushPromises()
    expect(asked, 'read once').toHaveLength(1)
    w.unmount()
  })

  it('a deleted file is read at the base commit with its removed lines marked', async () => {
    const w = mount(DiffFileReview, { props: { file: removed, findings: [], comments: [], sessionId: 7 } })

    await w.get('[data-testid=view-file]').trigger('click')
    await flushPromises()

    expect(asked).toEqual(['/api/reviews/7/file?path=src%2FOld.cs&ref=base'])
    expect(w.findAll('.ag-code-line.is-hit')).toHaveLength(2)
    expect(w.get('[data-testid=whole-file]').text()).toContain('before this change deleted it')
  })

  it('without a session (a laptop review) there is no View file', () => {
    const w = mount(DiffFileReview, { props: { file: changed, findings: [], comments: [] } })

    expect(w.find('[data-testid=view-file]').exists()).toBe(false)
  })
})
