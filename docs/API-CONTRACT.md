# 双端 Agent HTTP 接口契约（Windows ↔ Linux）

本文钉死两个启动器自带的本机 HTTP 控制面的**传输层、发现文件、端点名与字段口径**，
使跨端脚本(cli / curl / agent)只换 base URL 就能跑。代码里的出处：

- Windows：`LlamaDSH_Launcher_CS/Services/AgentApi.cs`(传输层/发现文件)、`MainWindow.xaml.cs`
  (端点注册与处理器，`Agent*` 系列方法)、`Services/AgentCli.cs`(内置 cli)。
- Linux：`LlamaDSH_Launcher_Linux/main.py`(传输层 + `ROUTES` 全部处理器)。

约定：**改任何一端的接口形状，必须同步改本文**；本文与代码不一致时以本文描述为准去修代码。

## 1. 传输层（不变量）

| 项 | Windows | Linux |
|---|---|---|
| 绑定地址 | `127.0.0.1`(仅环回) | `127.0.0.1`(仅环回) |
| 默认端口 | `17325`(`AgentApi.DefaultPort`) | `8788`(`DEFAULT_PORT`) |
| 端口被占 | 用 `agent.json` 的 `port` 或 `LLAMADSH_AGENT_PORT` 改 | 自动 `+1` 顺延，最多试 20 个 |
| 发现文件 | `~/.dsh_launcher_logs/agent.json` | 同路径(Windows 是 `%USERPROFILE%\.dsh_launcher_logs\agent.json`) |
| 鉴权头 | `X-Auth: <token>`，也接受 `Authorization: Bearer <token>` | 只认 `X-Auth: <token>` |
| Host 白名单 | 有(`HostIsLoopback`)：`localhost` / `::1` / `127.*`，可带端口；不符 → **400** | 有(`_host_ok`)：`127.0.0.1` / `localhost` / `::1`(含 `[::1]:port`)；不符 → **403** |
| 请求体上限 | **262144 字符**(也先按 `Content-Length` 判)；超出 → **413**，**绝不截断后继续解析** | **10 MB**(按 `Content-Length`)；超出 → **413** |
| 免鉴权端点 | 只有 `GET /api/ping` | `GET /api/ping`；另有 `GET /api/status`(客户端自举探测) |
| 已知字段类型校验 | 有：请求体不是合法 JSON 对象、或 `on`/`toggle` 等布尔字段带了非布尔值 → 处理器回 `AgentBadRequestException`，出口统一 **400 + `ok:false`** | 有(`_body_shape_error`，仅 POST)：`profile`/`config` 须为对象、`name`/`dir`/`target`/`url`/`repo`/`path`/`source`/`version`/`outtype` 须为字符串、`on`/`visible`/`active`/`toggle` 须为布尔；字段存在且非 `null` 而类型不符 → **400** |

两端共同保证的响应约定：

- **每个 JSON 响应都带 `ok`**，包括 `4xx` / `5xx` 与业务失败。
- **业务失败 = HTTP 200 + `ok:false` + `error:"…"`**；处理器抛异常 = **HTTP 500 + `ok:false`**。
  调用方判断成败只看 `ok`，不要用 HTTP 状态码代替(200 也可能是失败)。
- `error` 字段只增不减名；Linux 侧由 `_with_ok()` 在出口统一补 `ok`(按 `error` 的**值**推断，
  `{"error": null}` 视为成功)，因此老的 `if (data.error)` 写法仍然成立。
- **布尔入参不吃"真值串"**：`on` / `visible` / `active` / `toggle` 一旦携带就必须是真布尔，
  不接受 `"false"`、`0`、`[]` 这类会被 `bool()` 误判成 `True` 的值。**两端都在入口挡成 400**
  (Linux `_body_shape_error`；Windows `AgentBadRequestException` → `AgentApi` 出口)。
  以前 Windows 把类型错误也回成 `200 + ok:false`，跨端按状态码分支的脚本会走错路(已统一)。
  字段**缺省**时各自语义见对应端点。

### 1.1 停止端点的三态回报（`llama/stop` / `dsh/stop`）

停止是"发起了停止"，不等于"进程真的没了"。两端 `ok:true` 只表示停止流程已执行，
**真实下场看 `status`**：

- `status:"stopped"` —— 进程已确认退出（或本来就未在运行，`was_running:false`）。
- `status:"stop_failed"` —— 停止命令已发出，但进程在时限内没退（Linux `stop()` 返回停止失败；
  Windows 由 UI 收尾确认，超时/仍在即此值）。

