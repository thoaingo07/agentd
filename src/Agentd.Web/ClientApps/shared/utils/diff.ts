// A small parser for `git diff` output (unified format). No dependency, no HTML: the result is data
// that DiffView renders as text.

export type FileStatus = 'modified' | 'added' | 'deleted' | 'renamed'
export type LineKind = 'context' | 'add' | 'del'

export interface DiffLine {
  kind: LineKind
  oldNo: number | null
  newNo: number | null
  text: string
  /** The line had no newline at the end of the file ("\ No newline at end of file"). */
  noNewline?: boolean
}

export interface DiffHunk {
  header: string
  lines: DiffLine[]
}

export interface DiffFile {
  oldPath: string
  newPath: string
  status: FileStatus
  binary: boolean
  hunks: DiffHunk[]
  additions: number
  deletions: number
}

const hunkHeader = /^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/

/** "a/src/x.cs" → "src/x.cs"; quoted paths (spaces, unicode) lose their quotes. */
function cleanPath(raw: string): string {
  const unquoted = raw.startsWith('"') && raw.endsWith('"') ? raw.slice(1, -1) : raw
  return unquoted.replace(/^[ab]\//, '')
}

export function parseDiff(text: string): DiffFile[] {
  const files: DiffFile[] = []
  let file: DiffFile | null = null
  let hunk: DiffHunk | null = null
  let oldNo = 0
  let newNo = 0

  for (const line of text.replace(/\r\n/g, '\n').split('\n')) {
    if (line.startsWith('diff --git ')) {
      const m = /^diff --git (.+?) (b\/.+|"b\/.+")$/.exec(line)
      file = { oldPath: cleanPath(m?.[1] ?? ''), newPath: cleanPath(m?.[2] ?? ''), status: 'modified', binary: false, hunks: [], additions: 0, deletions: 0 }
      files.push(file)
      hunk = null
      continue
    }
    if (!file) continue

    if (hunk === null) {
      if (line.startsWith('new file mode')) file.status = 'added'
      else if (line.startsWith('deleted file mode')) file.status = 'deleted'
      else if (line.startsWith('rename from ')) {
        file.status = 'renamed'
        file.oldPath = cleanPath(line.slice('rename from '.length))
      } else if (line.startsWith('rename to ')) file.newPath = cleanPath(line.slice('rename to '.length))
      else if (line.startsWith('Binary files ') || line === 'GIT binary patch') file.binary = true
      else if (line.startsWith('--- ') && line !== '--- /dev/null') file.oldPath = cleanPath(line.slice(4))
      else if (line.startsWith('+++ ') && line !== '+++ /dev/null') file.newPath = cleanPath(line.slice(4))
    }

    const header = hunkHeader.exec(line)
    if (header) {
      hunk = { header: line, lines: [] }
      file.hunks.push(hunk)
      oldNo = Number(header[1])
      newNo = Number(header[2])
      continue
    }
    if (!hunk) continue

    if (line.startsWith('\\')) {
      const last = hunk.lines.at(-1)
      if (last) last.noNewline = true
    } else if (line.startsWith('+')) {
      hunk.lines.push({ kind: 'add', oldNo: null, newNo: newNo++, text: line.slice(1) })
      file.additions++
    } else if (line.startsWith('-')) {
      hunk.lines.push({ kind: 'del', oldNo: oldNo++, newNo: null, text: line.slice(1) })
      file.deletions++
    } else if (line.startsWith(' ')) {
      hunk.lines.push({ kind: 'context', oldNo: oldNo++, newNo: newNo++, text: line.slice(1) })
    }
  }

  for (const f of files) {
    if (f.status === 'added' && f.oldPath === '') f.oldPath = f.newPath
    if (f.status === 'deleted') f.newPath = f.oldPath
  }
  return files
}

/** The before/after of an Edit or Write tool call as diff lines (whole blocks; no line matching). */
export function blockDiff(before: string, after: string): DiffLine[] {
  const old = before === '' ? [] : before.split('\n')
  const next = after === '' ? [] : after.split('\n')
  return [
    ...old.map((text, i): DiffLine => ({ kind: 'del', oldNo: i + 1, newNo: null, text })),
    ...next.map((text, i): DiffLine => ({ kind: 'add', oldNo: null, newNo: i + 1, text })),
  ]
}
