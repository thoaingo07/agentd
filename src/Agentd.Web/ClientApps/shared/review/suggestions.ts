import type { GuideSuggestion, ReviewComment } from '../api/types'

// Keeping a guide suggestion (review-sessions.md §2a, step 3) makes it one of your comments: Send posts it and Fix it
// applies it, like any comment you wrote. It stays editable and removable there.

/** The comment a kept suggestion becomes. */
export function suggestionComment(g: GuideSuggestion): string {
  return [`✨ Suggestion: ${g.title}`, g.detail, g.suggestion && `Suggested change: ${g.suggestion}`].filter(Boolean).join('\n\n')
}

/** The comment a suggestion was kept as, if it's still there: same place, starting with its title line. */
export function keptAs(g: GuideSuggestion, comments: readonly ReviewComment[]): ReviewComment | undefined {
  const first = `✨ Suggestion: ${g.title}`
  return comments.find((c) => (c.file ?? null) === (g.file ?? null) && (c.line ?? null) === (g.line ?? null) && (c.text === first || c.text.startsWith(`${first}\n`)))
}