"查不到 ≠ 不是"：Linux `stop()` 无 pid 时也回 `stopped`（本来就停着），不会把"从没运行过"
误报成失败。调用方要判定"真的停了没"，读 `status`，别只看 `ok`。

### 1.2 启动端点的三态回报（`llama/start` / `dsh/start`）

与 §1.1 对称：**"下发了启动"不等于"拉起了进程"**。以前 Windows 一律回
`ok:true + msg:"启动指令已下发"`，UAC 被用户取消、DSH 目录没配、命令构建失败这几种情况
在 agent 侧完全看不出来(而"用户级安装 + 以管理员启动"这条路径下 UAC 是真的会弹的)。

- `ok` / `started`：`true` 只代表**进程已拉起或转换已排上**；是否在跑仍要用 `status` / `monitor` 判。
- `status`：Windows 给 `started` / `converting` / `start_failed`；Linux 给 `started` + `converting`(bool)。
- `start_failed` 时 Windows 另给 `error:"start_failed"`；**具体原因只进 run.log**，用 `cli logs`
  或 `GET /api/logs` 查(端口被占、模型路径不存在、UAC 被拒、参数检查未过…)。
- Windows 的 `llama/start` 在 safetensors 分片场景会**弹转换确认框**(agent 场景请用 GGUF 路径)，
  这也是 `status:"converting"` 存在的原因：转换排上了，真正的启动要等转换完成。

## 2. 发现文件与环境变量

`~/.dsh_launcher_logs/agent.json` —— **两端都写**，字段名、大小写、语义完全一致：

```json
{"port": 17325, "token": "…", "enabled": true}
```

- 权限：Windows 把 DACL 收成 `SYSTEM + Administrators + 当前用户` 并置 Hidden；Linux 以 `0600` 落盘
  (每次重写后补 `chmod 0600`，因为文件已存在时 `O_CREAT` 的 mode 不起作用)。
- `enabled`：Windows 语义是"接口开关"——读到 `false` 就**根本不监听**(`failReason` 写进日志)；
  Linux 的这个 HTTP 服务就是主窗口本身，没有"监听但不服务"的形态，所以恒写 `true`。
  调用方在两端都可以无条件读这个键。
- 这份文件是**最后一次启动**的记录，进程退出不会删除也不会置 `false`。所以先用
  `GET /api/ping` 探活，别把"文件存在"当成"服务在跑"。
- Windows 启动时会**读**这份文件(port 必须在 1025–65535)；Linux 只写不读。
- Linux 额外保留 `~/.dsh_launcher_token`(`0600` 纯文本 token)作向后兼容，
  `client.py` / `tray.py` 仍从它取 token；新代码应优先读 `agent.json`(里面连实际监听端口都有，
  端口顺延后只看默认 8788 会连错)。

Windows 环境变量(仅 Windows)：

| 变量 | 语义 |
|---|---|
| `LLAMADSH_AGENT_PORT` | 覆盖端口(1025–65535)。优先级高于 `agent.json` |
| `LLAMADSH_AGENT_TOKEN` | 覆盖鉴权 token。**不会被写进 `agent.json`**——落盘的是磁盘上的旧 token(没有则空串)，`cli` 也按同一优先级读 env，所以"文件里的 token 用不了"是预期行为 |
| `LLAMADSH_AGENT_DISABLE=1` | 禁用接口(不监听)。与 `agent.json` 里 `enabled:false` 同效 |
| `LLAMADSH_AGENT_TIMEOUT` | 内置 cli 的等待秒数，clamp 到 5–3600，默认 600 |

Linux 没有对应的开关：端口来自 `-p/--port`，接口不可禁用。

## 3. 端点表

方法栏只写实际受理的方法。路径都是 `/api/<名>`；Linux 侧 `POST` 吃 JSON 对象，
`GET` 吃 query(`?k=v`，两端都按字符串取值再由处理器自己转类型)。

### 3.1 两端同名同义

