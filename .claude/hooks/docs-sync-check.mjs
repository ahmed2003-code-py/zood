// Stop hook: when the session stops with code changes that no document or memory note has
// followed, ask once (per set of changed files) for zawed-docs-sync to run.
// It never blocks twice in a row (stop_hook_active) and stays quiet for the same file set.
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const root = process.env.CLAUDE_PROJECT_DIR || process.cwd();

let input = {};
try {
  input = JSON.parse(readFileSync(0, 'utf8') || '{}');
} catch {
  input = {};
}
if (input.stop_hook_active) process.exit(0);

try {
  const status = execFileSync('git', ['status', '--porcelain', '--untracked-files=all'], { cwd: root, encoding: 'utf8' });
  const paths = status
    .split('\n')
    .map((l) => l.slice(3).trim().replace(/^"|"$/g, ''))
    .filter(Boolean);
  const code = paths.filter((p) => /^apps\/(api\/src|web\/src|web\/e2e)\//.test(p)).sort();
  if (code.length === 0) process.exit(0);
  const docs = paths.some((p) => /^(docs\/|README\.md|apps\/[^/]+\/README\.md|CLAUDE\.md)/.test(p));

  const gitDir = execFileSync('git', ['rev-parse', '--git-dir'], { cwd: root, encoding: 'utf8' }).trim();
  const stateFile = join(root, gitDir, 'zawed-docs-sync.state');
  const fingerprint = createHash('sha1').update(code.join('\n')).digest('hex');
  const last = existsSync(stateFile) ? readFileSync(stateFile, 'utf8').trim() : '';
  if (last === fingerprint) process.exit(0);
  writeFileSync(stateFile, fingerprint);

  const reason = [
    `${code.length} code file(s) changed in the working tree${docs ? '' : ' and no document changed with them'}.`,
    'Run the zawed-docs-sync skill now: decide which docs, ADRs, contracts, summaries, memory notes and diagrams this change makes wrong, update them, refresh the code graph.',
    'If nothing needs updating, say so in one line and why. This reminder will not repeat for the same set of files.',
  ].join(' ');
  process.stdout.write(JSON.stringify({ decision: 'block', reason }));
} catch {
  // Never break a session over a reminder.
}
process.exit(0);
