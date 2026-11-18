# 09 DSH 桌面壳：dsh-desktop-tauri

> 2026-11-17 经 `git subtree add --prefix=dsh-desktop-tauri` 并入本仓库，上游独立 git 历史（9 个提交）完整保留在 master 图中；随后一个提交同步了上游工作树未提交的改动（v0.1.22 + 本地内核补丁阶段 0/2/4）。
> 原始位置 `D:\code\DSH\dsh-desktop-tauri` 保留为工作副本（构建缓存 target/ 与 node_modules 在那边），融合后以仓库内 `dsh-desktop-tauri/` 为真源。

## 定位

Tauri（Rust + WebView2）外壳承载 DSH Web GUI，由外壳负责 dsh host 进程的生命周期。它原是 deepseek-harness monorepo 的 `apps/desktop-tauri`（对照 Electron 壳 `apps/desktop`），拆成独立项目后又并入本仓库。约 4.4 千行 Rust + 5 百行 Node 脚本。

安装包**不带 DSH 运行时**（NSIS，约 2.8 MiB）：安装时先定位机器上已有的 harness，装进 `<harness>\apps\desktop-tauri`，运行时直接用该 harness 自己的 `apps\cli\lib\bin.js`。

## 关键机制（README.zh.md 的浓缩，细节以源码注释为准）

- **启动序列**：先显示 splash（打包内静态页）→ 启动 `dsh web --no-open --port 3090` → 监视 stdout 捕获 `dsh web: http://127.0.0.1:3090/?token=…` → 销毁 splash，以 `WebviewUrl::External(token URL)` 重建主窗。必须让主窗文档源是 host 本身：DSH 会话 cookie 是 `SameSite=Strict`，从 `tauri.localhost` 源跳过去会被拒收落在 401。
- **端口固定 3090**（`DSH_TAURI_PORT` 可改），被占直接报错不换随机端口——端口是主窗 origin 的一部分，`127.0.0.1` 的 cookie 不分端口，每换一个端口多一条会话 cookie，累积超过 Node 16KB 请求头上限后整个界面只会回 431。
- **监督线程**：服务意外退出/启动失败按 2s/5s/10s 退避重启，崩溃额度 3 次；只有稳定运行 ≥60s 的那一轮归还额度；额度耗尽后**暂停**（不退出），托盘「重启 DSH 服务」恢复并重置额度；用户主动退出不触发自动重启。
- **进程收尾**：先发 Ctrl+C 让 dsh 走有界优雅关闭，3 秒超时再强杀；每个 host 进程挂进 kill-on-close 作业对象，外壳被强杀/崩溃都不遗留 host 进程树（含 PATH 回退档的 `cmd.exe` 包装）。
- **托管语义**：`DSH_TAURI_TAKEOVER` 默认开启——启动自己前结束本机其它持有同一 `DSH_HOME` task-board 账本的 DSH host（先 Ctrl+C 再强杀；管理员终端里启动的 host 对资源管理器启动的外壳不可见，此时只记「接管受阻」不猜测；设 0 不动对方）。
- **桌面行为**：无终端窗口（`windows_subsystem="windows"` + 全部 spawn 带 `CREATE_NO_WINDOW`）；无原生菜单栏，功能全在托盘（显示 / 开机自启切换（写 `HKCU\...\Run\DSH`）/ 重启服务 / 检查更新 / 退出）；关窗=隐藏到托盘；单实例聚焦已有窗口；窗口位置尺寸持久化。
- **登录桥接**：主窗注入 `EXTERNAL_LINKS_JS`，页面 `window.open` 统一转系统浏览器（`open_external` 命令，仅 http(s) 且按 URL 解析校验）——原生 webview 默认拒绝 window.open，没有它登录流程直接死。
- **remote IPC 收口**：主窗文档源是 host，被 Tauri 判定 remote origin，默认调不到任何命令；`capabilities/host-page.json` 只给它一条 `allow-open-external` 且仅限环回；`capabilities/default.json` 给本地 splash 四条。扩大 Host 页可调用面是安全决策。
- **导航守卫**：`on_navigation` 把主窗锁在 host origin，其余跳转转系统浏览器。
- **文件拖放**：主窗 `disable_drag_drop_handler()`，否则 Tauri 截获拖放、Web GUI 附件区收不到 drop 事件。
- **日志管道**：`%TEMP%\dsh-tauri.log`（带 UTC 时间戳、`token=` 脱敏，splash 底部同显）。host 输出按**字节级 pump + lossy 解码**（`server.rs pump_lines`）——`BufReader::lines()` 遇非法 UTF-8（如 cp936 横幅）会终止循环，host 下次写日志得 EPIPE 静默 exit(1)，表现为周期性崩溃循环；有单测守住。

## 运行时回退链（第一命中生效）