| 端点 | Windows | Linux | 入参 | 返回关键字段 |
|---|---|---|---|---|
| `ping` | GET | GET | — | `ok`、`app`、`cli`(免 token；Windows 的 `cli` 是提示串，Linux 恒为空串) |
| `status` | GET | GET / POST | — | `ok`；`llama{running,status,pid}`、`dsh{running,status,pid}`、`profile_name`(见 §4) |
| `monitor` | GET | POST | Linux: `{visible?:bool}`(打点 3s 快采) | `ok`、`sampled_at`、`sampled_at_utc`、历史序列(见 §4) |
| `logs` | GET / POST | POST / GET | `n`(尾部条数) 与/或 `after`(游标 seq)、`level`(`error`/`warn`/`info`)、`grep`(子串)，见 §4 | `ok`、`lines`、`after`/`seq_last`、`count`、`boot`、`ring`、`seq_first_available`、`gap`. |
| `preview` | GET / POST | POST | Windows 忽略入参；Linux `{profile}` | 见 §3.4(两端字段不同) |
| `llama/start` | POST | POST | `{profile, confirm?}` | `started`/`error`，Linux 另可回 `need_confirm`、`converting` |
| `llama/stop` | POST | POST | — | `ok`、`was_running`(Windows)、`status`(`stopped`/`stop_failed`，见 §1.1) |
| `dsh/start` | POST | POST | `{profile}` | `started`/`error`，Linux 另可回 `need_rebuild` |
| `dsh/stop` | POST | POST | — | `ok`、`was_running`(Windows)、`status`(`stopped`/`stop_failed`，见 §1.1) |
| `logs/clear` | POST | POST | — | `ok` |
| `lightweight` | POST | POST | `{on:bool}` 或 `{toggle:true}`(见 §3.4) | `ok`/`lightweight`；两者都不带 → `ok:false` |
| `job` | GET / POST | POST / GET | `{id}`(GET 用 `?id=`) | `ok`、`id`、`name`、`status`、`log`、`result`(见 §5) |
| `update/check` | POST | POST | `{target:"llama"\|"dsh", dir?}` | `local`、`latest`、`up_to_date`，失败带 `error`+`cause:"upstream"` |
| `update/do` | POST | POST | `{target, dir?}` | Windows: `{job:id}`；Linux: llama 走 `{job:id}`、dsh 同步回 `{ok,msg}` |
| `dsh/compat` | POST | POST | `{profile}` | `report`、`log` |
| `dsh/compat/repair` | POST | POST | `{profile}` | `report`、`log`、`fixed` |
| `node/status` | GET / POST | POST | — | `ok` + node/pnpm 探测结果(字段名两端不同，见 §3.4) |
| `config/save` | POST | POST | Windows **不接受** `config`；Linux 必须带 `{config:{…}}` | `ok`(+ Windows `msg` / Linux `path`)。见 §3.4 |
| `quit` | POST | POST | `{stop_services?:bool}`(默认 true) | `ok`、`msg`；Linux 另带 `stopping`、`stop_services` |

### 3.2 仅 Windows

| 端点 | 方法 | 入参 | 返回关键字段 |
|---|---|---|---|
| `profiles` | GET | — | `ok`、`current`、`names`、`configs` |
| `profiles/use` | POST | `{name}` | `ok` / `error` |
| `breaker` | GET / POST | GET —；POST `{enabled, type:"pct"\|"gb", value}` | `enabled`、`type`、`value`、`triggered` |
| `llama/raw` | GET | `?path=slots\|props\|health` | llama API 透传 |
| `window` | POST | `{action:"show"\|"hide"\|"minimize"\|"restore"}` | `ok` / `error` |

(Windows 侧没有 Linux 的工具链/插件/配置目录等端点：那些动作在 Windows 上由界面完成。)

### 3.3 仅 Linux

