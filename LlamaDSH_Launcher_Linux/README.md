# LlamaDSH Launcher — Linux 复刻版(2026-11-03: 图形客户端 + 发布包)

## 图形客户端与轻量化模式(实测数据)

`bash start.sh`(或应用菜单里的 "LlamaDSH Launcher")启动**托盘助手**(tray.py,
纯 python-dbus 微型常驻 ~30MB),左键/菜单"显示主窗口"按需拉起 **GTK 窗口进程**
(WebKit2 渲染与网页端**完全相同**的 UI,同 Strata"客户端=网页端"形态)。

三态内存(本机实测):

| 状态 | 内存 | 说明 |
|---|---|---|
| 驻留态(托盘+服务) | **~64 MB / 2 进程** | tray.py ~30MB + main.py ~34MB |
| 窗口打开 | ~600 MB / 5 进程 | GTK+WebKit 按需拉起 |
| 关窗(点 X) | **~64 MB / 2 进程** | 窗口进程整体退出(WebKit 子进程全灭) |

- **轻量化模式** = 收进托盘后的服务端形态:无服务存活 60s 纯休眠(不做任何
  子进程采样);有服务存活 30s 采样(熔断/接管安全不降级);恢复正常监控 3s
- 托盘 = SNI + dbusmenu 纯 Python 实现(GNOME ubuntu-appindicator 扩展):
  左键显示主窗口;右键 显示主窗口 / 轻量化模式(复选) / 退出托盘助手
- 客户端退出不影响已启动的 llama/DSH(分离会话);关窗自动进入轻量化
- 依赖:python3-gi + gir1.2-webkit2-4.1(窗口)+ python3-dbus(托盘);
  缺 GTK 降级为"服务 + 浏览器",缺 dbus 降级为纯服务

安装与打包:`bash install.sh`(装到 ~/.local/share 并注册应用菜单/托盘图标)、
`bash build_release.sh`(产出 dist/LlamaDSH_Launcher_Linux_v1.1.0.tar.gz)。

---
(以下为 2026-10-31 同步说明)

原版 `../LlamaDSH_Launcher_CS` 为 .NET 8 WPF 的 Windows 启动器;本项目将其复刻到
**Python 3 标准库 + PyYAML** 上,以本地 Web UI 为主窗口。本次同步对齐 Windows 版
2026-10-31 HEAD(58247ff)的全部功能性更新,并按 Linux 环境做了差异化适配。

## 运行

```bash
cd LlamaDSH_Launcher_Linux
python3 main.py            # 默认端口 8788,被占用自动 +1
```

浏览器打开打印的地址即主窗口;服务端进程常驻,关闭标签页不影响已启动的服务。
依赖:Python 3.10+、`pyyaml`。

## 本轮同步内容(相对 2026-09-29 功能基线)

### 监测子系统
- **LlamaApiMonitor**(`llapimon.py`, 2026-11-04 起独立实现):/slots 快轮询 + /props、
  /v1/models 慢轮询(30s 节流),每槽差分算速;llama.cpp 日志正则状态机
  (相位机/任务落账/MTP 接受率/graphs 复用);旧解析层(ParseTimings、/metrics 兜底、
  TTFT、请求表)按 f564fe7 删除。
- **端口解析与自动发现**(f919fd5+58247ff):配置端口勾选启用才可信 → 命令行
  `--port[= ]` → 自动发现缓存 → 盲扫 [8080, 8081, 1234](失败节流 10s,3 次失败判
  离线);端口只在配置侧解析一次写入线程安全缓存。
- **监测页全页重建**(WebUI):摘要条(4 格聚合)、核心指标卡(生成/预填充/上下文
  + Spark 迷你曲线、MTP 270° 环形 Gauge)、GPU 按卡面板(利用率环/显存条/温度/功耗/
  风扇/频率/PCIe/P-State/驱动)、实时生成任务(相位胶囊 + 三列 Stat + 进度条 + 明细行,
  对齐 LlamaStateCard)、事件流(200 条,单调序号增量)、系统资源 2×2(CPU 逐核柱/
  内存/磁盘/网络)、模型与 Slot 卡、11 张 60 点趋势图(真实时间轴,584f243)。
- 采样节奏:llama 运行中或监测页可见 3s,否则 30s。

### 显存预算与 OOM(10/25-31 系列)
- 张量表按层精确拆分(`ReadGgufTensorStats` 等价):专家/非专家逐层字节、
  `--n-cpu-moe`/`--cpu-moe`/`--moe-cache-mib` 任意组合;×0.95 校准。