1. `DSH_TAURI_CMD`（+ `DSH_TAURI_ARGS` JSON 数组 / `DSH_TAURI_CWD`）完全自定义；
2. 安装包捆绑的 `resources/cli/lib/bin.js`（可选自包含形态，配 `resources/node/node.exe`）；
3. `DSH_CHECKOUT`：含 `apps/cli/lib/bin.js` 的 checkout；
4. dev 默认：从 exe 位置向上推断 checkout；
5. PATH 上的 `dsh`（经 `cmd.exe /C` 包装；强杀外壳只杀 cmd 包装，node 子进程可能存活——发布形态建议 1–4）。

## 安装包与 harness 检索

`src-tauri/installer/detect-harness.ps1`（可独立运行，`-Best`/`-Json`）按置信度：注册表 `HKCU\Software\DeepSeek\DSH Desktop\HarnessRoot` → 正在运行的 DSH host 命令行 → `%USERPROFILE%\.dsh\sessions` 会话分桶（编码对连字符有歧义，逐段拿文件系统裁决）→ PATH → 常见安装根限深扫描。全未命中时问用户：从官方 GitHub 部署新 harness 或手动指定目录；取消即终止安装。部署器还会给无 Node 机器装 Node 24.9+（官方 zip + SHASUMS256.txt 校验，装到 `<harness>\node`，corepack 取 pnpm）。卸载只删 `<harness>\apps\desktop-tauri`，HarnessRoot 注册表保留。

## 双更新器（共享全机 in-flight 锁；`--check-update`/`--check-harness-update` 二次启动可触发）

- **「检查 harness 更新…」**：同步 harness 到最新官方发布 tag（`ls-remote` 按版本排序选 `dsh-v*`，`DSH_REPO_REF`/`DSH_REPO_URL` 可覆盖；master 只作兜底）。裸文件部署的 harness 首次原地 `git init` + 浅 fetch 对齐；本地改动 reset 前入 stash；换 ref 的未跟踪文件先 dry-run 列清单再清理（豁免：已安装外壳、`LOCAL-CHANGES*`、`.agents`、`.claude`、`*.log`）；git 直连失败先 HTTP 重试再走 gh-proxy 镜像（`DSH_GIT_MIRROR`）。同步后走上游 artifacts 管线重建（`pnpm install` frozen 失败回退 + `build.ts --scope artifacts`）并重启外壳；同步失败用原产物自动恢复，重建失败保持停止由用户决断。
- **本地内核补丁阶段 0/2/4**（与本仓库启动器 84783b6 同款）：0 记基线、2 在 reset/clean 后重放补丁（补丁正本存 checkout 外，防止被它要恢复的那次更新清掉）、4 断言重建产物仍带补丁；失败即中止——0 只记日志，2 回滚旧产物，4 宁可停服不启动防御被清掉的构建。`clean_args` 豁免 checkout 自带的断言文件，`run_captured` 让清理步骤的逐行结论可回传。
- **「检查安装包更新…」**：Tauri updater feed（`latest.json` + minisign 签名；端点烧进包，`DSH_TAURI_UPDATE_ENDPOINT` 运行时可覆盖，release 只收 https）。版本号唯一来源是 `tauri.conf.json` 的 `version`。已知行为：NSIS 静默更新终结运行中的应用后**不会自动重启**（上游行为），更新完需手动重开。

## 代码地图

| 文件 | 行数 | 内容 |
|---|---|---|
| `src-tauri/src/server.rs` | 2127 | 端口挑选（固定档被占即报错）、启动计划梯（回退链逐档）、字节级 pump_lines、进程清理/接管扫描、token URL 捕获 |
| `src-tauri/src/lib.rs` | 1197 | 托盘/窗口/单实例/window-state、监督线程与崩溃额度、IPC 命令路由、EXTERNAL_LINKS_JS、更新入口编排 |
| `src-tauri/src/harness_update.rs` | 1086 | harness tag 同步、stash/清理/镜像、内核补丁阶段 0/2/4、断言门禁 |
| `src-tauri/src/main.rs` | 11 | GUI 子系统入口 |
| `scripts/prepare-installer-hooks.mjs` | 48 | 从模板生成 `installer-hooks.nsh`（NSIS 钩子：卸载删 Run 值与目录） |
| `scripts/prepare-runtime-cli.mjs` | 172 | 可选自包含闭包（hoisted deploy + 链接实体化 + 剔除 app 自链接——曾把 9GB target 打进包） |
| `scripts/publish-updates.mjs` | 157 | 组装 `dist-update/`（latest.json + 产物），可推 COS |
| `scripts/verify-runtime-cli.mjs` | 125 | 闭包完整性 + `DSH_CLIENT_VERSION` define 缺失检测（缺失 = 登录第一次点击即抛错） |

