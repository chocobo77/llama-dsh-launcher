# 04 · harness_update.rs —— 内核自更新与本地补丁门

1086 行。模块头注释（L1–29）就是设计说明书，这里按实现展开。

## 1. 设计总纲

**浅同步（shallow by design）**：部署的 harness 不需要历史，本地仓库只承载
“最新官方状态”（用户决策）。流程五步：

1. `ensure/check`：没有 `.git` 的检出**原地引导**（init + remote + fetch
   --depth 1 + reset --hard）；有 `.git` 则浅 fetch 后比对 HEAD SHA 与
   FETCH_HEAD SHA——不同即“有更新”（无历史仓库数不了 behind，也不需要数）。
2. `sync`：浅 fetch → **tracked 改动先 stash（保留，永不自动 pop）** →
   `reset --hard FETCH_HEAD`。untracked 的构建产物/node_modules/已装外壳
   绝不动；只有 reset 管辖的文件对齐上游。
3. `restore_local_patches`（阶段 2）：检出是**运行源**，只存在于本地的内核
   修复（台账 `LOCAL-CHANGES.txt`）活在它的源码里，第 2 步会抹掉它们
   （reset 抹 tracked 编辑、clean 抹 untracked 新文件）。所以重放用的**规范
   副本存在检出之外**——“能被更新抹掉的东西”不能是“负责恢复更新的东西”。
4. `rebuild`：`pnpm install` + 上游 artifacts 管线（回退 build:lib + build:web）。
5. `verify_local_changes --stage build`（阶段 4）：检出自带的断言必须对
   **Host 真正加载的构建产物**成立。2–5 任何非零退出 = 更新失败：壳绝不在
   “防线已被静默抹掉”的树上重启服务（案底：2026-11-16/17，
   LOCAL-CHANGES.txt——补丁被抹时各处无声无息，模型开始把工具调用写成
   纯文本，直到生产会话劣化才被发现，这就是本模块存在的理由）。

## 2. 同步目标与 tag 排序

- `TAG_PREFIX = "refs/tags/dsh-v"`；`sync_ref()`：`DSH_REPO_REF`（tag/分支/SHA）
  显式指定则直接用；否则 `git ls-remote --tags`（先直连，再
  `-c http.version=HTTP/1.1` 直连——GitHub over HTTP/2 在部分网络会挂死，
  HTTP/1.1 重试是实测必需），对 `dsh-v*` tag 逐个 `parse_tag_version` 取最大。
  跳过 `^{}` peeled 行。全失败回退 `"master"`。
- 为什么默认不用 master（模块注释）：master 是 worktree-sync 的垃圾场，tip
  经常不编译（39163dd3：office.ts 调用自家 skill-office 没有的 API），
  **官方 release tag 才是上游真正出货的东西**。
- `parse_tag_version`：`vMAJOR.MINOR.PATCH[-stage.N]` → 四元组可比较；第四槽
  排预发布：alpha(0) < beta(1) < rc(2) < final(3)，同阶段比序号
  （`stage*1_000_000 + num`）。测试 `tag_versions_sort_releases_above_rcs`。
- 回归 `resolved_ref_is_the_full_ls_remote_ref`：钉死曾出现过的
  `refs/tags/refs/tags/…` 双前缀 bug（L91–93 注释）。

## 3. 关键函数