| 端点 | 方法 | 入参 | 返回关键字段 |
|---|---|---|---|
| `bootstrap` | POST | — | `schema{groups,params,errors,warnings,source}`、`config`、`current_profile`、`config_dir`、`config_path`、`log_dir`、`override_path`、`llama_status`、`dsh_status`、`llama_pid`、`dsh_pid` |
| `dsh/start_inner` | POST | `{profile}` | `started`、`error` |
| `dsh/rebuild` / `dsh/rebuild_and_start` | POST | `{profile}` | `ok`、`error`(前者只重建) |
| `dsh/plugins` | POST | `{profile}` | `report{items[{name,note,level}],count}`、`counts{abnormal,market,links}`、`log` |
| `dsh/plugins/audit` | POST | `{profile}` | `plugins[]`、`writebacks`、`summary`、`dsh_running` |
| `dsh/plugins/set_active` | POST | `{name, active}` | `ok`、`msg` |
| `dsh/plugins/exempt` | POST | `{name, version}` | `ok`、`msg` |
| `dsh/plugins/preview_update` | POST | `{name, profile}` | `installed`、`latest`、`up_to_date`、`gate_blocked`、`concern` |
| `dsh/plugins/upgrade` / `uninstall` | POST | `{name}` | `ok`、`msg`;DSH 运行中 → `ok:false + error` |
| `dsh/plugins/install_local` | POST | `{source}` | `ok`、`msg`;DSH 运行中 → `ok:false + error`(DSH 目录取当前方案) |
| `dsh/plugins/open_dir` | POST | `{dir}` | `ok` / `error`("目录不存在: …") |
| `search` | POST | `{target}` | `result`(找到的路径) |
| `config/load` | POST | — | `config`、`error`(null=成功)、`current_profile` |
| `config/setdir` | POST | `{dir}` | `ok`、`dir` / `error` |
| `schema/reload` | POST | `{profile?}` | `schema{…}`、`messages` |
| `open/logs` / `open/dsh` | POST | — | `ok`；`open/dsh` 另回 `url`、`opened` |
| `shutdown` | POST | 同 `quit` | `shutdown` 是 `quit` 的历史别名(旧托盘在用)，两端语义相同 |
| `convert/start` | POST | `{dir, outtype}` | `ok`、`error` |
| `browse` | POST | `{path}` | `path`、`dirs[]`、`files[{name,size}]`、`files_truncated`、`roots` |
| `args_map` | POST | `{profile}` | `flag_args`(逐参数实际启动参数) |
| `toolchains/variants` | POST | — | `variants[{name,repo,dir,installed,local_build}]`、`active`、`backend*`、`gpu_name` |
| `toolchains/variants/switch` | POST | `{name}` | `ok`、`msg`、`new_dir`；Llama 运行中 → `ok:false` |
| `toolchains/variants/remove` | POST | `{name}` | `ok` / `error` |
| `toolchains/parse_repo` | POST | `{url}` | `repo`、`name`、`dir`、`backend` |
| `toolchains/variants/install` | POST | `{repo\|url, name, dir}` | `job` |
| `toolchains/check_update` | POST | — | `local`、`latest`、`tag`、`repo`、`backend`、`asset_name`、`up_to_date`、`mismatch` |
| `toolchains/update` | POST | — | `job` |
| `node/install` | POST | — | `job` |
| `health` | POST | — | `ok`(服务端 5s 自检的对外可见面) |

### 3.4 同名但语义不同(本次已统一的部分与仍存的差异)

- **`quit` / `stop_services`**：本次统一。Windows 端点名 `quit`；Linux 新增 `quit`，
  保留 `shutdown` 作别名。两端都接受 `{"stop_services":bool}`，**默认 true**；
  且**只有 JSON `true` 算 true**(`1`/`"true"`/`null` 一律按 false)，传 `false` 只结束启动器进程、
  llama.cpp 与 DSH 继续运行(Linux 侧看门狗同时不再预备补杀这些 PID)。
- **`logs`**：本次统一参数(`n` 尾部 / `after` 游标 / `level` / `grep`)与 `count`、`boot`、`ring`、
  `seq_first_available`、`gap` 这一组游标字段，但 `lines` 的元素类型仍不同，见 §4。
- **`config/save`**：仍是两回事——Windows 把"界面当前状态"写回配置文件，带 `config` 字段会被
  明确拒绝；Linux 接收完整 `config` 对象并落盘(缺 `config` → `ok:false,"缺少 config"`)。
  跨端脚本不要指望这一条可移植。
- **`lightweight`**：本次统一(旧语义相反：Linux 不带参数=**关**，Windows=**翻转**)。现两端一致——
  `{"on":true/false}`=显式设定；`{"toggle":true}`=翻转当前；两者都不带 → `ok:false`(不再猜默认)。
  非布尔的 `on`/`toggle`：Linux 入口 **400**、Windows **`ok:false`**。`AgentCli light toggle` 发 `{"toggle":true}`。
- **`monitor` 的 `visible`**：两端都是"缺省 / `null` 视为在看(True)"；传了非布尔则 Linux **400**、Windows **`ok:false`**。
- **`profile` 回写守卫**(Linux 的 `preview` / `llama/start` / `dsh/start` / `args/map`)：请求**带** `profile`(对象)
  才把它写回运行时副本；**不带 `profile` 时不覆盖、更不清空**当前方案(本轮修掉"不带即抹成空")。
