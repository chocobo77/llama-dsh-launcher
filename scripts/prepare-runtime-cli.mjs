// Prepare the self-contained dsh runtime for the Tauri bundle.
//
// Produces (under apps/desktop-tauri, both gitignored and wired through
// tauri.conf.json `bundle.resources`):
//   runtime-cli/     production closure of @deepseek-ai/dsh (hoisted layout)
//   runtime-node/    portable node.exe (the one running this script)
//
// Usage: node scripts/prepare-runtime-cli.mjs   (from anywhere; paths are fixed)
// Then:  pnpm tauri build
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const appDir = path.join(here, '..');
const repoRoot = path.join(appDir, '..', '..');
const cliOut = path.join(appDir, 'runtime-cli');
const nodeOut = path.join(appDir, 'runtime-node');
// Staging directories: the closure is built *and verified* here, and only
// then swapped into place. Wiping runtime-cli/ before the deploy could leave
// the bundle with an empty closure whenever the deploy failed (on-site
// incident), and the verify guard silently passes on an empty placeholder.
const cliStage = path.join(appDir, 'runtime-cli.next');
const nodeStage = path.join(appDir, 'runtime-node.next');

// App packages that must never enter the CLI runtime closure (their links
// would copy build trees back into the bundle).
const EXCLUDED = new Set(['dsh-desktop-tauri', 'dsh-desktop']);

function rmrf(p) {
  fs.rmSync(p, { recursive: true, force: true });
}

console.log('deploying @deepseek-ai/dsh production closure (hoisted)…');
rmrf(cliStage);
// forceLegacyDeploy: the shared-lockfile deploy path is flaky here.
// strictDepBuilds=false: deploy re-specifies workspace `file:` deps with
// absolute file:/// URLs, which the workspace allowBuilds entries (written
// relative) cannot match, so their lifecycle scripts would hard-error. The
// scripts' outputs (if any) already exist in the workspace copies we
// materialize below, so warnings are safe.
// Spawn pnpm through its Node entry when the npm-installed shim exposes one
// (spawning the .cmd shim from Node hits EINVAL under shell-less execFileSync).
function pnpmSpawn() {
  const appdataPnpm = process.env.APPDATA === undefined ? [] : [
    path.join(process.env.APPDATA, 'npm', 'node_modules', 'pnpm', 'bin', 'pnpm.cjs'),
  ];
  const candidates = [process.env.npm_execpath, ...appdataPnpm, 'pnpm'].filter((p) => p !== undefined && p !== '');
  for (const candidate of candidates) {
    if (candidate.endsWith('.cjs') || candidate.endsWith('.js')) {
      if (fs.existsSync(candidate)) return { file: process.execPath, args: [candidate] };
    } else {
      try {
        execFileSync(candidate, ['--version'], { stdio: 'ignore' });
        return { file: candidate, args: [] };
      } catch { /* try the next form */ }
    }
  }
  throw new Error('cannot locate a spawnable pnpm (npm_execpath unset and no shim works)');
}
const pnpm = pnpmSpawn();
execFileSync(
  pnpm.file,
  [
    ...pnpm.args,
    '--filter',
    '@deepseek-ai/dsh',
    'deploy',
    '--prod',
    '--config.node-linker=hoisted',
    '--config.allowUnusedPatches=true',
    '--config.forceLegacyDeploy=true',
    '--config.strictDepBuilds=false',
    cliStage,
  ],
  { cwd: repoRoot, stdio: 'inherit' },
);

const nmRoot = path.join(cliStage, 'node_modules');
const scoped = path.join(nmRoot, '@deepseek-ai');
for (const name of EXCLUDED) rmrf(path.join(scoped, name));

// pnpm leaves workspace deps as symlinks pointing into the checkout; a
// packaged app can't resolve those. Replace each with a real copy (skipping
// the target's own node_modules — its deps are already hoisted).
function walkLinks(dir) {
  const out = [];
  for (const ent of fs.readdirSync(dir, { withFileTypes: true })) {
    if (!ent.isDirectory() && !ent.isSymbolicLink()) continue;
    const full = path.join(dir, ent.name);
    if (ent.isSymbolicLink()) out.push(full);
    else out.push(...walkLinks(full));
  }
  return out;
}

const links = walkLinks(nmRoot);
console.log(`materializing ${links.length} symlinked packages…`);
for (const link of links) {
  const target = fs.realpathSync(link);
  fs.rmSync(link);
  fs.cpSync(target, link, {
    recursive: true,
    dereference: true,
    filter: (src) => {
      // inside the copied package, skip nested node_modules of the source
      const rel = path.relative(target, src);
      return rel === '' || !rel.split(path.sep).includes('node_modules');
    },
  });
}

const remaining = walkLinks(nmRoot).length;
if (remaining > 0) throw new Error(`${remaining} symlinks remain after materialization`);

console.log('bundling portable node…');
rmrf(nodeStage);
fs.mkdirSync(nodeStage, { recursive: true });
fs.copyFileSync(process.execPath, path.join(nodeStage, 'node.exe'));
console.log(`node: ${process.execPath} (${process.version}) -> runtime-node/node.exe`);

const size = (function du(dir) {
  let total = 0;
  for (const ent of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, ent.name);
    total += ent.isDirectory() ? du(full) : fs.statSync(full).size;
  }
  return total;
})(cliStage);
console.log(`runtime-cli staged: ${(size / 1048576).toFixed(0)} MB`);

// Verify the staged closure before anything in place is touched.
execFileSync(
  process.execPath,
  [path.join(here, 'verify-runtime-cli.mjs'), `--cli=${cliStage}`, `--node=${nodeStage}`],
  { stdio: 'inherit' },
);

// Swap into place, keeping the previous closure as `*.old` until the new one
// is fully in position, and restoring it if the swap itself fails.
function swap(stageDir, finalDir) {
  const backup = `${finalDir}.old`;
  rmrf(backup);
  if (fs.existsSync(finalDir)) fs.renameSync(finalDir, backup);
  try {
    fs.renameSync(stageDir, finalDir);
  } catch (error) {
    if (fs.existsSync(backup)) fs.renameSync(backup, finalDir);
    throw error;
  }
  rmrf(backup);
}

swap(cliStage, cliOut);
swap(nodeStage, nodeOut);
console.log('runtime-cli and runtime-node swapped into place.');
