// Stop hook (async): keeps the local Graphify code graph in step with uncommitted code changes.
// Git hooks (graphify hook install) already rebuild it after commit/checkout/merge/rewrite;
// this covers work in progress. Code only, AI backends off, nothing leaves the machine.
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, statSync } from 'node:fs';
import { join } from 'node:path';

const root = process.env.CLAUDE_PROJECT_DIR || process.cwd();
const graph = join(root, 'graphify-out', 'graph.json');
const CODE = /^apps\/.*\.(java|ts|tsx|js|mjs|sql)$/;

function graphifyBin() {
  const candidates = [
    process.env.GRAPHIFY_BIN,
    join(process.env.APPDATA || '', 'Python', 'Python312', 'Scripts', 'graphify.exe'),
  ].filter(Boolean);
  return candidates.find((p) => existsSync(p)) || 'graphify';
}

try {
  if (!existsSync(graph)) process.exit(0); // never built on this machine: zawed-code-map explains the full build
  const builtAt = statSync(graph).mtimeMs;
  const status = execFileSync('git', ['status', '--porcelain', '--untracked-files=all'], { cwd: root, encoding: 'utf8' });
  const changed = status
    .split('\n')
    .map((l) => l.slice(3).trim().replace(/^"|"$/g, ''))
    .filter((p) => CODE.test(p))
    .some((p) => existsSync(join(root, p)) && statSync(join(root, p)).mtimeMs > builtAt);
  if (!changed) process.exit(0);
  spawnSync(graphifyBin(), ['update', '.'], {
    cwd: root,
    stdio: 'ignore',
    timeout: 120_000,
    env: { ...process.env, GRAPHIFY_BACKEND: 'none', GRAPHIFY_QUERY_LOG_DISABLE: '1', PYTHONHASHSEED: '0' },
  });
} catch {
  // A graph refresh must never break a session.
}
process.exit(0);
