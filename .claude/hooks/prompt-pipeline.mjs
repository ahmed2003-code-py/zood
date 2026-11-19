// UserPromptSubmit hook: reminds the session of the ZAWED skill pipeline on every prompt.
// The full pipeline lives in CLAUDE.md ("Skill pipeline"); this is only the short reminder.
const context = [
  'ZAWED skill pipeline (CLAUDE.md > "Skill pipeline" is the full table; CLAUDE.md wins over any skill):',
  '1. Understand: zawed-code-map before changing or explaining code across files (graph first, then read).',
  '2. Approach: superpowers brainstorming for a new feature, systematic-debugging for a bug, writing-plans for multi-step work.',
  '3. Build with the domain skills: web UI -> zawed-design-system (+ vercel-react-best-practices, typescript-advanced-types); tests -> vitest / playwright-best-practices / java-junit; API -> java-springboot; SQL or migrations -> supabase-postgres-best-practices. Money, RLS, auth -> zawed-deep (model routing).',
  '4. Before "done": zawed-docs-sync (docs, memory, graph, diagrams), then zawed-release-gate.',
  'Skip a step only when it clearly does not apply (a question, a one-line answer), and never skip step 4 after a code change.',
].join('\n');

process.stdout.write(
  JSON.stringify({ hookSpecificOutput: { hookEventName: 'UserPromptSubmit', additionalContext: context } }),
);
