// Materialise src-tauri/installer-hooks.nsh from the template.
//
// Tauri inlines bundle.windows.nsis.installerHooks into the generated
// installer.nsi, which lives under target/release/nsis/<target>/. A relative
// `File` path inside a hook therefore resolves against that build directory, not
// against the sources, and the installer fails with "no files found". The
// template marks the absolute path it needs with @@INSTALLER_DIR@@; this script
// fills it in, so the generated file is a build artifact (.gitignore) and hook
// paths are always correct on any machine.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const appDir = path.join(path.dirname(fileURLToPath(import.meta.url)), '..');
const template = path.join(appDir, 'src-tauri', 'installer-hooks.template.nsh');
const output = path.join(appDir, 'src-tauri', 'installer-hooks.nsh');
const installerDir = path.join(appDir, 'src-tauri', 'installer');

if (!fs.existsSync(template)) {
  console.error(`missing template: ${template}`);
  process.exit(1);
}
for (const name of ['detect-harness.ps1', 'deploy-harness.ps1']) {
  const file = path.join(installerDir, name);
  if (!fs.existsSync(file)) {
    console.error(`missing installer script: ${file}`);
    process.exit(1);
  }
}

const text = fs
  .readFileSync(template, 'utf8')
  .replaceAll('@@INSTALLER_DIR@@', installerDir.replaceAll('/', '\\'));

if (text.includes('@@INSTALLER_DIR@@')) {
  console.error('template still contains an unsubstituted placeholder');
  process.exit(1);
}

// Only rewrite when the content changes: an unchanged file keeps the bundle step
// incremental.
const previous = fs.existsSync(output) ? fs.readFileSync(output, 'utf8') : '';
if (previous !== text) {
  fs.writeFileSync(output, text, 'utf8');
  console.log(`wrote ${path.relative(appDir, output)}`);
} else {
  console.log(`${path.relative(appDir, output)} is up to date`);
}
