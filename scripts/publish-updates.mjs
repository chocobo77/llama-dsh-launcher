// Publish the Tauri updater feed, mirroring apps/desktop's COS conventions
// (desktop-auto-update-environment.mjs): same env contract, region, and
// deployment presets, under a separate `dsh-desk-tauri` object prefix so the
// two updaters never share manifests.
//
// Modes:
//   node scripts/publish-updates.mjs                 # stage dist-update/ locally
//   node scripts/publish-updates.mjs --cos           # also upload to Tencent COS
// Options: --env test|production (default test), --notes "release notes"
//
// COS credentials follow the Electron shell exactly:
//   test:       DOWNLOAD_TEST_ORIGIN (public https origin),
//               DOWNLOAD_TEST_COS_BUCKET, DOWNLOAD_TEST_COS_SECRET_ID/KEY,
//               DOWNLOAD_TEST_RELEASE_ID (32-hex isolation id)
//   production: fixed origin https://download.deepseek.com,
//               DOWNLOAD_PROD_COS_BUCKET, DOWNLOAD_PROD_COS_SECRET_ID/KEY
//
// The URL layout for the installed app (prefix dsh-desk-tauri):
//   feed:  <origin>/dsh-desk-tauri/feeds/win-x64/latest.json
//   bin:   <origin>/dsh-desk-tauri/bin/win-x64/DSH_<version>_x64-setup.exe
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';

const appDir = path.join(path.dirname(fileURLToPath(import.meta.url)), '..');
// Tauri resolves the app version from tauri.conf.json — single source of truth.
const VERSION = /"version"\s*:\s*"([^"]+)"/u.exec(
  fs.readFileSync(path.join(appDir, 'src-tauri', 'tauri.conf.json'), 'utf8'),
)[1];
const argv = process.argv.slice(2);

const getOpt = (name, dflt) => {
  const i = argv.indexOf(`--${name}`);
  return i >= 0 ? argv[i + 1] : dflt;
};

const envName = getOpt('env', process.env.DSH_TAURI_UPDATE_ENV || 'test');
if (envName !== 'test' && envName !== 'production') throw new Error(`unknown --env ${envName}`);
const cosMode = argv.includes('--cos');
const notes = getOpt('notes', `DSH Desktop ${VERSION}`);
const PREFIX = 'dsh-desk-tauri';
const PLATFORM = 'windows-x86_64';

const required = (name) => {
  const v = (process.env[name] ?? '').trim();
  if (!v) throw new Error(`${name} must be set`);
  return v;
};

let origin;
let bucket;
let releasePrefix = PREFIX;
if (envName === 'production') {
  origin = 'https://download.deepseek.com';
  bucket = required('DOWNLOAD_PROD_COS_BUCKET');
} else {
  origin = required('DOWNLOAD_TEST_ORIGIN').replace(/\/+$/, '');
  if (!origin.startsWith('https://')) throw new Error('DOWNLOAD_TEST_ORIGIN must be https');
  const releaseId = required('DOWNLOAD_TEST_RELEASE_ID');
  if (!/^[a-f0-9]{32}$/u.test(releaseId)) throw new Error('DOWNLOAD_TEST_RELEASE_ID must be 32 lowercase hex chars');
  releasePrefix += `/${releaseId}`;
}

const bundleDir = path.join(appDir, 'src-tauri', 'target', 'release', 'bundle', 'nsis');
const setupName = `DSH_${VERSION}_x64-setup.exe`;
const setupPath = path.join(bundleDir, setupName);
const sigPath = `${setupPath}.sig`;
for (const f of [setupPath, sigPath]) {
  if (!fs.existsSync(f)) throw new Error(`missing artifact: ${f} (run: pnpm run build)`);
}

const feedKey = `${releasePrefix}/feeds/win-x64/latest.json`;
const binKey = `${releasePrefix}/bin/win-x64/${setupName}`;
const feedUrl = `${origin}/${feedKey}`;
const binUrl = `${origin}/${binKey}`;

