# -*- coding: utf-8 -*-
"""启动器核心运行时:日志管线、Llama/DSH 进程运行器、监控循环、显存熔断、
OOM 自动降层重试、safetensors 转换、DSH 重建。

f564fe7 语义: 旧日志解析层(ParseTimings//metrics 兜底/TTFT)已删除,
统一由 llapimon.LlamaApiMonitor 承接。
"""
import os
import re
import glob
import json
import shlex
import time
import shutil
import threading
import subprocess
import collections

import applog
import cmdline
import llapimon
import monitor
import procman
import state

TMP_DIR = os.environ.get("TMPDIR", "/tmp")
DSH_BOOT_LOG = os.path.join(TMP_DIR, "LlamaDSH_dsh_boot.log")
DSH_REBUILD_LOG = os.path.join(TMP_DIR, "LlamaDSH_dsh_rebuild.log")
DSH_REBUILD_SCRIPT = os.path.join(TMP_DIR, "LlamaDSH_dsh_rebuild.sh")
CONVERT_LOG_NAME = "llamadsh_convert.log"
TOOLS_DIR = os.path.join(os.path.expanduser("~"), ".dsh_launcher_tools")

DSH_PORT = 3080
GIB = 1073741824.0


def is_dsh_noise(content):
    """DSH 日志噪音判定(4f2ccf5 同语义)。

    错误/诊断相关行一律保留(堆栈、模块解析错误等);其余按强特征白名单放行。
    旧表含 started/ready/server/web/plugin/error 等宽子串——node 启动横幅几乎
    全部命中,隐藏功能形同虚设(环形缓冲很快滚掉关键报错),故收敛为强特征。
    """
    if not content or not content.strip():
        return False
    trimmed = content.strip()
    low = trimmed.lower()
    # 强保留: 错误/诊断信息,绝不隐藏
    force_keep = ("at ", "^", "node:internal", "file:///", "imported from",
                  "cannot find", "module", "require", "resolve", "throw")
    if any(k in low for k in force_keep):
        return False
    # 白名单收敛为强特征(listening on/ready in/eaddrinuse/127.0.0.1/localhost/dsh web/http(s)://)
    if (low.startswith("ready in")
            or "listening on" in low
            or "dsh web" in low
            or "eaddrinuse" in low
            or "127.0.0.1" in low
            or "localhost" in low
            or "http://" in low
            or "https://" in low):
        return False
    return True


# ===================================================================== 日志 ==

class LogHub:
    """UI 日志环形缓冲(2000 条,seq 增量读取,非破坏性,支持多消费者)+ run.log 环。"""

    def __init__(self):
        self.ring = collections.deque(maxlen=2000)
        self.run = collections.deque(maxlen=5000)
        self.seq = 0
        self._last_line = ""
        self._dsh_hidden = 0

    def _emit(self, text):
        self.seq += 1
        self.ring.append((self.seq, text))

    def log(self, text):
        if text == self._last_line and not text.startswith("[Perf] Error"):
            return
        self._last_line = text
        ts = time.strftime("%H:%M:%S")
        if text.startswith("[dsh] ") and is_dsh_noise(text[len("[dsh] "):]):
            self._dsh_hidden += 1
            return
        if self._dsh_hidden:
            self._emit("[dsh] ……(已隐藏 %d 行非日志内容)" % self._dsh_hidden)
            self._dsh_hidden = 0
        self._emit(text)
        self.run.append("[%s] %s" % (ts, text))
        applog.run(text)      # applog 自己带日期: 再传一遍时间戳会在 run.log 里双戳

    def error_ex(self, ex, ctx):
        applog.error_ex(ex, ctx)
        import traceback
        tb = traceback.format_exc().splitlines()[:3]
        self.log("[ERROR] %s: %s: %s" % (ctx, type(ex).__name__, ex))
        for ln in tb:
            self.log("    " + ln)

    def since(self, after, max_items=400):
        out = [it for it in self.ring if it[0] > after]
        if len(out) > max_items:
            out = out[-max_items:]
        return out, (out[-1][0] if out else after)


# ================================================================ Llama ====