- KV 只算全注意力层(`full_attention_interval`);FA off → V 量化回退 f16;
  SWA(窗口/--swa-full)与线性注意力(SSM state,随 `--parallel` 槽数增长)三分支;
  计算图 = ubatch×embed×173B;运行时基线 300MiB;草稿模型(spec-draft-ngl=0 不计);
  视觉编码器;o1 语义:输出 logits 缓冲不计入。
- **OOM 自动降层重试**(0314b6b+4bf9416):日志检测 OOM 关键词/offloaded N/M 层/
  buffer 汇总(输出 `[显存实测]` breakdown);崩溃后 `--gpu-layers`×0.85(下限 4)
  自动重启最多 3 次;干净退出才重置额度;API 在线补重置 + 降层值落盘。

### llama.cpp 变体/版本管理(`toolchain.py`,10/30 系列)
- **后端切换**(2026-10-31 增补):运行环境页「llama 后端」下拉——自动(按显卡)/
  Vulkan/ROCm/CUDA/SYCL;选择存方案 `LlamaBackend`,变体安装/检查更新/更新链全部
  遵循;ROCm 资产名带版本(`rocm-10.0-x64`),匹配 token 用 `rocm-`。
- 变体 = 独立目录,存于方案 `LlamaVariants`;激活切换 = 切 `LlamaDir`(运行中禁切);
  旧配置自动迁移"标准 llama.cpp"。
- 仓库链接解析(f3374cf 最终算法):主页/.git/tree/releases 深链/裸 owner/repo。
- 资产按显卡挑后端(52d1f36 Linux 化,已按 b11960 真实资产核对):
  NVIDIA=CUDA(版本最高)+ cudart 配对包(解压并入同目录),Intel=SYCL(fp16→fp32),
  AMD/默认=Vulkan → ROCm → CPU;平台 token `-bin-ubuntu-`、`.tar.gz`。
- 下载加固(d227f3c):`.part` + 原子改名 + size ±1% 对账 + 磁盘预检(3×, ≥512MB)
  + 三镜像回退;安装后 **build 硬校验**(缺失/不一致按失败,PrismML 混源事故防护);
  PrismML tag(`bNNNNN-hex`)解析与后端限制提示。

### Node 环境面板(`nodetool.py`,NodeSetup Linux 适配)
- 检测:PATH/~/.nvm/fnm/volta/snap/env;安装:npmmirror → nodejs.org 的
  node-v24-linux-x64.tar.xz → `~/.local/lib`,bin 软链 `~/.local/bin`;
  SHASUMS256 校验(取不到即换源,不做弱校验);pnpm 优先 corepack;
  81cdb3b:exe 在但 `node -v` 无输出 → 视为未知版本已安装,跳过重装。

### DSH 兼容/插件管理(compat.py 更新,2026-10-31 二次对齐)
- **插件管理器对齐 C# PluginManagerWindow**:列表列(名称/版本/状态标签/Spec 依赖
  声明/前 2 条 peer)、行内按钮及启用条件(激活=未激活;停用并锁定↔启用并解锁,带
  C# 同款确认文案;升级=Spec 非空且非 file:;豁免=不兼容且未豁免;卸载;打开目录;
  安装本地插件)、DSH 运行中互斥(升级/卸载/安装被拒——pnpm 会写 node_modules)、
  头部摘要同格式。
- **升级预检对齐**:engines.dsh + 全部 @deepseek-ai/dsh-* peer 对当前 DSH 版本的
  门禁判定(版本未知不判定);registry 最新版声明已删除依赖(concern)二次确认;
  npmmirror 回退;升级后读回实际版本 + 死 peer 依赖声明清理。
