// Files nobody reads line by line: lockfiles, minified or built output, and code generators' files. The review page hides
// their diffs until asked (review-sessions.md §2a, step 5); a file with a finding is always shown.
const patterns = [
  /(^|\/)(package-lock\.json|npm-shrinkwrap\.json|yarn\.lock|pnpm-lock\.yaml|packages\.lock\.json|Cargo\.lock|poetry\.lock|Pipfile\.lock|composer\.lock|Gemfile\.lock|go\.sum)$/,
  /\.min\.(js|css)$/,
  /\.map$/,
  /\.(g|g\.i|designer|generated)\.cs$/i,
  /ModelSnapshot\.cs$/,
  /(_pb2\.py|\.pb\.go)$/,
  /(^|\/)(dist|vendor|node_modules)\//,
]

export const isGenerated = (path: string): boolean => patterns.some((p) => p.test(path))