- **`job` 查询的 `id`**：`/api/job` 缺 `id` 或传空串一律 `ok:false` + `error:"缺少 id…"`，
  不再回含糊的 `未知任务 id: None`；`id` 确实查不到仍回 `ok:false` + `status:"unknown"`(见 §5)。

下列端点**同名但字段口径仍不同**(本轮未统一，跨端脚本只能各取所需，别假设可移植)：

- `preview`：Windows(忽略入参)只回 `cmd`、`llama_running`、`dsh_running`；
  Linux 回 `fmt_cmd`、`cmd_line`、`violations`、`qwen35`、`flag_args`、`vram{detail,fits,over}`，
  请求**带** `profile` 才写回运行时副本(不带不清空，见 §3.4 的回写守卫)。
- `node/status`：Windows 是扁平的 `node24_exe` / `node24_version` / `any_node_exe` / `pnpm_exe` /
  `pnpm_version`(`ok` = 是否满足 v24+pnpm 要求)；Linux 是
  `node24{path,version}` / `any_node{…}` / `pnpm{…}` / `meets` / `version_unknown` / `latest_v24`。
- `update/do`：Windows 一律走任务模型回 `{"ok":true,"job":"<id>"}`；Linux 只有 llama 分支回 `job`，
  dsh 分支同步回 `{"ok","msg"}`(见 §5 的"分钟级必须走任务模型"，这一条 Linux 仍有例外)。
- `status` 顶层字段集不同：Windows 另有 `app`/`version`/`pid`/`uptime_s`/`ui{…}`/`vram_breaker`/
  `gpus`/`system`/`paths`/`agent_port`；Linux 另有 `dsh.url`/`monitor{…}`/`lightweight`/`dsh_stale_lock`。
  跨端只依赖 §4 列出的别名口径。

## 4. 字段别名口径

- `status.llama.running`(bool) 与 `status.llama.status`(字符串 `stopped/starting/running/failed/crashed/taken_over`)
  **都给**；`status.dsh` 同理。Linux 本次补上了 `running`。
- `profile`：Windows 是**对象**(`{current,model_path,mtp_path,llama_dir,dsh_dir,active_variant,variants,
  backend,backend_auto,backend_choice}`)，
  Linux 历来是**当前方案名字符串**(webui 依赖，不改类型)。跨端只认
  **`profile_name`(字符串)** ——两端都有。
- 请求体里的 `profile`(**对象**，Linux 侧)：会**合并**进当前方案的运行时副本(2026-11-11 起)。
  以前是整体替换 —— 只带部分字段的请求(常见：只改 `model`/`--ctx` 的启动)会把用户已存方案里
  其余参数抹掉，而 safetensors 转换完成的回调 `current_profile() → save_config()` 会把那份
  残缺方案**永久写进 config.json**。不带 `profile` 时绝不用 `{}` 覆盖(不变)。UI 每次提交完整
  方案，所以对 UI 行为完全等价。Windows 的对应入口(`AgentApplyProfileArg`)只接受**方案名
  字符串**、未知名字直接报错，不接受对象 —— 这条差异跨端要写两支。
- llama.cpp 后端三件套 **`backend` / `backend_auto` / `backend_choice`**：值域 `vulkan|rocm|cuda|sycl`，
  空串=自动。`backend`=本次选预编译资产实际用的后端(方案显式选择优先，否则按显卡厂商探测)，
  `backend_auto`=探测值，`backend_choice`=方案里的显式选择(空=自动)。放法不同：Windows 在
  `status.profile` 对象里(那边 profile 是对象)，Linux 在 `status` 顶层(那边 profile 是字符串)，
  Linux 另在 `/api/toolchain/variants` 里也给同一组。对应的方案字段是 **`LlamaBackend`**(两端同名同义，
  Windows `Models/ProfileConfig.cs`、Linux `state.py` 默认方案；该字段只影响下载哪个预编译包)。
- 时间戳同时给 `sampled_at`(epoch 秒) 与 `sampled_at_utc`(ISO-8601 UTC，Windows 用 .NET `"O"` 格式，
  Linux 给毫秒精度 + `Z`)。任务另有 `started_at` / `started_at_utc`。
