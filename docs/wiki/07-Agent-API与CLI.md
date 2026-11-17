# 07 Agent API 与 CLI

启动器内置本机 HTTP 控制面，供脚本/agent 排障与自动化。**契约真源是 `docs/API-CONTRACT.md`**（入库）；本文是其结构化摘要，冲突时以契约文档为准。

## 传输层不变量

- **只绑 127.0.0.1**。Windows 默认端口 **17325**；Linux 默认 **8788**，被占自动 +1 顺延最多 20 个。
- **发现文件** `~/.dsh_launcher_logs/agent.json` = `{"port","token","enabled"}`，两个启动器写同一份（Windows 启动时还会读它，port 须在 1025–65535；Linux 只写不读）；是「最后一次启动」的记录，进程退出不清理——先 `GET /api/ping` 再当它在跑。Linux 另留 `~/.dsh_launcher_token` 纯文本（向后兼容）。
- **鉴权**：`X-Auth: <token>`（Windows 也接受 `Authorization: Bearer`）。免 token 仅 `GET /api/ping`（Linux 另放行 `GET /api/status` 供客户端自举）。Host 头白名单（127.0.0.1/localhost/::1）防 DNS rebinding 与跨站表单 CSRF，不符时 Windows 回 400 / Linux 回 403。
- **响应一律带 `ok`**：业务失败 = HTTP 200 + `ok:false` + `error`；请求体类型错误 = 400（布尔入参不吃「真值串」，两端入口都挡）；体超限 = 413，**绝不截断后解析**（Windows 262144 字符 / Linux 10MB）；未知端点 404；处理器异常 500。
- **停止端点三态**：`{"status": "stopped" | "stop_failed"}`（杀不掉如实回报，不谎报）。
- **启动端点三态**：`started | converting | start_failed`（具体原因只进 run.log）。
- 环境变量：`LLAMADSH_AGENT_PORT`（改端口）、`LLAMADSH_AGENT_TOKEN`（临时换 token，**不会**写进 agent.json）、`LLAMADSH_AGENT_DISABLE=1`（禁用，仅 Windows）、`LLAMADSH_AGENT_TIMEOUT`（cli 等待秒数 5–3600，默认 600）。
- 日志脱敏三形态（URL `?token=` / JSON `"token"` / `Bearer …`）→ `‹已隐藏›`；`/api/logs` 返回的内容同样脱敏，复制诊断不会带密钥。

## 端点清单

### 两端同名同义（19 个）

`ping`、`status`、`monitor`、`logs`、`preview`、`llama/start`、`llama/stop`、`dsh/start`、`dsh/stop`、`logs/clear`、`lightweight`、`job`、`update/check`、`update/do`、`dsh/compat`、`dsh/compat/repair`、`node/status`、`config/save`、`quit`

（除 `ping` 与 GET 类外均 POST；GET 可用：`ping/status/logs/job` 等查询类。）

### 仅 Windows（5 个）

`profiles`、`profiles/use`、`breaker`、`llama/raw`（直连 llama API 透传 slots/props/health）、`window`（hide/show）

### 仅 Linux（32 条路由 = 31 个独有端点 + `shutdown` 别名）

`bootstrap`（前端首屏）、`dsh/start_inner`、`dsh/rebuild`、`dsh/rebuild_and_start`、`dsh/plugins` 全家桶（`/audit` `/set_active` `/exempt` `/preview_update` `/upgrade` `/uninstall` `/install_local` `/open_dir`）、`search`、`config/load`、`config/setdir`、`schema/reload`、`open/logs`、`open/dsh`、`shutdown`（quit 别名）、`convert/start`、`browse`（服务端目录浏览）、`args_map`、`toolchains/variants`（+ `/switch` `/remove` `/install` `/parse_repo`）、`toolchains/check_update`、`toolchains/update`、`node/install`、`health`

### 同名但语义有差异（跨端脚本注意）

| 端点 | 差异 |
|---|---|
| `config/save` | **不可移植**：Windows 拒绝带 config 体（只改目录）；Linux 必须带完整 config |
| `preview` / `node/status` | 字段结构扁平 vs 嵌套，未统一 |
| `update/do` | Windows 恒回 `{"job":id}`；Linux 只有 llama 更新回 job，DSH 同步返回 |
| `status` 顶层字段集 | 两端不完全一致；跨端只认下表公共字段 |
| `quit` / `stop_services` | 已统一：默认 true，只有 JSON `true` 算 true（停止 llama/DSH 再退出） |
| `lightweight` | 已统一：`{on:bool}` 或 `{toggle:true}`，都不带 → ok:false |

## 字段别名口径