class LlamaRunner:
    _start_lock = threading.Lock()  # start 的 Already-running 检查与 Popen 之间是 TOCTOU 窄窗

    def __init__(self, hub, schema_holder):
        self.hub = hub
        self.schema = schema_holder
        self.proc = None
        self.status = "stopped"  # stopped/starting/running/failed/crashed/taken_over
        self.exit_code = None
        self.last_cmd = ""
        self.last_cmd_list = []
        self.last_profile = {}
        # 端口缓存: 仅在配置/命令构建侧(HTTP 处理线程)解析,采集线程只读缓存
        # (58247ff: 跨线程读 UI 控件是采集全灭的根因)
        self._cached_port = 0
        self._port_lock = threading.Lock()
        self.api = llapimon.LlamaApiMonitor(self._port_getter)
        # OOM 自动降层重试(0314b6b + 4bf9416)
        self.oom_retry_left = 3
        self.oom_retry_active = False
        # 用户/收尾主动停止的标志: 没有它, stop() 发出的 SIGTERM 会被 _wait_exit
        # 当成"异常退出"(退出码 -15), 本轮见过 OOM 时接着自动降层重启一个
        # llama-server —— 收尾场景下那个新进程直接变成孤儿。
        self.user_stopped = False
        self.mem_state = {"oom_seen": False, "offloaded": 0, "total_layers": 0, "buffers": {}}
        self.persist_config = None  # main.py 注入: 降层落盘
        self._pump_threads = []

    @property
    def running(self):
        return self.proc is not None and self.proc.poll() is None

    # ------------------------------------------------------------ 端口 ------
    def cache_port_from_profile(self, profile):
        """配置侧解析端口并缓存: 勾选启用才可信(禁用时 BuildCmd 不发射 --port,
        面板默认 1234 是幌子 — f919fd5 根因)。"""
        defs = self.schema["defs"]
        st = cmdline.ParamState(profile, defs)
        port = 0
        d = st.role_def("port")
        if d is not None:
            v = st.raw_value(d["flag"])
            try:
                v = int(cmdline.norm_tok(v) or 0)
                if 0 < v < 65536:
                    port = v
            except (TypeError, ValueError):
                port = 0
        with self._port_lock:
            self._cached_port = port

    def _port_getter(self):
        """采集线程专用: 只读缓存 → 命令行 → API 自动发现 → 0(交盲扫)。"""
        with self._port_lock:
            cached = self._cached_port
        if cached > 0:
            return cached
        m = re.search(r"--port[= ](\d+)", self.last_cmd or "")
        if m:
            return int(m.group(1))
        if self.api._active_port > 0:
            return self.api._active_port
        return 0

    def get_port(self):
        return self._port_getter()

    # ------------------------------------------------------------ 前置检查 --
    def precheck(self, profile):
        """启动前置检查(确认链)。返回 dict;kind:
        safetensors/empty/build_error/confirm/ok。"""
        defs = self.schema["defs"]
        st_dir = cmdline.detect_safetensors_dir(profile.get("ModelPath"))
        if st_dir:
            return {"kind": "safetensors", "dir": st_dir,
                    "message": ("检测到 safetensors 模型分片,llama.cpp 无法直接加载。\n\n"
                                "是否自动转换为 GGUF?(大模型耗时较长,30B 级约 10~40 分钟)\n\n"
                                "F16(精度优先,磁盘≈源文件大小) / Q8_0(磁盘约省一半) / 取消")}
        mp, m_err = cmdline.resolve_model_path(profile.get("ModelPath"))
        if m_err:
            return {"kind": "build_error", "message": "Build failed:" + m_err}
        # http(s) 远程模型 URL 不做本地存在性检查(4f2ccf5 同语义)
        if mp and not mp.lower().startswith(("http://", "https://")) and not os.path.isfile(mp):
            return {"kind": "build_error", "message": "Build failed:模型文件不存在: %s" % mp}
        try:
            lst, violations, flag_args = cmdline.build_cmd(profile, defs)
        except Exception as ex:
            return {"kind": "build_error", "message": "Build failed:%s" % ex}
        self.last_cmd = " ".join(cmdline.qa(x) for x in lst)
        self.last_cmd_list = lst
        self.cache_port_from_profile(profile)
        if not lst:
            return {"kind": "empty", "message": "Empty"}
        occ = procman.find_process_by_port(self.get_port(), name_filter=None) if self.get_port() else None
        if occ:
            return {"kind": "build_error",
                    "message": "端口 %d 已被占用(PID %d, 可能是遗留的 llama-server 进程)。请先停止或更换端口。" % (self.get_port(), occ)}
        confirm = {}
        if violations:
            confirm["violations"] = violations
        est = cmdline.estimate_vram_needed(profile, defs, monitor.query_gpu)
        if est:
            confirm["vram"] = est["detail"]
            confirm["vram_fits"] = bool(est.get("fits"))
        qw = cmdline.qwen35_partial_offload_warning(profile, defs)
        if qw:
            confirm["qwen35"] = qw
        if confirm:
            return {"kind": "confirm", "cmd": lst, "flag_args": flag_args, **confirm}
        return {"kind": "ok", "cmd": lst, "flag_args": flag_args}

    def args_map(self, profile):
        """逐参数发射片段表(逐参数真实启动参数回显)。"""
        try:
            _, _, flag_args = cmdline.build_cmd(profile, self.schema["defs"])
            return flag_args
        except Exception:
            return {}

    # ------------------------------------------------------------ 启动 ------
    def start(self, profile, cmd_list=None, skip_vram_confirm=False):
        with LlamaRunner._start_lock:
            return self._start_locked(profile, cmd_list, skip_vram_confirm)

    def _start_locked(self, profile, cmd_list=None, skip_vram_confirm=False):
        defs = self.schema["defs"]
        if self.running:
            return False, "Already running"
        if self.status == "stop_failed":
            # 上一次"停止"没能真的杀掉进程(root 属主/杀不掉): 这时候放行启动只会在同一
            # 个端口上撞 EADDRINUSE, 用户看到的是一个没头没尾的"启动失败"。C# 也是把
            # Start 保持禁用直到 Stop 成功为止。
            return False, ("上一次停止未成功(进程仍在, 可能以 root 启动): 请先手动结束它,"
                           " 或再点一次「停止」重试")
        self.last_profile = profile
        self.user_stopped = False     # 新会话开始, 清掉上一次的"用户已停"
        self.cache_port_from_profile(profile)
        if cmd_list is None:
            try:
                lst, violations, _ = cmdline.build_cmd(profile, defs)
            except Exception as ex:
                return False, "Build failed:%s" % ex
            if violations and not skip_vram_confirm:
                return False, "参数依赖检查未通过"
            cmd_list = lst
        self.last_cmd_list = cmd_list
        self.last_cmd = " ".join(cmdline.qa(x) for x in cmd_list)
        # OOM 额度: 用户手动启动重置为 3(4bf9416);重试路径保持
        if not self.oom_retry_active:
            self.oom_retry_left = 3
        self.mem_state = {"oom_seen": False, "offloaded": 0, "total_layers": 0, "buffers": {}}
        self.api.reset_log_machine()
        try:
            env = dict(os.environ)
            env["GGML_CUDA_GRAPH_OPT"] = "1"
            p = subprocess.Popen(
                cmd_list, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                stdin=subprocess.DEVNULL, env=env,
                text=True, errors="replace", bufsize=1)
        except Exception as ex:
            self.hub.error_ex(ex, "Llama 启动失败")
            return False, str(ex)
        self.proc = p
        self.exit_code = None
        self.status = "starting"
        st = cmdline.ParamState(profile, defs)
        cmdline.log_param_hints(st, defs, self.hub.log)
        self._pump()
        threading.Thread(target=self._verify_after, args=(3.0,), daemon=True).start()
        self.hub.log("--- 正在启动 Llama ---\n" + cmdline.fmt_cmd(cmd_list, defs))
        state.update_state(LlamaPid=p.pid)
        return True, None

    def _pump(self):
        p = self.proc
        for stream in (p.stdout, p.stderr):
            t = threading.Thread(target=self._pump_line, args=(stream,), daemon=True)
            t.start()
            self._pump_threads.append(t)
        threading.Thread(target=self._wait_exit, daemon=True).start()

    def _pump_line(self, stream):
        try:
            for line in stream:
                line = line.rstrip("\r\n")
                if not line:
                    continue
                self.hub.log("[llama] " + line)
                self.api.feed_log_line(line)
                hint = self.api.note_memory_line(line, self.mem_state)
                if hint:
                    self.hub.log(hint)
        except Exception:
            pass

    def _verify_after(self, secs):
        time.sleep(secs)
        if self.proc is None:
            return
        if self.proc.poll() is not None:
            self.hub.log("Llama failed")
            self.status = "failed"
            state.clear_state_for("LlamaPid")
        else:
            self.status = "running"

    def _wait_exit(self):
        code = self.proc.wait()
        self.exit_code = code
        # 假在线冻结数据修复(4bf9416): 退出即清采集态,图表不吃冻结数据
        self.api.reset_log_machine()
        self.proc = None
        state.clear_state_for("LlamaPid")
        if self.user_stopped:
            # stop() 自己发的 SIGTERM: 退出码是 -15/-9, 绝不能走下面的"异常退出"
            # 分支, 否则本轮见过 OOM 时会自动降层再拉一个 llama-server 起来
            # (收尾/关服务时那个新进程就是孤儿)。
            self.status = "stopped"
            self.hub.log("--- Llama 已停止(用户请求) ---")
            self.oom_retry_left = 3
        elif code != 0:
            self.status = "crashed"
            self.hub.log("--- Llama 异常退出 (退出码 %s) --- 请查看上方日志,通常是显存不足或参数错误导致 ---" % code)
            self._maybe_oom_retry()
        else:
            self.status = "stopped"
            self.hub.log("--- Llama 已退出 ---")
            # 干净退出才重置 OOM 额度(4bf9416)
            self.oom_retry_left = 3

    # ------------------------------------------------------------ OOM 降层 --
    def _maybe_oom_retry(self):
        """退出码非 0 且检测到 OOM → --gpu-layers ×0.85(下限 4)自动重启,最多 3 次。"""
        if self.user_stopped:
            return                      # 二次防线: 用户/收尾要的"停"不能被重启推翻
        st = self.mem_state
        if not (st.get("oom_seen") and self.oom_retry_left > 0 and self.last_profile):
            return
        if not self.last_profile.get("OomRetryEnabled", True):
            return
        defs = self.schema["defs"]
        sp = cmdline.ParamState(self.last_profile, defs)
        ngl_def = sp.role_def("ngl")
        cur = None
        if ngl_def is not None:
            v = sp.raw_value(ngl_def["flag"])
            cur = cmdline._try_float(v)
        base = None
        if cur is not None and cur > 0:
            base = int(cur)
        elif st.get("offloaded", 0) > 0:
            base = st["offloaded"]
        elif st.get("total_layers", 0) > 0:
            base = st["total_layers"]
        if not base:
            self.hub.log("[OOM] 无法确定当前卸载层数,放弃自动降层(请手动调小 --gpu-layers 或上下文)")
            return
        new_ngl = max(4, int(base * 0.85))
        if new_ngl >= base:
            new_ngl = base - 1
        if new_ngl < 4:
            self.hub.log("[OOM] 降层后仍低于 4 层,放弃重试。请调小 ctx、量化 KV 或换更小的量化。")
            return
        flag = ngl_def["flag"] if ngl_def is not None else "gpu-layers"
        vp = self.last_profile.setdefault("ValueParams", {})
        vp[flag] = {"Enabled": True, "Value": str(new_ngl)}
        self.oom_retry_left -= 1
        self.oom_retry_active = True
        self.hub.log("[OOM] 自动降层重试: --gpu-layers %d → %d(剩余重试 %d 次)" % (base, new_ngl, self.oom_retry_left))
        threading.Thread(target=self._relaunch, daemon=True).start()

    def _relaunch(self):
        time.sleep(1.0)
        # 决策与执行之间隔了 1s: 这期间用户点"停止"或走收尾都改不了已经排下的重启。
        # user_stopped 是**实例级**标志, 只挡决策点(core.py:321/341)挡不住这里在途的
        # 那一次 —— 收尾场景下 os._exit 之前 Popen 出来的 llama-server 直接是孤儿。
        if self.user_stopped or self.running:
            self.oom_retry_active = False
            self.hub.log("[OOM] 已放弃自动降层重启(进程已被停止或已被接管)")
            return
        ok, msg = self.start(self.last_profile, skip_vram_confirm=True)
        if not ok:
            self.hub.log("[OOM] 重试启动失败: %s" % msg)
            self.oom_retry_active = False

    def oom_recredit(self):
        """API 在线补重置 + 降层值落盘(4bf9416: 否则下次启动回到 OOM 原值重演崩溃循环)。"""
        if self.oom_retry_active and self.api.online:
            self.oom_retry_active = False
            self.oom_retry_left = 3
            self.hub.log("[OOM] 服务已稳定在线,重试额度已恢复,降层值已写入方案")
            if self.persist_config:
                try:
                    self.persist_config()
                except Exception:
                    pass

    # ------------------------------------------------------------ 停止 ------
    def stop(self):
        port = self.get_port()
        if self.proc is not None:
            pid = self.proc.pid
        elif port > 0:
            pid = procman.find_process_by_port(port, name_filter=("llama-server",))
        else:
            pid = None
        # 先立标志再杀: _wait_exit 会看到它, 不会把 SIGTERM 的 -15 当崩溃去 OOM 重启
        self.user_stopped = True
        if pid is None:
            # 没在跑(或已经自己退出): 幂等收尾, 不谎报"停止失败"也不抛异常
            self._finish_stop(quiet=True)
            return
        self.hub.log("--- 正在停止 Llama ---")
        killed = procman.kill_process_tree(pid)
        if not killed:
            # kill_process_tree 的返回值以前被丢掉, 于是 root 属主/杀不掉的进程
            # 也照样打印"已停止", 面板还把 Start 重新点亮(再点就是撞端口)
            self.hub.log("[Llama] ⚠ 进程 PID %s 未能停止(可能以 root 启动), 请手动结束" % pid)
        self._finish_stop(ok=killed)

    def _finish_stop(self, quiet=False, ok=True):
        self.proc = None
        # stop_failed 不是 stopped: 进程还在。前端据此保持 Stop 可点、Start 禁用,
        # 日志与状态标签都要能看出"没停掉"(C# FinishStopLlama 的"仍在运行"同语义)。
        self.status = "stopped" if ok else "stop_failed"
        self.api.reset_log_machine()
        self.mem_state = {"oom_seen": False, "offloaded": 0, "total_layers": 0, "buffers": {}}
        self.oom_retry_active = False
        if not quiet:
            self.hub.log("--- 已停止 ---")
        state.clear_state_for("LlamaPid")


