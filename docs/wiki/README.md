# LlamaDSH 启动器 Code Wiki

本目录是仓库的代码 Wiki，从源码与既有文档整理而成，覆盖项目架构、模块职责、关键类与函数、依赖关系和构建运行方式。所有路径均相对仓库根。

> 版本基准：v1.1.3（`installer.iss` 的 `MyAppVersion` 为版本真源）。整理日期：2026-11-13。

## 文档目录

| 文档 | 内容 |
|---|---|
| [01-项目总览](01-项目总览.md) | 项目定位、双端形态、目录地图、文件入库分类、术语表 |
| [02-整体架构](02-整体架构.md) | 双端架构图、跨端公共设计（参数 schema / 方案 / 进程管理 / 监测熔断 / 日志 / Agent 接口）、关键常量 |
| [03-Windows端-WPF主程序](03-Windows端-WPF主程序.md) | `LlamaDSH_Launcher_CS` 全量拆解：csproj、启动流程、MainWindow、25 个 Services、Models、依赖图、已知坑 |
| [04-Linux端-Python-WebUI](04-Linux端-Python-WebUI.md) | `LlamaDSH_Launcher_Linux` 全量拆解：三进程模型、22 个模块、WebUI、selftest、依赖图、已知坑 |
| [05-外围工具与测试](05-外围工具与测试.md) | cli / gputemp / WmiProbe(退役) / SmokeTests / vendor / 根目录杂物 |
| [06-构建打包与发布](06-构建打包与发布.md) | 构建命令、一键封包.cmd 八步流程、Inno Setup 双安装模式、Linux 打包安装、版本同步规则 |
| [07-Agent-API与CLI](07-Agent-API与CLI.md) | 本机 HTTP 控制面契约、两端端点差异、字段别名口径、任务模型、llamadsh-cli 用法 |
| [08-配置与数据文件](08-配置与数据文件.md) | 配置 JSON、状态文件、日志目录、agent.json、llama_params.yml 参数表结构 |
| [09-DSH桌面壳-dsh-desktop-tauri](09-DSH桌面壳-dsh-desktop-tauri.md) | Tauri 桌面壳子项目：架构、双更新器、安装包挂载 harness、构建发布、与启动器的边界 |

## 一分钟了解本项目

**LlamaDSH 启动器**是一个把 llama.cpp（`llama-server`）与 DeepSeek Harness（DSH）集中管理的本地部署桌面工具：

- 按 llama.cpp 官方 help 维护的参数表（`llama_params.yml`）生成命令行，参数带依赖/互斥规则，按方案（profile）保存切换；
- 一键启动/停止 llama-server 与 DSH，支持接管已在运行的外部进程；
- 监测 CPU/内存/GPU/显存/TPS，显存熔断超阈值自动停 llama；
- 一键更新 llama.cpp（按显卡挑预编译后端）、Node 24/pnpm、DSH（git + 镜像）；
- DSH 兼容性检查修复与插件管理（启用/停用/升级/豁免/卸载/本地安装）；
- 内置本机 HTTP 控制面 + `llamadsh-cli`，供脚本与 agent 调用。

两端实现：**Windows 端** `LlamaDSH_Launcher_CS`（.NET 8 WPF，约 3 万行）为主程序；**Linux 端** `LlamaDSH_Launcher_Linux`（Python 3 标准库 + WebUI，约 1.7 万行）为功能对齐的复刻版。两端配置文件同名同格式可互导，Agent HTTP 接口契约统一维护在 `docs/API-CONTRACT.md`。

## 相关既有文档

- `README.md` — 用户视角的功能说明与常见问题
- `docs/API-CONTRACT.md` — Agent HTTP 接口契约真源（入库）
- `docs/安装模式与提权门控-2026-11-10.md` — Windows 安装模式设计文档（入库）
- `显存占用计算指南.md` — 显存预估公式手记（不入库）
- `docs/修复记录-*.md`、`docs/代码审查报告-2026-11-04.md`、`docs/AUDIT-2026-11-09-*.md` — 历轮修复/审查记录（不入库）