**测试**：`cd src-tauri && cargo test`，29 项全过（端口挑选不漂移、启动计划梯顺序、pump_lines 非法 UTF-8 存活、崩溃额度短跑消耗/稳定归还、导航守卫只放行 host origin、进程清扫带启动时间等）。融合后已在仓库内验证通过。

## 构建与发布

```sh
pnpm install                     # @tauri-apps/cli
pnpm run build                   # = prepare-installer-hooks + tauri build（NSIS）
# 产物: src-tauri/target/release/bundle/nsis/ 下 setup.exe(+.sig)
# 需要签名环境: TAURI_SIGNING_PRIVATE_KEY（keys/updater.key 内容）+ 空密码
node scripts/publish-updates.mjs [--cos]   # 组装/上传 feed
cargo test                       # 单测
```

前置：Rust MSVC 工具链 + WebView2、Node + pnpm。`keys/`（更新签名私钥）被 gitignore，**必须离线备份**；`runtime-cli/`、`runtime-node/`、`src-tauri/target/`、`dist-update/` 均不入库。

仓库级入口：`一键封包.cmd -tauri` 走 [9/8] 可选步骤（自动 pnpm install + tauri build，私钥从 keys/ 经 PowerShell 环境注入，见 [06](06-构建打包与发布.md)）。

## 启动配置环境变量（优先级自上而下）

| 变量 | 含义 |
|---|---|
| `DSH_TAURI_CMD` / `DSH_TAURI_ARGS` / `DSH_TAURI_CWD` | 完全自定义启动命令（ARGS 为 JSON 数组） |
| `DSH_TAURI_NODE` | node 路径（优先于捆绑 node 与 PATH） |
| `DSH_CHECKOUT` | 含 `apps/cli/lib/bin.js` 的 checkout 根 |
| `DSH_TAURI_PORT` | host 固定端口（默认 3090，被占报错） |
| `DSH_TAURI_PROFILE` | host profile（默认 web；`desktop` 被 Electron 壳保留） |
| `DSH_TAURI_HOME` | 子进程 `DSH_HOME`（与终端会话并存时各自独立） |
| `DSH_TAURI_CWD` | host 工作目录（会话按工作区分桶） |
| `DSH_TAURI_TAKEOVER` | 0 = 不结束其它 DSH host |
| `DSH_TAURI_UPDATE_ENDPOINT` / `DSH_TAURI_UPDATE_CHECK=1` | 更新端点覆盖（release 仅 https）/ 启动 3s 后无界面更新钩子 |
| `DSH_REPO_REF` / `DSH_REPO_URL` / `DSH_GIT_MIRROR` | harness 更新的 ref / 上游 / 镜像覆盖 |

## 与启动器（本仓库主程序）的边界

- **端口分工**：启动器管理的 DSH Web 固定 **3080**（`DshIdentity.DshPort`）；桌面壳固定 **3090**。两者互不感知、互不接管——桌面壳按自己的 TAKEOVER 语义清点的是「同 `DSH_HOME` 的其它 host」，启动器的接管判定（进程名→命令行→HTTP 指纹）同样不针对 3090。
- **共用的事故经验**：token 脱敏（启动器 `AppLog.Redact` / 桌面壳日志管道脱敏）、ledger-v2.lock 单持有者（启动器残留锁三条件判定 / 桌面壳接管扫描）、提权进程命令行读不到的兜底（启动器 HTTP 指纹 / 桌面壳「接管受阻」记录）、git 更新的 stash 保护与镜像链（启动器 UpdateService / 桌面壳 harness_update）。
- **未做的功能融合**（有意保留边界）：启动器不管理桌面壳进程，桌面壳不调用启动器 agent API；两者更新链独立（启动器 llama/DSH git 更新 vs 桌面壳 harness tag 同步 + 安装包 feed）。

## 融合与上游同步口径

- 仓库内 `dsh-desktop-tauri/` 为真源；上游 9 个提交（`2a680d1` 初始提交 → `57fa07a` 更新流加固）完整保留在 master 历史图中。注意路径过滤的 `git log -- dsh-desktop-tauri` 只显示 merge 与 overlay 两个提交（上游提交改的是其根级路径）；查上游链用 `git log 2a680d1..57fa07a`。
- 若原位置还要继续开发：在原仓库提交后用 `git subtree pull --prefix=dsh-desktop-tauri D:\code\DSH\dsh-desktop-tauri main` 同步进来；或直接在本仓库的子目录里开发（推荐），原位置退役。
- `dsh-desktop-tauri/.gitignore`/`.gitattributes` 随 subtree 保留，嵌套规则生效（`keys/`、`target/`、`node_modules/`、`runtime-*`、`dist-update/`、生成的 `installer-hooks.nsh` 均被正确排除，已用 `git check-ignore` 验证私钥路径）。