| 函数 | 职责与要点 |
|---|---|
| `OFFICIAL_REPO` | `https://github.com/deepseek-ai/deepseek-harness.git`；引导时默认 remote，`DSH_REPO_URL` 可换（fork/中转） |
| `harness_root()` | 即 `server::default_checkout()`——外壳挂载进的那个检出（或显式指定的） |
| `git_exe()` | PATH 逐个目录试 `git.exe`，再试 Git for Windows 两处标准安装（cmd\、bin\）——装了但没进 PATH 也能用 |
| `run_git(args, timeout)` | 捕获 stdout+stderr；CREATE_NO_WINDOW；**轮询 try_wait 的超时等待**（网络 fetch 挂起也守时限）；超时会 kill+排空两条泵线程（防管道泄漏挂到进程退出） |
| `normalize_origin` | `git@github.com:`/`ssh://git@github.com/` → https；仅认 github.com；去查询串、尾斜杠、尾 `.git`（镜像拼接用） |
| `mirror_urls` | `DSH_GIT_MIRROR`（分号分隔；`{url}` 占位或前缀式）在前，然后三个公共代理 gh-proxy.com / ghfast.top / ghproxy.net |
| `Info` | `local/remote/via/needs_init/dirty`；`update_available()` = SHA 不同且非引导态（无历史下“不同”即有新版，不分方向）。测试 `update_gate_is_sha_equality` |
| 超时常量 | `SHORT=20s`（本地 git）、`FETCH=300s`（网络）、`RESET=120s` |
| `remote_url` | 仓库 origin 优先，其次 `DSH_REPO_URL`，最后官方 |
| `shallow_fetch` | 尝试序列：origin 直连 → origin HTTP/1.1 → 每个镜像（HTTP/1.1 + `--no-tags`）；成功后 `rev-parse FETCH_HEAD`，SHA 必须 40 位才算成；全败报“fetch 失败（直连与镜像都不通）”+尾部 800 字节 |
| `check(root)` | 无 `.git` → `needs_init`；有则查 dirty（`status --porcelain`）、HEAD、shallow_fetch。只读，不动工作树 |

## 4. sync()：stash 语义与失败回滚

- 超时：`SHORT=20s`（本地 git 操作）、`FETCH=300s`（网络 fetch）、
  `RESET=120s`（reset/clean）。
- 有 tracked 改动先 `stash push -m "dsh-sync-auto (tracked changes; not
  popped)"`；`rev-parse stash@{0}` 满 40 位才确认存进去了（push 在
  “No local changes to save” 时也返回 0）。日志明确告知 stash SHA、
  不会自动 pop、`git stash list` 可见历史积累。
- **stash 之后任何失败都 best-effort `stash pop` 回滚**（网络同步失败绝不
  把用户未提交改动搬家）；pop 失败则告知改动仍在 `stash@{0}` 请人工处理。
- `reuse_fetch=true`：直接信 `check()` 刚产出的 FETCH_HEAD（同一轮用户交互，
  间隔数秒，不再走网络）；没有有效 FETCH_HEAD 则报错。
- `reset --hard FETCH_HEAD` 后清理**上个版本残留的 untracked 源文件**（它们
  会污染 tag 的编译——案底：只在 master 存在的 office.ts 弄坏 release tag
  编译）。`clean -fd` 带豁免（见 §5），**先 `-fdn` 干跑把要删的列进日志**。
  clean 不带 `-x`，所以 .gitignore 的路径（node_modules、构建输出）不受影响。
- 收尾簿记：`update-ref refs/remotes/origin/master FETCH_HEAD` +
  `symbolic-ref HEAD refs/heads/master`，让后续 fetch/status 读数正常。

## 5. 更新时刻的豁免（CLEAN_EXCLUDES）

`clean -fd` 豁免清单（L449–456）：

| 模式 | 保的东西 |
|---|---|
| `apps/desktop-tauri` | 已安装的本外壳自己 |
| `LOCAL-CHANGES*` | 本地补丁台账 |
| `scripts/verify-local-changes.mjs`（`VERIFY_SCRIPT`） | **检出自带的断言脚本**——clean 若抹了它，更新就删掉了自己下一步要跑的裁判 |
| `.agents`、`.claude` | 本地代理配置 |
| `*.log` | 本地日志 |

测试 `clean_excludes_spare_the_patch_checker` 钉住前两项与参数拼装。

## 6. 重建（rebuild）

- `pnpm_for`：`<harness>\node\pnpm.cmd`（部署脚本随 Node 带的 corepack shim）
  → PATH 的 pnpm.cmd/pnpm.exe。