- `monitor` 历史序列键名统一为 **`gpu_temp` / `gpu_power` / `net_rx`**(另有 `net_tx`)；
  Windows 旧键 `temp` / `power` / `net` 作为兼容别名再保留一段时间(与统一键同值)，Linux 只用统一键。
  注意容器键名仍不同：Windows 是 `hist`，Linux 是 `hists`(Linux 本次也补了 `hist` 别名，
  指向同一个对象；浅拷贝，不改监控线程的活动快照)。
- `logs`：
  - Linux 无论 `n` 还是 `after`，一律返回 `{"lines":[{"seq":N,"level":"INFO","text":"…"}], "after":N}`。
  - Windows 只有**带 `after`** 时才返回同一种结构化行(`{seq,ts,level,text}`)；纯尾部(只给 `n`)返回
    `{"lines":["…"],"count","seq_first","seq_last"}`。
  - 两端共有的游标字段：`boot` = 本次进程启动标识(`%Y%m%d%H%M%S`-`pid`，进程内不变)、
    `ring` = 环形缓冲容量(两端都是 2000)、`seq_first_available` = 环里现存的最小 seq(环空时 = 当前 seq+1)、
    `gap` = 游标落在现存缓冲之前(中间的行已被挤出环)、`count` = 本次返回的行数。
  - ⚠ `seq` 每次进程启动都从 1 重来：客户端手上的 `after` 是上一轮的大数时会**永久返回空且不报错**。
    轮询方必须把 `boot` 存下来，本次响应里 `boot` 变了就把游标归零重读；只看 `lines == []`
    判断"没有新日志"是错的。
  - `level`(省略或 `all` = 不过滤；`error`/`warn`/`info` 大小写不敏感，Windows 还把 `warning` 归一成
    `WARN`)与 `grep`(大小写不敏感子串)在**截尾部 N 条之前**过滤，所以 `n` 与过滤条件可叠用；
    游标 `after` 只前进到**已返回行**的最大 seq，带过滤时未命中的行留在游标之前不会补发。
  - `lines[].level` 两端都给；行时间戳 `ts` 只有 Windows 给。落盘的 run.log 只在 ERROR/WARN 前加
    `[ERROR]`/`[WARN]` 标签，INFO 不加(保持肉眼可读)，按级别筛一律用 `level=`。
  - **文本两端都已脱敏**：URL 查询串 `[?&]token=…`、JSON `"token": "…"`、`Bearer …` 三种形态在
    进缓冲与写盘之前替换成 `‹已隐藏›`(DSH 启动横幅 `dsh web: http://127.0.0.1:3080/?token=…`
    实测明文进过 run.log 和 logs 响应)。要真 token 只读 `agent.json`，日志里永远拿不到。
  - **脱敏上线前落盘的明文会被回头清掉**：Linux 每次启动把已存在的 `run/info/error/crash`(.1/.2/.3 一并)
    整文件重过一遍 `redact()` 再写回(`applog.scrub_existing()`)；`/api/job` 的 `log`/`result` 也同过脱敏(见 §5)。
    Windows 侧历史 run.log 无此回头清(旧明文需手动清)，新写入一律已脱敏。
  - 落盘日志按大小滚动：单文件超过 4 MiB 滚出 `<file>.1/.2/.3`(保留 3 份历史，`.1` 最新)，两端同口径；
    DSH 噪音折叠的汇总行会指出全文所在的 boot log 路径。
  - **可移植写法**：两端都用 `GET /api/logs?after=<上次 after>&n=200`(Linux 本次补上了 GET 侧)，
    返回的都是带 `{seq,level,text}` 的结构化行。要"最新一行往后"就回传上次得到的 `after`。
  - ⚠ Windows 的 `logs` 两种入参形式都受理：GET 用 query(`n=` / `after=` / `level=` / `grep=`)；POST 的请求体以 `{`
    开头时按 JSON 解析 `n` / `after` / `level` / `grep`(数字或数字字符串均可)，其它非空文本按 query 形式用正则取参数；
    JSON 不合法直接回 `ok:false`，不再"当没带参数"退回默认尾部 200 行。
  - Linux 的 `n` 取值 1–2000(与 Windows 的 `logs` 参数上限同口径)，缺省 400；
    环形缓冲容量 2000 行，游标越过缓冲头的旧 seq 只能拿到仍在环里的部分，且响应里 `gap:true` 会明说。

## 5. 任务模型(分钟级操作必须走它)

发起端点(如 `update/do`、`toolchains/variants/install`、`node/install`)立即返回
`{"job":"<id>"}`；随后 `GET /api/job?id=<id>`(或 `POST /api/job {"id":"<id>"}`)轮询：