# ================================================================== DSH =====

class DshRunner:
    def __init__(self, hub, schema_holder):
        self.hub = hub
        self.schema = schema_holder
        self.tracker = None
        self.proc = None
        self.status = "stopped"
        self.admin_mode = False
        self.tailer_stop = threading.Event()
        self.verify_stop = threading.Event()
        self.rebuild_stop = threading.Event()
        self.retries_left = 0
        # 与 LlamaRunner 对称: 没有它, `_verify_loop` 的"启动失败自动重试"会在用户点
        # 停止或正在收尾之后再拉一个分离会话的 DSH(孤儿 + 留 stale lock)。C# 里
        # StartDsh 与 StopDshVerifyLoop 同在 UI 线程, 结构上不可能撞车, 所以那边没有
        # 这个标志 —— 多线程的 Linux 侧必须自己挡。
        self.user_stopped = False
        self.last_profile = {}
        self._tailer_fh = None
        self._deadline = 0.0

    @property
    def running(self):
        if self.tracker is not None:
            return self.tracker.poll() is None
        return self.proc is not None and self.proc.poll() is None

    @staticmethod
    def lock_path():
        return os.path.join(os.path.expanduser("~"), ".dsh", "task-board", "ledger-v2.lock")

    def stale_lock_present(self):
        lp = self.lock_path()
        if not os.path.exists(lp):
            return False
        if procman.find_process_by_port(DSH_PORT):
            return False
        return not procman.node_host_running()

    @staticmethod
    def git_head(dsh_dir):
        try:
            out = subprocess.run(["git", "rev-parse", "HEAD"], cwd=dsh_dir,
                                 capture_output=True, text=True, timeout=15).stdout.strip()
            if re.fullmatch(r"[0-9a-f]{40}", out):
                return out
        except Exception:
            pass
        return ""

    def resolve_mode(self, profile):
        if (profile.get("DshMode") or 0) == 1:
            return "npx"
        dsh_dir = (profile.get("DshDir") or "").strip().strip('"') or "deepseek-harness"
        if os.path.isfile(os.path.join(dsh_dir, "apps", "cli", "lib", "bin.js")) and self.retries_left > 0:
            return "compiled-js"
        return "compiled-tsx"

    def start(self, profile):
        if self.running:
            return False, "Already running"
        self.last_profile = profile
        # 每次启动会话截断 boot log: append 模式下旧失败(ERR_MODULE_NOT_FOUND 等)
        # 会毒化后续所有尝试的快速失败判定
        try:
            open(DSH_BOOT_LOG, "wb").close()
        except OSError:
            pass
        existing = procman.find_process_by_port(DSH_PORT)
        if existing:
            self.attach(existing, "检测到端口 %d 已有 DSH 在运行,直接接管" % DSH_PORT)
            return True, None
        self.retries_left = 2
        return self._prepare_then_start(profile)

    def _prepare_then_start(self, profile):
        if self.stale_lock_present():
            try:
                os.remove(self.lock_path())
                self.hub.log("[DSH] 已清理残留锁: " + self.lock_path())
            except OSError as ex:
                self.hub.error_ex(ex, "清理残留锁失败")
        dsh_dir = (profile.get("DshDir") or "").strip().strip('"')
        mode = self.resolve_mode(profile)
        if mode != "npx" and dsh_dir:
            head = self.git_head(dsh_dir)
            bs = state.load_build_state()
            if head and bs.get("head") and bs["head"] != head:
                return {"need_rebuild": True, "head": head, "old_head": bs["head"],
                        "message": ("DSH 仓库已升级(%s → %s),旧编译产物大概率报错。\n"
                                    "需要全量重建:pnpm clean / build:lib / build:web。\n\n"
                                    "是 = 现在自动重建后启动\n否 = 跳过重建直接启动(可能报旧产物错误)" % (bs["head"][:8], head[:8]))}
            if head and not bs.get("head"):
                state.save_build_state(head)
        return self._start_inner(profile)

    def _sudo_preflight(self):
        """管理员模式预检: 返回 None = 可无人值守提权, 否则返回不可用的原因。

        不预检的后果: `sudo -n` **立刻**退出(报错只落在 DSH boot log 里), 而 _spawn 只
        判断 Popen 是否成功 → 白等 30s 超时 + 自动重试再一次, 用户只看到
        "DSH failed (启动超时)"。两类真实原因:
          - NoNewPrivs: 启动器进程继承了内核 NNP 位(从受限会话/沙箱里拉起的后台服务),
            该位禁止任何 exec 获得新权限 → sudo 永久失效
          - 需要密码: 无人值守服务无法交互输密码
        """
        if not procman.which("sudo"):
            return "环境里没有 sudo"
        try:
            r = subprocess.run(["sudo", "-n", "true"], stdout=subprocess.DEVNULL,
                               stderr=subprocess.PIPE, timeout=8)
        except Exception as ex:
            return "sudo 预检异常: %s" % ex
        if r.returncode == 0:
            return None
        lines = (r.stderr or b"").decode("utf-8", "replace").strip().splitlines()
        reason = lines[0] if lines else ("sudo -n 退出码 %d" % r.returncode)
        low = reason.lower()
        if "no new privileges" in low:
            reason += " —— 启动器进程带 NoNewPrivs 位(常见于从受限会话/沙箱里拉起的后台服务)," \
                      "请从应用菜单/桌面图标重新启动启动器"
        elif "password" in low:
            reason += " —— 后台服务无法交互输密码; 确需提权请在 sudoers 为该用户配置 NOPASSWD"
        return reason

    def _start_inner(self, profile):
        mode = self.resolve_mode(profile)
        dsh_dir = (profile.get("DshDir") or "").strip().strip('"') or "deepseek-harness"
        self.proc = None
        self.tracker = None
        self.status = "starting"
        self.user_stopped = False
        self.verify_stop.clear()
        self._deadline = time.time() + 30.0

        # 不拼 bash -c 脚本:目录走 Popen 的 cwd,命令走 argv 列表——
        # DshDir 是 webui 可编辑字段,拼进 shell 等于命令注入。
        inner = {"compiled-js": ["node", "apps/cli/lib/bin.js", "web"],
                 "compiled-tsx": ["pnpm", "dsh", "web"],
                 "npx": ["npx", "--yes", "@deepseek-ai/dsh", "web"]}[mode]
        want_admin = bool(profile.get("DshAdminMode"))
        demoted = False
        if want_admin:
            why = self._sudo_preflight()
            if why:
                # 与 Windows 不同: 那边提权是读游戏内存的硬需求(失败必须停), Linux 下
                # DSH 装在用户目录, root 并非必需 → 降级普通启动并显式告知原因
                self.hub.log("[DSH] ⚠ 管理员模式不可用: %s" % why)
                self.hub.log("[DSH] 本次自动按普通启动(Linux 下 DSH 不需要 root);"
                             "确需提权请给该用户配 sudoers NOPASSWD, 或取消勾选“管理员模式”")
                want_admin, demoted = False, True
        if want_admin:
            # 提权启动要显式带环境与绝对路径: sudo 的 env_reset 会把 HOME 变成 /root
            # (DSH 会在 /root/.local/share 下另建一套数据), 而 secure_path 里没有
            # ~/.local/share/pnpm → `sudo pnpm` 直接 command not found。
            binx = procman.which(inner[0]) or inner[0]
            argv = (["sudo", "-n", "env",
                     "HOME=" + os.path.expanduser("~"),
                     "PATH=" + (os.environ.get("PATH") or "")]
                    + [binx] + inner[1:])
            self.hub.log("--- 正在启动 DSH (%s · sudo 提权) ---" % mode)
            if not self._spawn(argv, detached=True, cwd=dsh_dir or None):
                self.hub.log("[DSH] sudo 提权启动失败")
                self.status = "failed"
                state.clear_state_for("DshPid")
                return False, "sudo 启动失败"
            self.admin_mode = True
        else:
            self.admin_mode = False
            if mode == "compiled-js":
                argv = ["node", "apps/cli/lib/bin.js", "web"]
            elif mode == "compiled-tsx":
                if os.path.isfile(os.path.join(dsh_dir, "apps", "cli", "lib", "bin.js")):
                    self.hub.log("--- 编译版启动失败,本次回退到 pnpm dsh web ---")
                argv = ["pnpm", "dsh", "web"]
            else:
                argv = ["npx", "--yes", "@deepseek-ai/dsh", "web"]
            self.hub.log("--- 正在启动 DSH (%s · 分离模式%s) ---"
                         % (mode, " · 管理员模式已降级" if demoted else ""))
            if not self._spawn(argv, detached=True, cwd=dsh_dir or None):
                self.status = "failed"
                return False, "启动失败"
        self._start_tailer()
        threading.Thread(target=self._verify_loop, daemon=True).start()
        return True, None

    def _spawn(self, argv, detached=True, cwd=None):
        try:
            fh = open(DSH_BOOT_LOG, "ab")
            kwargs = {}
            if detached:
                kwargs["start_new_session"] = True
            if cwd:
                kwargs["cwd"] = cwd
            self.proc = subprocess.Popen(argv, stdout=fh, stderr=subprocess.STDOUT,
                                         stdin=subprocess.DEVNULL, **kwargs)
            self._tailer_fh = fh
            return True
        except Exception as ex:
            self.hub.error_ex(ex, "DSH 启动失败")
            return False

    def _start_tailer(self):
        self.tailer_stop.clear()
        threading.Thread(target=self._tail_loop, daemon=True).start()

    def _stop_tailer(self):
        self.tailer_stop.set()
        if self._tailer_fh:
            try:
                self._tailer_fh.close()
            except OSError:
                pass
            self._tailer_fh = None

    def _tail_loop(self):
        pos = 0
        try:
            if os.path.exists(DSH_BOOT_LOG):
                pos = os.path.getsize(DSH_BOOT_LOG)
            else:
                open(DSH_BOOT_LOG, "wb").close()
        except OSError:
            pass
        while not self.tailer_stop.is_set():
            try:
                if os.path.exists(DSH_BOOT_LOG):
                    size = os.path.getsize(DSH_BOOT_LOG)
                    if size < pos:
                        pos = 0
                    if size > pos:
                        with open(DSH_BOOT_LOG, "r", encoding="utf-8", errors="replace") as f:
                            f.seek(pos)
                            chunk = f.read()
                        pos = f.tell()
                        for ln in chunk.splitlines():
                            if ln.strip():
                                self.hub.log("[dsh] " + ln)
            except OSError:
                pass
            self.tailer_stop.wait(0.4)

    def _verify_loop(self):
        while not self.verify_stop.is_set():
            pid = procman.find_process_by_port(DSH_PORT)
            fast_fail = False
            if not pid and os.path.exists(DSH_BOOT_LOG):
                try:
                    with open(DSH_BOOT_LOG, "r", encoding="utf-8", errors="replace") as f:
                        txt = f.read()
                    if "ERR_MODULE_NOT_FOUND" in txt or "Command failed with exit code" in txt:
                        fast_fail = True
                except OSError:
                    pass
            if pid:
                self.verify_stop.set()
                self.tracker = procman.ProcessTracker(pid)
                self.status = "running_admin" if self.admin_mode else "running"
                state.update_state(DshPid=pid, DshDir=self.last_profile.get("DshDir", ""))
                self.hub.log("DSH real PID:%d" % pid)
                return
            if fast_fail or time.time() >= self._deadline:
                self.verify_stop.set()
                if self.user_stopped:
                    # 用户已经要"停"(或正在收尾): 既不该再自动重试, 也不该把状态
                    # 落成 failed —— 那会让面板从"已停止"又跳成"失败"。
                    self.hub.log("--- DSH 未启动成功, 但已按用户请求停止, 不再自动重试 ---")
                    self.status = "stopped"
                    state.clear_state_for("DshPid")
                    return
                self.hub.log("DSH failed (%s)" % ("启动日志报错" if fast_fail else "启动超时"))
                if self.retries_left > 0:
                    self.retries_left -= 1
                    self.hub.log("--- DSH 启动失败,自动重试(剩余 %d 次)---" % self.retries_left)
                    self._start_inner(self.last_profile)
                else:
                    self.status = "failed"
                    state.clear_state_for("DshPid")
                return
            self.verify_stop.wait(2.0)

    def attach(self, pid, log_prefix, save_state=True):
        self.tracker = procman.ProcessTracker(pid)
        self.status = "running"
        self.hub.log("%s (PID %d)" % (log_prefix, pid))
        if save_state:
            state.update_state(DshPid=pid)

    def stop(self):
        self.verify_stop.set()
        self._stop_tailer()
        # PID 复用防线: tracker 带 StartTime, poll() 非 None 就说明这个 PID 已经不是
        # 我们记下的那一个(接管来的 DSH 退出后 PID 被复用是真实发生过的场景)。以前
        # 直接拿 tracker.pid 送进 kill_process_tree → 给无辜进程发 SIGTERM 再 SIGKILL
        # 整树。C# 侧的 HasDsh() + ProcessTracker.VerifyStartTime 就是防这个。
        if self.tracker is not None and self.tracker.poll() is not None:
            self.hub.log("[DSH] 记录的进程 PID %d 已不是当时那个进程(PID 被复用), 不动它"
                         % self.tracker.pid)
            self.tracker = None
        if self.proc is not None and self.proc.poll() is not None:
            self.proc = None
        # 先立标志: _verify_loop 的自动重试/失败落账都要看到"用户要的是停"
        self.user_stopped = True
        pid = self.tracker.pid if self.tracker else (self.proc.pid if self.proc else None)
        if pid:
            self.hub.log("--- 正在停止 DSH ---")
            procman.kill_process_tree(pid)
            if procman.alive(pid):
                if procman.which("sudo"):
                    try:
                        # 只杀根 PID 会把 root 属主的 pnpm/node 子进程留在原地
                        # (C# 用的是 taskkill /F /T, 整树)
                        try:
                            targets = procman.child_pids(pid)
                        except Exception:
                            targets = []
                        if pid not in targets:
                            targets.append(pid)
                        r = subprocess.run(["sudo", "-n", "kill", "-9"]
                                           + [str(p) for p in targets],
                                           capture_output=True, text=True,
                                           encoding="utf-8", errors="replace", timeout=15)
                        time.sleep(0.6)
                        if not procman.alive(pid):
                            self.hub.log("--- 提权 DSH 进程普通停止无效, 已用 sudo kill 停止"
                                         "(含 %d 个子进程) ---" % max(0, len(targets) - 1))
                        elif r.returncode == 0:
                            self.hub.log("[DSH] ⚠ 已下发 sudo kill 但进程 PID %d 仍在, 请手动结束" % pid)
                        else:
                            # 以前 Popen 不查返回码, sudo 要密码/无 NOPASSWD 时也报"已下发"
                            self.hub.log("[DSH] ⚠ 进程 PID %d 属主不是本人且提权失败(%s), 请手动 sudo kill"
                                         % (pid, ((r.stderr or r.stdout or "").strip() or "无权限")[:120]))
                    except OSError as ex:
                        self.hub.log("[DSH] 无法停止进程 PID %d,请手动结束: %s" % (pid, ex))
                else:
                    self.hub.log("[DSH] 进程 PID %d 仍存活,请手动结束" % pid)
        self.tracker = None
        self.proc = None
        # 同上: 复核过还在的进程不能落成"已停止"(面板会把 Start 点亮, 再点就是撞端口)
        self.status = "stopped"
        if pid and procman.alive(pid):
            self.status = "stop_failed"
        if pid:                         # 从没运行过就别报告"已停止"
            self.hub.log("--- 已停止 DSH ---" if self.status == "stopped"
                         else "[DSH] ⚠ 停止未成功, 进程仍在(PID %d), 状态记为停止失败" % pid)
        state.clear_state_for("DshPid")

    def rebuild(self, profile, on_done=None):
        dsh_dir = (profile.get("DshDir") or "").strip().strip('"')
        if not dsh_dir or not os.path.isdir(os.path.join(dsh_dir, "apps", "cli")):
            return False, "无效的 DSH 目录(缺少 apps/cli): %s" % dsh_dir
        head = self.git_head(dsh_dir)
        if not head:
            return False, "无法读取 git HEAD(不是有效的 git 仓库)"
        if self.running:
            self.stop()
            for _ in range(10):
                if not self.running:
                    break
                time.sleep(2)
            if self.running:
                return False, "DSH 仍在运行,中止重建"
        script = "\n".join([
            "#!/usr/bin/env bash",
            "cd %s || exit 1" % shlex.quote(dsh_dir),
            "echo \"=== 1/3 pnpm clean ===\"",
            "pnpm clean",
            "if [ $? -ne 0 ]; then echo EXITCODE:1; exit 1; fi",
            "echo \"=== 2/3 pnpm build:lib ===\"",
            "pnpm build:lib",
            "if [ $? -ne 0 ]; then echo EXITCODE:1; exit 1; fi",
            "echo \"=== 3/3 vite build ===\"",
            "cd apps/web && node node_modules/vite/bin/vite.js build",
            "if [ $? -ne 0 ]; then echo EXITCODE:1; exit 1; fi",
            "echo EXITCODE:0",
        ]) + "\n"
        with open(DSH_REBUILD_SCRIPT, "w", encoding="utf-8") as f:
            f.write(script)
        try:
            os.chmod(DSH_REBUILD_SCRIPT, 0o755)
            open(DSH_REBUILD_LOG, "wb").close()
            subprocess.Popen(["bash", DSH_REBUILD_SCRIPT], stdout=open(DSH_REBUILD_LOG, "ab"),
                             stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
                             start_new_session=True)
        except Exception as ex:
            return False, "重建启动失败: %s" % ex
        self.status = "rebuilding"
        self.hub.log("--- DSH 全量重建已启动(clean → build:lib → vite build)---")
        threading.Thread(target=self._poll_rebuild, args=(head, on_done), daemon=True).start()
        return True, None

    def _poll_rebuild(self, head, on_done):
        deadline = time.time() + 30 * 60
        while time.time() < deadline and not self.rebuild_stop.is_set():
            time.sleep(2)
            try:
                with open(DSH_REBUILD_LOG, "r", encoding="utf-8", errors="replace") as f:
                    txt = f.read()
            except OSError:
                continue
            for ln in txt.splitlines():
                if ln.startswith("===") or ln.startswith("EXITCODE"):
                    self.hub.log("[dsh-rebuild] " + ln)
            if "EXITCODE:0" in txt:
                state.save_build_state(head)
                self.hub.log("--- DSH 重建完成 ---")
                self.status = "stopped"
                if on_done:
                    on_done(True)
                return
            if "EXITCODE:1" in txt:
                self.hub.log("--- DSH 重建失败,请查看日志: %s ---" % DSH_REBUILD_LOG)
                self.status = "stopped"
                if on_done:
                    on_done(False)
                return
        self.hub.log("--- DSH 重建超时(30 分钟)---")
        self.status = "stopped"
        if on_done:
            on_done(False)

    def rebuild_running(self):
        return self.status == "rebuilding"


