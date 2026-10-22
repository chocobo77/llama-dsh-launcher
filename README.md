# LlamaDSH 启动器

Windows 上跑 llama.cpp 和 DeepSeek Harness 的桌面程序。把平时要敲的命令行、要记的参数、要盯的进程，收进一个窗口里。

![主界面](docs/images/main-window.png)

## 为什么写它

llama-server 的启动参数有两百多个，改一个值就要重敲一遍命令。DSH 依赖 node 24 和 pnpm，换台机器就得重装。插件目录搬过家之后 pnpm 的软链会悬空，报错只有一句 ERR_MODULE_NOT_FOUND。这些坑每次部署都要踩一遍，所以单独写个程序把它们收起来。

## 能做什么

- 参数面板：按 llama-server 官方 help 分节，勾选后自动生成命令，右侧实时预览
- 启停与日志：llama 和 DSH 各自一键启停，输出实时贴进日志区
- Node 环境：检测 node/pnpm 版本，一键安装或更新到 v24，装完顺手检查 DSH 的 node_modules 软链
- DSH 维护：兼容性检查、插件管理、失效链接重连、全量重建
- 显存熔断：超过设定阈值自动停掉 llama
- 托盘：关窗口不退出，双击托盘图标恢复

## 需要什么

- Windows 10 / 11 x64
- llama.cpp 目录（里面有 llama-server.exe）
- DeepSeek Harness 源码目录（含 apps/cli）
- node 24 与 pnpm。没有也不要紧，程序能装。

用发布版不需要装 .NET；从源码运行需要 .NET 8 桌面运行时。

## 怎么用

1. 打开程序，在「运行环境」页填模型文件、llama.cpp 目录、DSH 目录
2. 点「刷新状态」看 node 和 pnpm 是否就绪，缺哪个点「一键安装/更新」
3. 到「参数配置」页按需勾选，在「命令预览」页核对生成的命令
4. 点「启动 Llama」，等日志里出现 listening，再启动 DSH
5. 浏览器打开 http://127.0.0.1:3080

配置存在「文档」目录下的 json 文件里，可以有多套方案，切换方案就是换一套参数。

## 目录

- LlamaDSH_Launcher_CS：主程序，WPF
  - Services：进程执行、工具链探测、兼容修复、更新、GPU 采样
  - Resources/llama_params.yml：参数表，llama.cpp 升级后改这里
- LlamaDSH_Launcher_Linux：Linux 侧的 webui 版本，实验性质，能用但没细磨
- tests/SmokeTests：不依赖任何 NuGet 包的冒烟测试
- docs：历次修复记录
- WmiProbe：读进程命令行的小工具

## 常见问题

**启动 DSH 报 Cannot find package '@deepseek-ai/...'**

DSH 目录搬过家，pnpm 的软链还指着旧路径。点「兼容性修复」重连链接；新版里直接点启动也行，程序会先自愈再重试。还不行就在 DSH 目录跑一次 pnpm install。

**提示找不到 node 24**

只认 v24 及以上。用「一键安装/更新」装，或者把 node 目录加进 PATH 后点「自动检索」。

**改了参数但生成的命令没变**

点「重载参数配置」。参数表是从 Resources/llama_params.yml 读的，改完不用重启程序。

**托盘图标不见了**

在托盘设置里把它设成常显。程序退出会清掉图标，重启后恢复。

## 构建

    dotnet build LlamaDSH_Launcher_CS/LlamaDSH_Launcher.csproj

打安装包运行 一键封包.cmd，内部是 dotnet publish 单文件加 Inno Setup。

跑测试：

    dotnet run --project tests/SmokeTests/SmokeTests.csproj

## 参数表

当前对齐 llama-server 0.7.0-dev build 11680。升级 llama.cpp 之后建议把新版 --help 存成文件，和 llama_params.yml 逐条对一遍，方法写在文件开头。

## 许可证

MIT，见 LICENSE。改完想怎么用都行，留着版权声明就好。
