# 05 · Node 脚本与安装器

## 1. scripts/prepare-installer-hooks.mjs（48 行）

把 `src-tauri/installer-hooks.template.nsh` 里的 `@@INSTALLER_DIR@@` 替换为
安装器目录的绝对路径（正斜杠），写出 `src-tauri/installer-hooks.nsh`
（gitignored，tauri.conf.json 的 `installerHooks` 指它）。
- 两个 `.ps1` 缺失时直接报错退出（防止装出一个没有挂载逻辑的安装器）；
- 内容没变化就不重写（避免每次 build 弄脏时间戳）。
`pnpm run dev/build/build:debug` 都先跑它；也可单独 `pnpm run installer:hooks`。

## 2. scripts/prepare-runtime-cli.mjs（172 行）—— vendored 运行时闭包

为“自带运行时”的安装包制备 `runtime-cli/`（@deepseek-ai/dsh 生产闭包）+
`runtime-node/`（当前 node.exe 副本）。常规挂载式安装包不需要。

- 需要 monorepo 检出：`DSH_REPO_ROOT` 必须设置（独立项目后没有别的找法），
  在其上重新生成闭包。
- 暂存目录 `runtime-cli.next` / `runtime-node.next` → 校验 → 交换：旧目录改名
  `.old` 保留，失败自动还原（制备中断绝不留下半套运行时）。
- 闭包命令：`pnpm deploy --prod --config.node-linker=hoisted
  --config.allowUnusedPatches=true --config.forceLegacyDeploy=true
  --config.strictDepBuilds=false`；调 pnpm 经 `pnpmSpawn`（npm_execpath /
  APPDATA 下 .cjs / node 入口逐级找）。
- `EXCLUDED = dsh-desktop-tauri, dsh-desktop`：两个桌面壳不进闭包。
- 符号链接**实体化**（拷贝真实目录，跳过嵌套 node_modules）——Windows 上
  node_modules 里的 symlink 布局在 hoisted 部署后必须落成真实目录。
- 复制**正在运行的 node.exe** 进 runtime-node（保证与制备时行为一致）。
- swap 前调用 verify（见 §3）；不过就不交换。

## 3. scripts/verify-runtime-cli.mjs（125 行）—— 闭包回归门

对 `runtime-cli/` 逐项体检（占位模式下打桩 exit 0）：

1. 根 package 是 `@deepseek-ai/dsh` 且有 `lib/bin.js`；
2. 闭包内 **symlink 数量为零**；禁止出现自指链接；
3. `dsh-web-frontend` 有 `dist/index.html`，且 `dsh-web-app` 在场
   （Web GUI 缺了壳就白屏）；
4. 所有 `@deepseek-ai/*` 依赖都能解析到闭包内实体；
5. 原生件在场：`koffi`、`node-pty`、`libreoffice-kit-win32-x64`；
6. 冒烟：`bin.js --version` 能跑（环境 `DSH_NO_TELEMETRY=1`）；
7. 扫产物里有没有 `({}).DSH_CLIENT_VERSION`——esbuild define 丢失时登录会在
   第一次点击抛错，这一步在装包前就拦住（README“闭包必须携带 client 构建
   环境”回归案）。

## 4. scripts/publish-updates.mjs（157 行）—— 更新 feed 发布

- 版本号从 `tauri.conf.json` 读（正则），与 setup 产物对齐。
- `--env test|production`（默认 test；`DSH_TAURI_UPDATE_ENV` 同名）：
  - test：必须给 `DOWNLOAD_TEST_ORIGIN`（https）+ `DOWNLOAD_TEST_RELEASE_ID`
    （32-hex），上传凭据 `DOWNLOAD_TEST_COS_BUCKET/SECRET_ID/SECRET_KEY`；
  - production：固定 origin，凭据 `DOWNLOAD_PROD_COS_*`。
- 在 `dist-update/` 组包：`DSH_<ver>_x64-setup.exe` + `.sig`（tauri build 产物）
  + `latest.json`（`platforms.windows-x86_64: {signature, url}`）。
- `--cos` 上传顺序有讲究：**先传 setup.exe（forbid-overwrite）**，再传 .sig，
  最后才发布 feed——顺序反了会出现“feed 指向不存在的包”。

## 5. src-tauri/installer-hooks.template.nsh —— NSIS 钩子