# ============================================================ 监控/熔断 =====

class Monitor:
    """性能采集线程: GPU/系统 + LlamaApiMonitor 轮询 + 60 点历史序列 + 显存熔断。

    采样节奏(CS 语义): llama 运行中或监测页可见 → 3s;否则 30s。
    """

    def __init__(self, hub, llama: LlamaRunner, dsh: DshRunner):
        self.hub = hub
        self.llama = llama
        self.dsh = dsh
        self.active_profile = {}
        self.breaker_triggered = False
        self.monitor_visible = False
        # 轻量化模式(QQ/微信式最小化到托盘): 大幅降低采样频率, 无服务存活时不做任何
        # 子进程采样——llama/dsh 都没跑时后台几乎零占用
        self.lightweight = False
        self.snapshot = {}
        self.h = {k: llapimon.Hist() for k in
                  ("tps", "pf", "ctx", "mtp", "gpu_util", "gpu_mem", "gpu_temp", "gpu_power",
                   "cpu", "ram", "net_rx", "net_tx")}

    def clear_llama_hists(self):
        for k in ("tps", "pf", "ctx", "mtp"):
            self.h[k].clear()

    def _active_slot(self):
        for s in self.llama.api.slots:
            if s.get("processing"):
                return s
        return None

    def loop(self):
        while True:
            try:
                # 存活判定(CS IsLlamaRunning 语义): 进程对象 或 端口属主 或 API 在线
                llama_alive = self.llama.running
                api_online = self.llama.api.online
                dsh_alive = self.dsh.running
                if not llama_alive and not api_online:
                    port = self.llama.get_port()
                    if port and procman.find_process_by_port(port, name_filter=("llama-server",)):
                        llama_alive = True
                if not llama_alive:
                    self.breaker_triggered = False
                if self.lightweight:
                    # 轻量化: 有服务存活 → 30s(保熔断/接管安全); 无服务 → 60s 纯休眠,
                    # 不做任何子进程采样(nvidia-smi/ss/statvfs 全部跳过)
                    busy = llama_alive or api_online or dsh_alive
                    interval = 30.0 if busy else 60.0
                    if busy:
                        if llama_alive or api_online:
                            self.llama.api.poll_fast()
                            self.llama.api.poll_slow_if_needed()
                            self.llama.oom_recredit()
                        gpus = monitor.query_gpus()
                        sysi = monitor.sample_system()
                        vt_sum, vu_sum = self._vram_sums(gpus)
                        brk = self.active_profile or {}
                        if (brk.get("VramBreakerEnabled") and llama_alive and vt_sum > 0
                                and not self.breaker_triggered):
                            val = brk.get("VramBreakerValue", 90.0)
                            hit = ((brk.get("VramBreakerType", 0) == 0 and vu_sum / vt_sum * 100.0 >= val)
                                   or (brk.get("VramBreakerType", 0) == 1 and vu_sum / GIB >= val))
                            if hit:
                                self.breaker_triggered = True
                                self.hub.log("[熔断] 显存使用超限,自动停止 Llama 服务")
                                self.llama.stop()
                        gen, prompt, _src = self._collect_speeds()
                        self.snapshot = self._build_snapshot(gpus, sysi, gen, prompt,
                            "api", -1, 0, vt_sum, vu_sum)
                    else:
                        self.snapshot.update({"lightweight": True,
                                              "llama_status": self.llama.status,
                                              "dsh_status": self.dsh.status})
                    time.sleep(interval)
                    continue
                interval = 3.0 if (llama_alive or api_online or self.monitor_visible) else 30.0
                if llama_alive or api_online or self.monitor_visible:
                    self.llama.api.poll_fast()
                    self.llama.api.poll_slow_if_needed()
                    self.llama.oom_recredit()
                gpus, sysi, (gen, prompt, src) = self._sample_all()
                self.h["tps"].push(gen if gen > 0 else None)
                self.h["pf"].push(prompt if prompt > 0 else None)
                ctx_used, ctx_total = self.llama.api.ctx_view()
                self.h["ctx"].push(ctx_used if ctx_used >= 0 else None)
                acc = self.llama.api.mtp_acceptance
                self.h["mtp"].push(acc * 100.0 if acc is not None else None)  # b3d3527: 历史按百分数
                utils = [c["util"] for c in gpus if c.get("util") is not None]
                vus = [c.get("vram_used") for c in gpus if c.get("vram_used") is not None]
                vts = [c.get("vram_total") for c in gpus if c.get("vram_total") is not None]
                temps = [c.get("temp") for c in gpus if c.get("temp") is not None]
                pws = [c.get("power_w") for c in gpus if c.get("power_w") is not None]
                self.h["gpu_util"].push(round(sum(utils) / len(utils), 1) if utils else None)
                self.h["gpu_mem"].push(round(sum(vus) / GIB, 2) if vus else None)
                self.h["gpu_temp"].push(max(temps) if temps else None)
                self.h["gpu_power"].push(round(sum(pws), 1) if pws else None)
                self.h["cpu"].push(sysi.get("cpu"))
                self.h["ram"].push(round((sysi.get("mem_used") or 0) / GIB, 2))
                nets = sysi.get("net") or {}
                rx = round(sum(v[0] for v in nets.values()), 2) if nets else None
                tx = round(sum(v[1] for v in nets.values()), 2) if nets else None
                self.h["net_rx"].push(rx)
                self.h["net_tx"].push(tx)
                # 显存熔断: 百分比=各卡 used 和/total 和;GB 模式=used 和
                brk = self.active_profile or {}
                vt_sum = sum(vts) if vts else 0.0
                vu_sum = sum(vus) if vus else 0.0
                if (brk.get("VramBreakerEnabled") and llama_alive and vt_sum > 0
                        and not self.breaker_triggered):
                    val = brk.get("VramBreakerValue", 90.0)
                    hit = ((brk.get("VramBreakerType", 0) == 0 and vu_sum / vt_sum * 100.0 >= val)
                           or (brk.get("VramBreakerType", 0) == 1 and vu_sum / GIB >= val))
                    if hit:
                        self.breaker_triggered = True
                        self.hub.log("[熔断] 显存使用超限,自动停止 Llama 服务")
                        self.llama.stop()
                self.snapshot = self._build_snapshot(gpus, sysi, gen, prompt, src,
                                                     ctx_used, ctx_total, vt_sum, vu_sum)
            except Exception as ex:
                self.hub.log("[Perf] Error: %s" % ex)
            time.sleep(interval)

    def _collect_speeds(self):
        return self.llama.api.speeds()

    def _vram_sums(self, gpus):
        vus = [c.get("vram_used") for c in gpus if c.get("vram_used") is not None]
        vts = [c.get("vram_total") for c in gpus if c.get("vram_total") is not None]
        return (sum(vus) if vus else 0.0), (sum(vts) if vts else 0.0)

    def _sample_all(self):
        gpus = monitor.query_gpus()
        sysi = monitor.sample_system()
        return gpus, sysi, self.llama.api.speeds()

    def _phase_pill(self, alive_effective):
        api = self.llama.api
        if self.breaker_triggered:
            return "已熔断", "#ff8a98", "#2eff5a6e"
        if not alive_effective:
            return "llama 离线", "#ff8a98", "#2eff5a6e"
        if not api.online:
            return "未知(API)", "#f8c96a", "#2ef5b53d"
        if api.phase == "decoding":
            return "生成中", "#7cd7f7", "#2e7cd7f7"
        if api.phase == "prompt_processing":
            return "Prompt 处理中", "#4ade80", "#2e34d399"
        return "空闲", "#8e979f", "#1a8e979f"

    def _build_snapshot(self, gpus, sysi, gen, prompt, src, ctx_used, ctx_total, vt_sum, vu_sum):
        api = self.llama.api
        pill = self._phase_pill(self.llama.running or api.online)
        events, total_events = api.events_snapshot()
        hists = {k: h.snapshot() for k, h in self.h.items()}
        return {
            "llama_status": self.llama.status,
            "llama_alive": self.llama.running or self.llama.api.online,
            "online": api.online,
            "active_port": api._active_port,
            "phase": api.phase,
            "pill": {"text": pill[0], "fg": pill[1], "bg": pill[2]},
            "speeds": {"gen": gen, "prompt": prompt, "source": src,
                       "gen_ft": llapimon.fmt_tps(gen), "prompt_ft": llapimon.fmt_tps(prompt)},
            "gen_detail": {"tg": api.tg_tps, "tg3": api.tg3s_tps,
                           "log_prompt": api.log_prompt_speed,
                           "decoded": api.log_n_decoded,
                           "last_prefill": api.last_prefill_speed},
            "ctx": {"used": ctx_used, "total": ctx_total,
                    "pct": round(ctx_used * 100.0 / ctx_total, 1) if ctx_used >= 0 and ctx_total > 0 else None},
            "mtp": {"acc": api.mtp_acceptance, "accepted": api.mtp_accepted,
                    "generated": api.mtp_generated, "mean_len": api.mtp_mean_len,
                    "graphs": api.graphs_reused},
            "progress": {"prompt_progress": api.prompt_progress,
                         "prompt_total": api.prompt_total_tokens,
                         "prompt_elapsed": api.prompt_elapsed_s},
            "task": api.last_task,
            "task_started_at": api.task_started_at,
            "slots": api.slots,
            "events": list(reversed(events)),   # 新 → 旧
            "total_events": total_events,
            "model": api.model_info(),
            "boot": api.boot,
            "gpus": gpus,
            "sys": sysi,
            "hists": hists,
            "monitor_visible": self.monitor_visible,
            "breaker_triggered": self.breaker_triggered,
            "vram": {"used": vu_sum, "total": vt_sum,
                     "pct": round(100.0 * vu_sum / vt_sum, 1) if vt_sum > 0 else 0.0},
        }