| 字段 | 口径 |
|---|---|
| `llama.running` / `llama.status` | 双给：bool 运行态 + 字符串状态（running/stopped/…） |
| 方案名 | 跨端只认 **`profile_name`**（Windows 的 `profile` 是方案对象，Linux 是名字字符串） |
| 后端三件套 | `backend`（生效）/ `backend_auto`（探测）/ `backend_choice`（方案显式选择）；值域 vulkan/rocm/cuda/sycl，对应方案字段 `LlamaBackend`（空=自动） |
| 时间戳 | `sampled_at`（epoch）+ `sampled_at_utc`（ISO-8601）双给 |
| monitor 键 | 统一 `gpu_temp/gpu_power/net_rx(+net_tx)`（旧键 temp/power/net 兼容）；历史序列容器统一 `hist`（Linux 旧名 `hists` 已加别名） |
| logs 游标 | 响应带 `boot` / `ring`(2000) / `seq_first_available` / `gap`；**seq 每次重启从 1 重来，boot 变了必须归零游标** |
| 参数过滤 | `logs` 支持 `n`（条数，≤2000，缺省 400）、`after=<seq>`、`level=error|warn|info`、`grep=文本`（先滤后截） |

## 任务模型（job）

分钟级操作（`update/do`、`toolchains/variants/install`、`node/install`）不能同步等：立即回 `{"job":"<id>"}`，轮询 `GET /api/job?id=` 取 `{status: running|done|error, log[-200:], result}`。任务保留最近 20 个。差异：`result` 类型 Windows=JSON 字符串 / Linux=对象（不可移植）。为什么必须走任务模型：Windows 侧 `AgentOp` 同步 marshal 到 UI 线程有 30s 上限。

## llamadsh-cli

两种形态等价：安装包自带的 `llamadsh-cli.exe`，或 `LlamaDSH_Launcher.exe cli <命令>`。stdout 永远是一行可直接解析的 JSON。

```
llamadsh-cli status            全量状态快照（llama/dsh/GPU/熔断/轻量档/方案/路径）
llamadsh-cli monitor          监测页同款数据（采样 + slots + 12 条速度历史）
llamadsh-cli logs 300         运行日志尾部（支持 level=error、grep=、after= 游标）
llamadsh-cli llama start|stop 启停 llama-server（带端口/模型预检，失败返回 JSON 原因）
llamadsh-cli dsh start|stop   启停 DSH
llamadsh-cli light toggle     托盘轻量档
llamadsh-cli window hide      隐藏到托盘 / show 恢复（仅 Windows）
llamadsh-cli profiles         方案列表；profile use <名> 切换（仅 Windows）
llamadsh-cli breaker set pct 90   显存熔断配置（set gb N / on / off）
llamadsh-cli raw slots        直连 llama API 透传（slots/props/health，仅 Windows）
llamadsh-cli api GET /api/status  任意端点逃生舱
llamadsh-cli quit             退出（默认先停服务；--no-stop 保留）
llamadsh-cli save             把界面当前状态写回配置文件（等价 POST config/save）
llamadsh-cli ping             探活
llamadsh-cli token / url      打印发现信息
```

> 注：没有 update / compat / node-status 专用子命令——这类操作走逃生舱，如 `llamadsh-cli api POST /api/update/check '{"target":"llama"}'`。

**退出码**：`0` 成功 / `1` 业务失败或 HTTP 4xx-5xx / `2` 接口不可达或已禁用 / `3` 超时（结果未知，**勿直接重试**，先用 `status`/`logs` 确认）。Linux 无内置 cli（脚本直接 curl）。

## curl 示例

```bash
# 读发现文件（Windows: %USERPROFILE%\.dsh_launcher_logs\agent.json；Linux 同路径）
TOKEN=$(python3 -c "import json;print(json.load(open(r'$HOME/.dsh_launcher_logs/agent.json'))['token'])")
PORT=$(python3 -c "import json;print(json.load(open(r'$HOME/.dsh_launcher_logs/agent.json'))['port'])")

curl -s http://127.0.0.1:$PORT/api/ping                      # 免 token 探活
curl -s -H "X-Auth: $TOKEN" http://127.0.0.1:$PORT/api/status # 全量状态
curl -s -H "Authorization: Bearer $TOKEN" "http://127.0.0.1:$PORT/api/logs?n=100&level=error"
curl -s -X POST -H "X-Auth: $TOKEN" -d '{}' http://127.0.0.1:$PORT/api/llama/stop
```

## 实现位置

| 端 | 服务端 | cli |
|---|---|---|
| Windows | `LlamaDSH_Launcher_CS/Services/AgentApi.cs`（HTTP 栈+鉴权）；路由注册在 `MainWindow.TryStartAgentApi`（9992 行处） | `Services/AgentCli.cs`，入口 `App.OnStartup` 第一行 |
| Linux | `LlamaDSH_Launcher_Linux/main.py`（`ROUTES` 表 + 49 个 `api_*` 处理器 + `Handler`） | 无 |
