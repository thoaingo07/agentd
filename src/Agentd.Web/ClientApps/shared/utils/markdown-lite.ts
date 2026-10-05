// Markdown for agent output, rendered as VNodes, never as an HTML string (no v-html; docs/security §3.4).
// Supported: paragraphs, "- " lists, fenced code, `code`, **bold**, and http(s) links. Everything else
// is plain text, so <script>, <img onerror> or javascript: links come out inert.
import { h, type VNode } from 'vue'

const inline = /(\*\*[^*]+\*\*|`[^`]+`|\[[^\]]+\]\([^)\s]+\)|https?:\/\/[^\s)<>]+)/g

/** Only http(s) URLs become links; anything else (javascript:, data:, relative) is shown as text. */
export function safeUrl(url: string): string | null {
  try {
    const u = new URL(url)
    return u.protocol === 'http:' || u.protocol === 'https:' ? u.href : null
  } catch {
    return null
  }
}

function link(text: string, href: string): VNode | string {
  const url = safeUrl(href)
  return url ? h('a', { href: url, target: '_blank', rel: 'noopener noreferrer', class: 'link link-primary break-all' }, text) : text
}

export function renderInline(text: string): (VNode | string)[] {
  const out: (VNode | string)[] = []
  let last = 0
  for (const m of text.matchAll(inline)) {
    if (m.index > last) out.push(text.slice(last, m.index))
    const t = m[0]
    if (t.startsWith('**')) out.push(h('strong', renderInline(t.slice(2, -2))))
    else if (t.startsWith('`')) out.push(h('code', { class: 'rounded bg-base-200 px-1 font-mono text-[13px]' }, t.slice(1, -1)))
    else if (t.startsWith('[')) {
      const [, label, href] = /^\[([^\]]+)\]\(([^)\s]+)\)$/.exec(t)!
      out.push(link(label!, href!))
    } else out.push(link(t, t))
    last = m.index + t.length
  }
  if (last < text.length) out.push(text.slice(last))
  return out
}

export function renderMarkdown(source: string): VNode[] {
  const blocks: VNode[] = []
  const lines = source.replace(/\r\n/g, '\n').split('\n')
  let paragraph: string[] = []
  let list: string[] = []
  const flush = () => {
    if (paragraph.length) blocks.push(h('p', renderInline(paragraph.join(' '))))
    if (list.length) blocks.push(h('ul', { class: 'list-disc pl-5' }, list.map((item) => h('li', renderInline(item)))))
    paragraph = []
    list = []
  }

  for (let i = 0; i < lines.length; i++) {
    const line = lines[i]!
    const fence = /^```(\S*)/.exec(line)
    if (fence) {
      flush()
      const code: string[] = []
      while (++i < lines.length && !lines[i]!.startsWith('```')) code.push(lines[i]!)
      blocks.push(h('pre', { class: 'overflow-x-auto rounded-box bg-base-200 p-3 font-mono text-[13px]', 'data-lang': fence[1] || undefined }, h('code', code.join('\n'))))
    } else if (/^\s*[-*] /.test(line)) {
      if (paragraph.length) flush()
      list.push(line.replace(/^\s*[-*] /, ''))
    } else if (line.trim() === '') {
      flush()
    } else {
      if (list.length) flush()
      paragraph.push(line.trim())
    }
  }
  flush()
  return blocks
}