```json
{"ok": true, "id": "3", "name": "llama-update", "status": "running",
 "started_at": 1730000000.0, "started_at_utc": "2024-10-27T03:33:20.000Z",
 "log": ["…最近 200 行…"], "result": null}
```

- `status`：`running` / `done` / `error`；`ok` = `status != "error"`(任务存在时)。
- id 不存在(例如服务重启后)→ `ok:false` + `status:"unknown"` + `error:"未知任务 id: …"`。
  `id` 缺省 / 传空串 → `ok:false` + `error:"缺少 id…"`(与"存在但查不到"区分, 不再是 `未知任务 id: None`)。
- 两端都**只保留最近 20 个已结束任务**，`log` 只回尾部 200 行。
- **`log` 与 `result` 都已脱敏**(§4 脱敏不变量)：任务里带回的 URL/头里的 `token` / `"token"` / `Bearer` 一律
  替换为 `‹已隐藏›`。Linux 对 `log` 逐行、对 `result` **递归到字符串叶子**过 `redact()`，`dict`/`list` 结构原样保留。
- `result` 的**类型两端不同**(仍存差异)：Windows 是**字符串**(任务结果被序列化成 JSON 文本)，Linux 是**对象/数组**
  (原样 `dict`)。跨端别假设 `result` 一定是对象——Windows 那边要先 `JSON.parse` 再取字段。
- 为什么必须走任务模型：同步返回会撞处理器超时上限(Windows `AgentOp` 30s)，
  客户端先放弃、服务端还在跑，同一变更就被做两遍。

## 6. 内置 cli 退出码(Windows)

`llamadsh-cli.exe <命令>`(或 `LlamaDSH_Launcher.exe cli <命令>`)，stdout 永远是一行可解析 JSON：

| 退出码 | 含义 |
|---|---|
| 0 | 成功(响应 `ok:true` 且 HTTP 2xx) |
| 1 | 业务失败(`ok:false`)或 HTTP 4xx/5xx |
| 2 | 接口不可达 / 已被禁用(`enabled:false`、`LLAMADSH_AGENT_DISABLE=1`、发现文件读不到) |
| 3 | **超时**——结果未知，**不要直接重试**，先用 `status` / `logs` 确认；等待秒数用 `LLAMADSH_AGENT_TIMEOUT` 放宽 |

Linux 侧目前没有内置 cli，`ping` 的 `cli` 字段因此为空串；调用 curl 时按同一套退出码约定自行分类
(不可达 / 4xx-5xx / 超时)。

## 7. 双端调用示例

```bash
# 发现(两端同路径；Windows 下是 %USERPROFILE%\.dsh_launcher_logs\agent.json)
python - <<'PY'
import json, os
print(json.load(open(os.path.expanduser("~/.dsh_launcher_logs/agent.json"))))
PY

# 免 token 探活：确认服务真的在跑(发现文件不会随进程退出清理)
curl -s http://127.0.0.1:8788/api/ping      # Linux
curl -s http://127.0.0.1:17325/api/ping     # Windows

# Linux：token 走 agent.json(或旧的 ~/.dsh_launcher_token)
TOK=$(python -c 'import json,os;print(json.load(open(os.path.expanduser("~/.dsh_launcher_logs/agent.json")))["token"])')
curl -s -H "X-Auth: $TOK" -H 'Content-Type: application/json' \
     -d '{"after":0,"n":200}' http://127.0.0.1:8788/api/logs
curl -s -H "X-Auth: $TOK" -d '{"stop_services":false}' http://127.0.0.1:8788/api/quit

# Windows：同一份参数
curl -s -H "X-Auth: <token>" "http://127.0.0.1:17325/api/logs?after=0&n=200"
curl -s -H "Authorization: Bearer <token>" -d '{"stop_services":false}' http://127.0.0.1:17325/api/quit

# 只看错误 / 只看含某子串的行(两端同参数名，级别与子串都大小写不敏感)
curl -s -H "X-Auth: <token>" "http://127.0.0.1:17325/api/logs?after=0&n=200&level=error"
curl -s -H "X-Auth: $TOK" "http://127.0.0.1:8788/api/logs?after=0&n=200&level=warn&grep=OOM"
# 响应里的 boot 与上次不同 = 启动器重启过：游标必须归零重读(seq 每次启动从 1 重来)
```