PREINSTALL：
1. 跑 `detect-harness.ps1 -Best` 找 harness；找到 → 用它。
2. 找不到：
   - **静默安装**：自动跑 `deploy-harness.ps1` 部署到默认
     `D:\Program files\deepseek-harness`；失败则把日志路径写
     `%TEMP%\dsh-install-error.txt`、`SetErrorLevel 7`、`Abort`。
   - **交互安装**：MessageBox YES=自动部署（可选目标文件夹，默认
     `D:\Program files` 下，没有 D 盘回退 LOCALAPPDATA）/ NO=选已有 harness
     （选到 `apps\cli` 这一层也接受，认一级上提）/ CANCEL=Abort。
3. `$INSTDIR = "<harness>\apps\desktop-tauri"` 后**重新 SetOutPath**
   （Tauri 模板在钩子前已经 SetOutPath 过一次，不重设会把文件装错地方——
   注释留案）。
4. 写注册表 `HKCU\Software\DeepSeek\DSH Desktop` 的 `HarnessRoot`。

POSTINSTALL：写 `InstallRoot`。
POSTUNINSTALL：删 `Run\DSH`（autostart 值）与 `InstallRoot`，**保留
`HarnessRoot`**（内核是独立资产，卸载壳不碰内核）。

## 6. src-tauri/installer/detect-harness.ps1（201 行）—— 发现既有 harness

参数：`-Json`（全部候选）、`-Best`（只输出最优）、`-MaxDepth=3`（盘扫深度）。
判定一个目录是 harness：`apps\cli\lib\bin.js` 存在，且
（`pnpm-workspace.yaml` 或 `packages\` 存在）。

五个来源按置信度打分（分数即优先级）：

| 分 | 来源 |
|---:|---|
| 100 | 注册表 `HarnessRoot`（本安装器记下的） |
| 95 | 正在运行的 DSH Host 进程所在的检出（命令行反推） |
| 80 | 会话桶路径推断：从 `%USERPROFILE%` 下 DSH 会话数据路径反推检出（`Expand-Bucket`：处理 `--wrap`、`~XXXX` 短路径展开、`-` 既可能是 `\` 也可能是连字符——对每段做**文件系统裁决式 BFS**，最多粘连 3 段） |
| 60 | PATH 上的 `dsh` shim，向上走 4 级找检出 |
| 40 | 磁盘扫描（仅当前四档都没有 ≥80 时）：根列表含 `D:\Program files` 等，跳过系统目录，4000 目录预算，不进 reparse point |

脚本刻意只用 ASCII 字面量（GBK/UTF-8 混乱的 PowerShell 环境里保平安——
与启动器仓库 一键封包.cmd 的 GBK 事故同源）。

## 7. src-tauri/installer/deploy-harness.ps1（220 行）—— 全新部署

参数：`-Target`（默认 `D:\Program files\deepseek-harness`）、`-Repo`（默认
官方）、`-Ref`、`-NodeVersion`、`-Check`（只体检）、`-SkipBuild`、`-Force`。
日志恒写 `%TEMP%\dsh-deploy.log`。要求 Node ≥ 24.9。

流程与失败语义（exit code）：

| code | 含义 |
|---:|---|
| 0 | 成功 / `-Check` 前置条件 OK |
| 2 | PATH 上没有 git（须先装 Git for Windows） |
| 3 | `git clone` 失败 |
| 4 | 目标存在且非空（须 `-Force`）/ `pnpm install` 失败 / `pnpm run build` 失败 |
| 5 | 部署走完但 `apps\cli\lib\bin.js` 不存在（检出或构建没产出 CLI） |
| 6 | Node 下载环节：index.json 无 v24 / SHASUMS256 缺行 / SHA-256 不符 / 解包后不可用 / pnpm 无法就绪 |

要点：
- 没有可用 Node 时从 nodejs.org `index.json` 选最新 v24，下载 zip 并用
  `SHASUMS256.txt` 校验（不校验的下载在 CI/代理环境不可信），解到
  `<target>\node`；`corepack enable pnpm` 出 pnpm。
- **故意完整 clone（不用 --depth 1）**：更新链要读 ahead/behind 计数
  （README 规定部署带历史；运行时 harness_update 才浅同步）。
- `pnpm install --frozen-lockfile` → 回退普通 install；`pnpm run build`。
- 用户 PATH 幂等追加 node 目录。

## 8. dist/index.html —— splash（119 行）

纯静态（无框架）：每 600ms `__TAURI__.core.invoke('server_status')`；渲染
状态 idle/spawning/ready/exited/error + 额度文案“（重启额度 X/3）” +
最近日志行 + 重启/退出按钮。页面**永不导航**（注释：导航会换 origin，
SameSite=Strict 的登录 cookie 场景反而更糟；进 GUI 由 Rust 侧开 main 窗）。