- `run_step/run_captured(program, args, cwd, extra_path, log, timeout)`：
  CREATE_NO_WINDOW；**PATH 前置 `<harness>\node`**（shim 解析这棵检出的 node，
  不是 PATH 上别的）；stderr 独立线程泵、stdout 主循环带 deadline（超时
  kill+“步骤超时”）；`run_captured` 把每行同时回传给调用方，让对话框能引用
  脚本自己的判词（“FAIL 锚点失配…”）而不是干巴巴的“步骤失败（exit 1）”。
- install：先 `--frozen-lockfile`（供应链护栏），失败退普通 `install
  --no-frozen-lockfile`（案底：上游多次出现 lockfile 落后 package.json，
  39163dd3 与 rc.3 均是）。
- 构建：首选 `pnpm exec tsx scripts/build.ts --scope artifacts`（native addon、
  Typert、host/client 编译与 bundle、web 全含）；失败回退 `build:lib` +
  `build:web`（早于该管线存在的两条产物配方）。
- **故意跳过聚合 type-check 门禁**（模块注释 L752–755）：release tag 曾带着
  自家发布管线都没跑过的 typecheck 欠账出货（dsh-v0.2.2-rc.3：测试对着未
  发布的 account API 写），拿它卡产物生产会把更新链困死。

## 7. 本地补丁门：阶段 0 / 2 / 4

模块注释 L821–827：为什么更新器要管补丁——**被抹掉的补丁不会在任何地方
打印**。本模块就是为了让“更新自己重放补丁、断言不过就拒绝回来”。

| 环境/函数 | 语义 |
|---|---|
| `PATCHES_DIR_ENV = DSH_LOCAL_PATCHES_DIR` | 显式指定规范副本目录（含 `restore-local-patches.mjs`）。**指错就大声失败**，不回退到别家的副本静默重放 |
| 候选顺序（`local_patches_candidates`） | 未显式指定时：`<checkout>\harness-patches` → `<parent>\harness-patches` → exe 向上 6 级祖先各拼 `harness-patches`（dev 布局） → 文档化固定家 `D:\code\DSH\harness-patches`。测试 `patch_candidates_probe_checkout_sibling_and_documented_home` |
| `SKIP_PATCHES_ENV = DSH_SKIP_LOCAL_PATCHES` | 非空 = 完全跳过补丁阶段（不带补丁的部署）；放弃门禁保持**刻意且留痕** |
| `has_patch_verifier` | 检出有没有 `scripts/verify-local-changes.mjs`——没有就**没有可报告的判词**，不能读成“补丁没了” |
| `patch_verdict` | 从脚本输出里只留 FAIL/MISSING/“missing —”/“未应用”行（≤8 行）做对话框判词；一行都没有则退回最后一行。测试 `patch_verdict_keeps_failures_and_drops_progress` |
| `verify_local_changes(root, stage)` | 跑检出的 `verify-local-changes.mjs`，stage=`source`（工作树）或 `build`（Host 真正加载的 lib 产物）；node 用 `node_for_checkout`；超时 180s。阶段 0 失败只记录（补丁在覆盖前就已缺，正是阶段 2 要修的），阶段 4 失败=更新失败 |
| `restore_local_patches`（阶段 2） | 跑 `restore-local-patches.mjs <root>`：重放补丁源 + 四处锚点接线（LOCAL-CHANGES.txt 台账）；超时 300s。**锚点失配按设计 exit 1**——官方新结构需要人来适配，被静默跳过的补丁正是这一阶段要抓的事故 |

## 8. bootstrap()

部署过但不是 git 仓库的 harness：`git init`（若无）→ 挂 remote（origin 缺时
`DSH_REPO_URL` 或官方）→ 转入 `sync`。终点状态与 sync 一致。

## 9. 测试（9 个）

见 08 总表；此模块测试全部纯逻辑（无网络无 git）：豁免清单、判词过滤、
候选目录、URL 规范化、镜像列表（含 `DSH_GIT_MIRROR` 两种写法）、SHA 相等门、
`tail`、tag 排序、ref 双前缀回归。