# ====================================================== safetensors 转换 ====

class Converter:
    """safetensors → GGUF 分离转换(python3 convert_hf_to_gguf.py,EXITCODE 标记判结束)。"""

    def __init__(self, hub):
        self.hub = hub
        self.running = False
        self._log_pos = 0

    def find_convert_script(self):
        cands = [os.path.join(TOOLS_DIR, "convert_hf_to_gguf.py")]
        for root in (os.path.join(os.path.expanduser("~"), "llama.cpp"), "/opt/llama.cpp", "/usr/local/share/llama.cpp"):
            cands.append(os.path.join(root, "convert_hf_to_gguf.py"))
        for c in cands:
            if os.path.isfile(c):
                return c
        return None

    def start(self, src_dir, outtype, on_done):
        # 白名单前置:outtype 直接来自请求体,后面会拼进产物文件名与命令行
        if outtype not in ("f16", "f32", "q8_0"):
            self.hub.log("[转换] 非法 outtype: %r(仅支持 f16/f32/q8_0),中止" % (outtype,))
            on_done(None)
            return
        script = self.find_convert_script()
        if not script:
            self.hub.log("[转换] 未找到 convert_hf_to_gguf.py(需要 llama.cpp 仓库或 ~/.dsh_launcher_tools)。")
            on_done(None)
            return
        src_bytes = sum(os.path.getsize(f) for f in glob.glob(os.path.join(src_dir, "*.safetensors")))
        dirname = src_dir.rstrip("/\\")
        out_file = os.path.join(src_dir, os.path.basename(dirname) + "-" + outtype + ".gguf")
        if os.path.exists(out_file):
            exist_len = os.path.getsize(out_file)
            header_ok = exist_len > 24 and open(out_file, "rb").read(4) == b"GGUF"
            factor = 0.9 if outtype == "f16" else 0.45
            if header_ok and exist_len >= src_bytes * factor:
                self.hub.log("[转换] 已存在有效的转换产物(%.1f GiB),跳过转换: %s" % (exist_len / cmdline.GIB, out_file))
                on_done(out_file)
                return
            try:
                os.remove(out_file)
                self.hub.log("[转换] 已删除上次中断留下的不完整产物,重新转换")
            except OSError:
                pass
        need = src_bytes * (1.05 if outtype == "f16" else 0.62)
        free = shutil.disk_usage(src_dir).free
        if free < need:
            self.hub.log("[转换] 磁盘空间不足(需要 %.1f GiB,可用 %.1f GiB),中止" % (need / cmdline.GIB, free / cmdline.GIB))
            on_done(None)
            return
        try:
            subprocess.run(["python3", "-c", "import gguf, torch, transformers"],
                           capture_output=True, timeout=60)
            deps_ok = True
        except Exception:
            deps_ok = False
        if not deps_ok:
            self.hub.log("[转换] 自动安装依赖: gguf torch transformers sentencepiece ...")
            for idx in ("https://pypi.tuna.tsinghua.edu.cn/simple", "https://pypi.org/simple"):
                r = subprocess.run(["python3", "-m", "pip", "install", "--disable-pip-version-check",
                                    "--progress-bar", "off", "gguf", "torch", "transformers",
                                    "sentencepiece", "-i", idx],
                                   capture_output=True, text=True, timeout=3600)
                if r.returncode == 0:
                    deps_ok = True
                    break
            if not deps_ok:
                self.hub.log("[转换] 依赖安装失败,中止。可手动: python3 -m pip install gguf torch transformers sentencepiece")
                on_done(None)
                return
        log_path = os.path.join(src_dir, CONVERT_LOG_NAME)
        open(log_path, "wb").close()
        self._log_pos = 0
        # 每个元素都过 shlex.quote:src_dir 直接来自请求体,
        # 未加引号的拼接是命令注入(payload: `f16; echo X #`)。
        argv = ["python3", script, src_dir, "--outfile", out_file, "--outtype", outtype]
        cmd = " ".join(shlex.quote(a) for a in argv) + \
            " >> %s 2>&1; echo EXITCODE:$? >> %s" % (shlex.quote(log_path), shlex.quote(log_path))
        try:
            subprocess.Popen(["nice", "-n", "10", "bash", "-c", cmd],
                             stdin=subprocess.DEVNULL, start_new_session=True)
        except Exception as ex:
            self.hub.error_ex(ex, "转换进程启动失败")
            on_done(None)
            return
        self.running = True
        self.hub.log("--- safetensors → GGUF(%s)转换已启动,目录: %s ---" % (outtype, src_dir))
        self.hub.log("[转换] 日志: %s" % log_path)
        threading.Thread(target=self._poll, args=(log_path, src_bytes, outtype, out_file, on_done),
                         daemon=True).start()

    def _poll(self, log_path, src_bytes, outtype, out_file, on_done):
        factor = 0.9 if outtype == "f16" else 0.45
        deadline = time.time() + 3 * 3600
        while time.time() < deadline:
            time.sleep(2)
            chunk = ""   # OSError 首轮即发生时也要有定义, 否则下面 `if chunk` 直接炸线程
            try:
                # 按真实字节取尾, 且只消费到最后一个换行:
                # 半行既会被拆成两条, 文本模式 tell() 的不透明记账与
                # 下一轮 seek 的字节位也可能错位(Windows 版同型 bug 的 Linux 修复)
                with open(log_path, "rb") as f:
                    f.seek(self._log_pos)
                    raw = f.read()
                    nl = raw.rfind(b"\n")
                    if nl < 0:
                        raw = b""
                    else:
                        raw = raw[:nl + 1]
                        self._log_pos += nl + 1
                chunk = raw.decode("utf-8", "replace")
                for ln in chunk.splitlines():
                    if ln.strip():
                        self.hub.log("[转换] " + ln.strip()[:200])
            except OSError:
                pass
            if chunk and "EXITCODE:0" in chunk:
                self.running = False
                header_ok = os.path.exists(out_file) and open(out_file, "rb").read(4) == b"GGUF"
                if header_ok and os.path.getsize(out_file) >= src_bytes * factor:
                    self.hub.log("[转换] 完成: %s" % out_file)
                    on_done(out_file)
                else:
                    self.hub.log("[转换] 退出码 0 但产物无效,中止")
                    on_done(None)
                return
            if chunk and "EXITCODE:" in chunk:
                self.running = False
                self.hub.log("[转换] 转换脚本报错,中止(详见日志)")
                on_done(None)
                return
        self.running = False
        self.hub.log("[转换] 超时(3 小时)未完成,中止")
        on_done(None)
