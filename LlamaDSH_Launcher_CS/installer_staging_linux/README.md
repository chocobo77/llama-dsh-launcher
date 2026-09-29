# LlamaDSH Launcher — Linux 复刻版

原版 `../LlamaDSH_Launcher_CS` 是 .NET 8 WPF 的 Windows 启动器;本项目把它的全部核心功能
复刻到 **Python 3(标准库 + PyYAML)** 上,以**本地 Web UI** 作为主窗口,在 Linux
(含无桌面的服务器)上可用。配置/状态文件与原版 **JSON 字段级互通**(PascalCase),
参数 schema 直接复用原版的 `llama_params.yml`(236 参数)。

## 运行

```bash
cd LlamaDSH_Launcher_Linux
python3 main.py            # 默认端口 8788,被占用自动 +1
python3 main.py -p 9000    # 指定端口
```

启动后自动打开浏览器(无桌面时手动访问打印出的地址),主窗口即
`http://127.0.0.1:8788`。服务端进程常驻,关闭浏览器标签页不影响已启动的 Llama/DSH。

依赖:Python 3.10+,`pip install pyyaml`(仅此一个;`requests` 可选,缺省走 urllib)。
单实例锁:`~/.llamadsh_launcher_linux.lock`。

## 功能对照(原版 → 复刻版)

| 原版(WPF) | 复刻版(Web UI) |
|---|---|
| 主窗口 1400×950 GitHub Dark | 浏览器页面,同色板/同布局(左参数右监控) |
| 文件/目录选择对话框 | 服务端目录浏览弹窗(`📁 浏览...`) |
| 托盘最小化 | 不适用(服务端常驻,天然后台);窗口尺寸改存浏览器 |
| WMI 分离启动(断开远程不中断) | `setsid` 会话分离 + 启动日志尾随 |
| 提权 runas(读游戏内存) | `sudo -n`(默认关闭;失败不回退,同原版原则) |
| gputemp.exe 温度 | hwmon/sysfs(AMD/Intel/NVIDIA 均覆盖) |
| PerformanceCounter/WMI 监控 | `/proc/stat`、`/proc/meminfo`、nvidia-smi/amd-smi/rocm-smi/xpu-smi/sysfs |
| netstat/WMI 找端口进程 | `ss -ltnp`(+ lsof 兜底),仅认 node 进程 |
| KillProcessTree(叶子先死) | `/proc/*/task/*/children` 递归,SIGTERM→SIGKILL |
| PID 复用防护(ProcessTracker) | `/proc/<pid>/stat` starttime 比对 |
| 三层参数 schema | 同(内置纯净副本 → 程序旁 `llama_params.yml` → `~/.llamadsh/params_override.yml`) |
| safetensors→GGUF 转换 | 同(python3 + convert_hf_to_gguf.py,nice 低优先级,产物校验/断点删除) |
| 显存估算/熔断/qwen35 风险 | 同(GGUF 头解析、K/M 归一、MoE 折算系数完全一致) |
| llama.cpp 一键更新(Vulkan 优先+三镜像) | ubuntu-x64 资产,vulkan→avx2 优先,镜像回退一致 |
| DSH 升级检测/三步重建/兼容性修复 | 同(pnpm clean → build:lib → vite build;先备份后写) |

完整行为规格见原版仓库的 `DSH_启动操作记录_20260830.md`、`DSH_插件异常修复记录_20260924.md`、
`docs/修复记录-2026-09-24.md`;启动序列(确认链顺序:参数剔除 → 显存估算 → qwen35
部分卸载)、DSH 端口 3080 接管语义、2 次自动重试、30s 验证超时、`EXITCODE:` 轮询
等均按修复后的最终行为复刻。

## 文件布局

```
main.py        HTTP 服务 + 全部 API + 启动接管(takeover)+ 单实例
core.py        LogHub(噪声过滤/环形缓冲)、LlamaRunner、DshRunner、Monitor(熔断)、Converter
cmdline.py     BuildCmd/FmtCmd/QA、ResolveModelPath、GGUF 解析、显存估算、依赖引擎
schema.py      参数 schema 三层加载 + aliases 迁移
state.py       配置/方案/状态/重建基线持久化(与原版 JSON 互通)
procman.py     杀树/按端口找进程/孤儿 DSH 搜索/进程存活
monitor.py     CPU/内存/GPU 利用率/显存/温度(nvidia/amd/intel/sysfs)
compat.py      DSH 兼容性检查/修复 + 插件检测(精简版,先备份后写)
updates.py     llama.cpp/DSH 更新 + 自动检索(FileSearcher 精简版)
webui/         index.html + app.js + style.css(复刻 WPF 布局)
llama_params.yml         程序旁参数配置(可编辑;schema 更高时自动升级并备份 .bak)
webui/llama_params_default.yml  内置纯净副本(等价原版嵌入式资源)
```

## 数据文件(与原版同名同格式)

| 路径 | 内容 |
|---|---|
| `~/Documents/LlamaDSH_Launcher_config.json` | 全部方案 + 当前方案(可用"更改路径"重定向) |
| `~/.llamadsh_config_path.json` | 配置目录指针 |
| `~/.dsh_launcher_state.json` | LlamaPid/DshPid/DshDir(跨会话接管) |
| `~/.llamadsh_dsh_build.json` | DSH git HEAD 重建基线 |
| `~/.llamadsh/params_override.yml` | 参数差异覆盖层 |
| `~/.dsh_launcher_logs/{info,run,error,crash}.log` | 应用日志 |
| `/tmp/LlamaDSH_dsh_boot.log`、`/tmp/LlamaDSH_dsh_rebuild.{sh,log}` | DSH 启动/重建 |

## 与原版的有意差异

- **提权默认关闭**:原版默认提权是因为要读 Windows 游戏内存(GameAccessProbe);
  Linux 上无此需求,勾选"提权启动(sudo)"才会用 `sudo -n`,且失败不回退。
- **托盘与窗口尺寸**:浏览器标签替代;关闭标签不影响服务,重新打开地址即可回到主窗口。
- **compat/插件修复为精简版**:覆盖残留锁、--dump-config 探针、cordis.patch.yml
  静态检查(含 code-runtime→ptc-runtime 改名映射与配置键迁移)、失效 node_modules
  链接、dsh-market disabled 清空、manifest bundles;原版的 semver 全量 peer 判定、
  junction 重连等 Windows 特有面未复刻。
- **多标签页日志**:环形缓冲按 seq 增量拉取,多个浏览器标签互不抢日志(原版单窗口)。

## 测试

`python3 main.py` 后:核心逻辑(schema/命令拼装/依赖剔除/GGUF/显存估算)有单元验证;
进程生命周期(启动→接管→双启动保护→停止→状态清理)经 API 端到端验证;GUI 经浏览器
黑盒测试(布局/预览联动/依赖灰显/确认弹窗/配置保存),截图在 `gui-test-screenshots/`。
DSH 实启动需要目标机器装有 node24 + pnpm(本构建机无 node,仅代码路径复核)。