const latest = {
  version: VERSION,
  notes,
  pub_date: new Date().toISOString().replace(/\.\d+Z$/, 'Z'),
  platforms: {
    [PLATFORM]: { signature: fs.readFileSync(sigPath, 'utf8').trim(), url: binUrl },
  },
};

const outDir = path.join(appDir, 'dist-update');
fs.rmSync(outDir, { recursive: true, force: true });
fs.mkdirSync(outDir, { recursive: true });
fs.copyFileSync(setupPath, path.join(outDir, setupName));
fs.copyFileSync(sigPath, path.join(outDir, `${setupName}.sig`));
fs.writeFileSync(path.join(outDir, 'latest.json'), `${JSON.stringify(latest, null, 2)}\n`);

console.log('staged dist-update/:');
console.log(`  feed: ${feedUrl}`);
console.log(`  bin:  ${binUrl}`);
console.log('端点（tauri.conf.json plugins.updater.endpoints 应包含）:');
console.log(`  ${origin}/${PREFIX}/feeds/win-x64/latest.json`);

if (!cosMode) {
  console.log('\n--cos 未指定：仅本地 staging。上传 COS 需凭据环境变量（见脚本头部）。');
  process.exit(0);
}

const secretId = required(envName === 'production' ? 'DOWNLOAD_PROD_COS_SECRET_ID' : 'DOWNLOAD_TEST_COS_SECRET_ID');
const secretKey = required(envName === 'production' ? 'DOWNLOAD_PROD_COS_SECRET_KEY' : 'DOWNLOAD_TEST_COS_SECRET_KEY');

// The COS SDK used to be borrowed from the monorepo install (apps/desktop ships
// it, and `noop.js` was the resolution anchor). Standalone, prefer this
// project's own install and fall back to a named checkout — so a missing
// dependency fails here with an instruction instead of inside the SDK.
let COS;
const anchors = [
  import.meta.url,
  process.env.DSH_REPO_ROOT
    ? path.join(path.resolve(process.env.DSH_REPO_ROOT), 'apps', 'desktop', 'noop.js')
    : null,
];
for (const anchor of anchors) {
  if (!anchor) continue;
  try {
    COS = createRequire(anchor)('cos-nodejs-sdk-v5');
    break;
  } catch {
    // try the next anchor
  }
}
if (!COS) {
  console.error(
    'cos-nodejs-sdk-v5 不可用：在本项目里装一个（pnpm add -D cos-nodejs-sdk-v5），' +
      `或设 DSH_REPO_ROOT 指向 DSH 仓库（试过：${anchors.filter(Boolean).join(' , ')}）`
  );
  process.exit(1);
}
const cos = new COS({
  SecretId: secretId, SecretKey: secretKey, Protocol: 'https:', KeepAlive: false,
  FollowRedirect: false, AutoSwitchHost: false, CorrectClockSkew: false,
  ChunkRetryTimes: 0, Timeout: 900_000, UploadCheckContentMd5: false,
});

const put = async (key, file, { forbidOverwrite = false, contentType } = {}) => {
  const bytes = fs.readFileSync(file);
  const md5 = createHash('md5').update(bytes).digest('base64');
  await cos.putObject({
    Bucket: bucket, Region: 'ap-beijing', Key: key, Body: bytes,
    ContentLength: bytes.length, ContentType: contentType ?? 'application/octet-stream',
    Headers: { 'Content-MD5': md5, ...(forbidOverwrite ? { 'x-cos-forbid-overwrite': 'true' } : {}) },
  });
  console.log(`uploaded ${key}`);
};

// Binary first (versioned, overwrite-forbidden), then the feed pointer switch.
await put(binKey, setupPath, { forbidOverwrite: true });
await put(`${binKey}.sig`, sigPath, { forbidOverwrite: true, contentType: 'text/plain' });
await put(feedKey, path.join(outDir, 'latest.json'), { contentType: 'application/json' });
console.log('published.');
