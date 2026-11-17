// Regression guard for the packaged dsh runtime closure (runtime-cli/).
//
// Checks the invariants that pnpm deploy silently breaks (see README):
// hoisted real-directory layout, linked workspace materialization, the
// web frontend dist, app self-links excluded, natives present — plus a
// functional probe running `bin.js --version` from inside the closure.
//
// Placeholder mode: an empty runtime-cli/ (fresh clone) exits 0 with a
// notice — the shell then falls back to checkout/PATH resolution.
// Exit 1 on any hard failure.
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const appDir = path.join(path.dirname(fileURLToPath(import.meta.url)), '..');
// Optional overrides so a staged closure can be verified before it replaces
// the working one (see prepare-runtime-cli.mjs):
//   --cli=<dir>  closure root,  --node=<dir>  runtime-node directory
const argDir = (name) => {
  const flag = `--${name}=`;
  const hit = process.argv.find((a) => a.startsWith(flag));
  // Resolve here: the closure probe spawns the node executable by path, and
  // a relative path is not what `spawnSync` wants on Windows.
  return hit === undefined ? undefined : path.resolve(hit.slice(flag.length));
};
const cliRoot = argDir('cli') ?? path.join(appDir, 'runtime-cli');
const nodeExe = path.join(argDir('node') ?? path.join(appDir, 'runtime-node'), 'node.exe');

const failures = [];
const notes = [];
const check = (cond, label) => {
  if (cond) notes.push(`ok   ${label}`);
  else failures.push(`FAIL ${label}`);
};

if (!fs.existsSync(path.join(cliRoot, 'package.json'))) {
  console.log('runtime-cli/ is a placeholder (fresh clone): skipping closure checks.');
  console.log('Run `pnpm run prepare:runtime` to bundle the dsh runtime.');
  process.exit(0);
}

// 1) Root package identity.
const pkg = JSON.parse(fs.readFileSync(path.join(cliRoot, 'package.json'), 'utf8'));
check(pkg.name === '@deepseek-ai/dsh', `root package is @deepseek-ai/dsh (got ${pkg.name})`);
check(fs.existsSync(path.join(cliRoot, 'lib', 'bin.js')), 'lib/bin.js present');

const nm = path.join(cliRoot, 'node_modules');
const scoped = path.join(nm, '@deepseek-ai');

// 2) No symlinks anywhere: packaged installs cannot resolve checkout paths.
(function walkLinks(dir) {
  for (const ent of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, ent.name);
    if (ent.isSymbolicLink()) failures.push(`FAIL symlink survives packaging: ${path.relative(cliRoot, full)}`);
    else if (ent.isDirectory()) walkLinks(full);
  }
})(nm);
notes.push('ok   symlink scan complete');

// 3) App self-links must not drag build trees into the bundle.
for (const banned of ['dsh-desktop', 'dsh-desktop-tauri']) {
  check(!fs.existsSync(path.join(scoped, banned)), `app self-link ${banned} excluded`);
}

// 4) Web frontend with its built dist (dsh web serves it via require.resolve).
const feDist = path.join(scoped, 'dsh-web-frontend', 'dist', 'index.html');
check(fs.existsSync(feDist), 'dsh-web-frontend dist/index.html present');
check(fs.existsSync(path.join(scoped, 'dsh-web-app', 'package.json')), 'dsh-web-app bundle present');

// 5) Every declared @deepseek-ai/* production dependency resolved in the tree.
const wsDeps = Object.keys(pkg.dependencies ?? {}).filter((n) => n.startsWith('@deepseek-ai/'));
const missing = wsDeps.filter((n) => !fs.existsSync(path.join(scoped, n.replace('@deepseek-ai/', ''), 'package.json')));
check(missing.length === 0, `${wsDeps.length} workspace deps resolved${missing.length ? `; missing: ${missing.join(', ')}` : ''}`);

// 6) Platform natives that silently no-op when stripped.
const natives = [
  ['koffi', 'koffi'],
  ['node-pty', 'node-pty'],
  ['@deepseek-ai/libreoffice-kit-win32-x64', 'libreoffice-kit-win32-x64'],
];
for (const [dep, dir] of natives) {
  if (pkg.dependencies?.[dep] === undefined) continue;
  check(fs.existsSync(path.join(nm, dir, 'package.json')), `native dep ${dep} present`);
}

// 7) Functional probe: run the closure's own bin.js from inside it.
if (fs.existsSync(nodeExe)) {
  try {
    const out = execFileSync(nodeExe, [path.join(cliRoot, 'lib', 'bin.js'), '--version'], {
      cwd: cliRoot,
      timeout: 60_000,
      encoding: 'utf8',
      env: { ...process.env, DSH_NO_TELEMETRY: '1' },
    });
    check(/^\d+\.\d+/.test(out.trim()) || out.trim().length > 0, `bin.js --version probe: ${out.trim().slice(0, 40)}`);
  } catch (error) {
    failures.push(`FAIL bin.js --version probe: ${String(error.message).slice(0, 200)}`);
  }
} else {
  notes.push('note runtime-node/node.exe absent; functional probe skipped (PATH node will be used)');
}

// 8) Client builds must carry the inlined DSH_CLIENT_VERSION: the account UI
//    throws at the first sign-in click when the build left the define out
//    (`process.env.DSH_CLIENT_VERSION` collapses to `({}).DSH_CLIENT_VERSION`).
let uninlined = 0;
for (const ent of fs.readdirSync(scoped, { withFileTypes: true })) {
  if (!ent.isDirectory()) continue;
  const client = path.join(scoped, ent.name, 'lib', 'client.js');
  if (!fs.existsSync(client)) continue;
  if (fs.readFileSync(client, 'utf8').includes('({}).DSH_CLIENT_VERSION')) {
    uninlined += 1;
    failures.push(`FAIL ${ent.name}/lib/client.js has no inlined DSH_CLIENT_VERSION (login will fail at click)`);
  }
}
if (uninlined === 0) notes.push('ok   DSH_CLIENT_VERSION define scan complete');

for (const line of notes) console.log(line);
if (failures.length) {
  for (const line of failures) console.error(line);
  console.error(`runtime-cli verification FAILED (${failures.length} problem(s))`);
  process.exit(1);
}
console.log('runtime-cli verification passed.');