- **死依赖**(IsDshPackageDead 语义):只判 @deepseek-ai/*;全部 node_modules farm
  (含 ~/.dsh/profiles 共享 farm)未装且源码树无该 workspace 包 → 死;peer 指向死包
  → 从依赖声明自动移除(备份+原子写)。
- **锁定回写强制**(C# audit 1979):pinned_plugins.json(C# 同格式,按 profile 键控)
  中的包被网页端/市场写回 bundles 时,审计报错并自动移除(备份+保持锁定)。
- **optional peer** 不参与判定;**dsh-market disabled** 计入错误态。
- **入口注入检测/改写**(C# 3996-4950):--dump-config 行定位入口文件 → inject 数组
  服务名提取(括号配平)→ 死服务解析(logger 恒死;webRuntime→webStartup
  ≥0.2.1-alpha.1;invariants ≥0.2.0-rc.2)→ 修复只动 inject 数组内字符串;
  运行时 ctx.<服务> 访问用 JS 词法状态机(字符串/模板/注释不算命中,单遍扫描)判定
  后改名;全部先备份。
- 工程三件套(be42658/868253e):全部写回原子化(tmp+replace)、备份时间戳含毫秒、
  repair 入口先重审(陈旧快照防护)。
- SemverUtil(完整 caret/tilde/x/比较符/AND/||/prerelease)+ 结构化插件审计
  (peer 判定/激活态/锁定/豁免)。
- 插件管理器(WebUI 🗂):激活/停用并锁定(`pinned_plugins.json`)、版本豁免
  (`compatibility.json`,备份失败即中止)、升级预检 + registry 失败换 npmmirror、
  卸载、本地插件安装(pnpm pack);bundle 自动激活守卫(3af19b9)。

### 可靠性修复批次移植(适用项)
- 配置原子写 + 损坏留底 `.corrupt-*.bak`;启动前模型存在性 + 端口占用预检
  (不限进程名);接管 PID 进程名校验(防复用误杀);退出即清采集态(防假在线
  冻结数据);`--port[= ]` 正则;分片家族按数量选择(868253e);fmt_cmd 按 schema
  对位;git 超时后 `.git/index.lock` 自清理(81cdb3b);进程树击杀前 start_time 复检。

### WebUI 重构(dc57df0/5778404/48a9a2f)
- 左栏 0.73*:配置文件/方案/浏览器式三标签(运行环境/参数配置/命令预览)固定不滚;
  参数页 = 过滤行固定 + 左侧子组导航(计数徽章)+ 面包屑 + 参数区独立滚动;
  运行环境页含 Node 环境卡与变体下拉;逐参数真实启动参数回显(绿=已发射/灰=预览,
  48a9a2f);命令预览页含自定义参数追加(橙色 CmdCustomPreview)。
- 右栏:运行控制(122×30 统一按钮、OOM 重试开关、熔断行)+ 📊 监测(默认)/📜 运行日志。
- 细滚动条、通知条(info 8s/error 12s)、输入框超宽换行。
- 参数行两行式布局(输入行 + 独占整行的回显行,小缩进对齐勾选框);命令预览
  空配置时显示引导占位文本(不再空白)。
- 参数 UI 为状态表驱动(等价 CS _pbw/_pw):导航式渲染下未挂载子组的取值照常参与
  拼装/保存/依赖评估。

## 与 Windows 版的有意差异

| Windows | Linux |
|---|---|
| 托盘/最小化 | 服务端常驻天然替代;窗口尺寸不适用 |
| NVML DLL / LibreHardwareMonitor / gputemp | nvidia-smi 按卡查询 / sysfs+hwmon / lm-sensors 语义 |
| MSI 静默装 Node + UAC + Authenticode | tarball + SHASUMS256,免 root |
| `-bin-win-*.zip` + cudart zip 配对 | `-bin-ubuntu-*.tar.gz` + cudart tarball 并入 |
| exe 文件锁占用检测 | 进程/端口检测 |
| Authenticode 验签 | 不适用(略) |
| 编辑命令为就地编辑 | 模态编辑自定义参数(仅追加语义不变) |
| WMI 分离启动 | setsid 会话分离 + 日志尾随 |
| 提权 runas 读游戏内存 | sudo -n 选项(默认关) |

DSH 兼容服务中的入口注入改写(词法机)、死依赖全量扫描为精简实现;semver peer
判定/激活守卫/锁定/豁免/升级链为完整移植。

## 数据文件(与原版同名同格式,JSON 可互导)

- `~/Documents/LlamaDSH_Launcher_config.json` — 方案(ProfileConfig,含
  LlamaVariants/ActiveLlamaVariant/CustomArgs/VramMeasured*)
- `~/.llamadsh_config_path.json`、`~/.dsh_launcher_state.json`、`~/.llamadsh_dsh_build.json`
- `~/.llamadsh/params_override.yml`(override 层支持 subgroup/offValue/offFlag/wide)
- `~/.dsh/pinned_plugins.json`、`~/.dsh/profiles/web/{package.json,compatibility.json,…}`
- `~/.dsh_launcher_logs/{info,run,error,crash}.log`
- llama.cpp 变体建议目录:`~/llama.cpp-<owner>`(与现有目录并排);更新临时:
  `~/.cache/llamadsh/updates`

## 测试

引擎(schema/off 语义/分片家族/GGUF 扩展/张量表/SWA/线性注意力/FA 回退)、
llapimon(13 正则状态机/差分/OOM 检测)、toolchain(链接解析/资产挑选/tag 正则)、
semver 全部有单元测试;服务端经 API 端到端验证(bootstrap/preview/monitor/变体/
Node/插件审计/启动确认链/端口占用);WebUI 经浏览器黑盒验证(标签切换/导航/
过滤/回显绿灰/off 联动/自定义参数/通知条/变体与 Node 面板),布局经几何校验
(本轮 IAB 截图通道超时,以 DOM+几何证据替代,见 `gui-test-screenshots/` 历史截图)。
