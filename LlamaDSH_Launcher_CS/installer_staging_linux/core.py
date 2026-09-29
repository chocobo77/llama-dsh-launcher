# -*- coding: utf-8 -*-
"""启动器核心运行时:日志管线、Llama/DSH 进程运行器、监控循环、显存熔断、
safetensors 转换、DSH 重建。等价原版 MainWindow.xaml.cs 的运行部分。
"""
import os
import re
import glob
import json
import time
import shutil
import threading
import subprocess
import collections

import applog
import cmdline
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

_RE_PROMPT_TPS = re.compile(r"prompt eval time\s+=\s*([\d.]+)\s*ms\s*/\s*(\d+)\s*tokens")
_RE_EVAL_TPS = re.compile(r"(?<!prompt )eval time\s+=\s*[\d.]+\s*ms\s*/\s*\d+\s*(?:new tokens|tokens|runs)\s*\([^)]*?([\d.]+)\s*(?:tokens? per second|tok/s)")
_RE_PROMPT_MS = re.compile(r"prompt eval time\s+=\s*([\d.]+)\s*ms")
_RE_EVAL_RAW = re.compile(r"(?<!prompt )eval time\s+=\s*([\d.]+)\s*ms\s*/\s*(\d+)")

# IsDshNoise:错误/诊断行一律保留,其余普通行视为噪声(批次末尾汇总隐藏数)
_DSH_NOISE_FORCE_KEEP = ("at ", "^", "node:internal", "file:///", "imported from",
                         "cannot find", "module", "require", "resolve", "throw")
_DSH_NOISE_KEEP_IF = ("dsh web", "http", "127.0.0.1", "localhost", "listen", "port",
                      "eaddrinuse", "error", "fail", "exception", "warn", "started",
                      "ready", "loaded", "plugin", "exit", "browser", "open", "server",
                      "version", "status", "web")


def is_dsh_noise(content):
    if not content or not content.strip():
        return False
    trimmed = content.strip()
    low = trimmed.lower()
    if any(k in low for k in _DSH_NOISE_FORCE_KEEP):
        return False
    if any(k in low for k in _DSH_NOISE_KEEP_IF):
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
        # [dsh] 噪声行合并为一条汇总(等价原版 FlushLogs 的批次过滤)
        if text.startswith("[dsh] ") and is_dsh_noise(text[len("[dsh] "):]):
            self._dsh_hidden += 1
            return
        if self._dsh_hidden:
            self._emit("[dsh] ……(已隐藏 %d 行非日志内容)" % self._dsh_hidden)
            self._dsh_hidden = 0
        self._emit(text)
        self.run.append("[%s] %s" % (ts, text))
        applog.run("[%s] %s" % (ts, text))

    def error_ex(self, ex, ctx):
        applog.error_ex(ex, ctx)
        import traceback
        tb = traceback.format_exc().splitlines()[:3]
        self.log("[ERROR] %s: %s: %s" % (ctx, type(ex).__name__, ex))
        for ln in tb:
            self.log("    " + ln)

    def since(self, after, max_items=400):
        """返回 (lines, new_after):seq > after 的条目,最多 max_items 条。"""
        out = [it for it in self.ring if it[0] > after]
        if len(out) > max_items:
            out = out[-max_items:]
        return out, (out[-1][0] if out else after)


# ================================================================ Llama ====

class LlamaRunner:
    def __init__(self, hub, schema_holder):
        self.hub = hub
        self.schema = schema_holder
        self.proc = None
        self.status = "stopped"  # stopped/starting/running/failed/crashed/taken_over
        self.exit_code = None
        self.port = 1234
        self._tps = collections.deque(maxlen=20)
        self._ttft = collections.deque(maxlen=20)
        self._pf = collections.deque(maxlen=20)
        self._perf_buf = ""
        self._pump_threads = []
        self._metrics_last = {}
        self.last_cmd = ""

    @property
    def running(self):
        return self.proc is not None and self.proc.poll() is None

    # ---- 启动前置检查(供 API 的确认链) ----
    def precheck(self, profile):
        """返回 need_confirm dict 或 None;与原版 StartLlama 弹窗顺序一致。"""
        defs = self.schema["defs"]
        st = cmdline.ParamState(profile, defs)
        st_dir = cmdline.detect_safetensors_dir(profile.get("ModelPath"))
        if st_dir:
            return {"kind": "safetensors", "dir": st_dir,
                    "message": ("检测到 safetensors 模型分片,llama.cpp 无法直接加载。\n\n"
                                "是否自动转换为 GGUF?(大模型耗时较长,30B 级约 10~40 分钟)\n\n"
                                "F16(精度优先,磁盘≈源文件大小) / Q8_0(磁盘约省一半) / 取消")}

        try:
            lst, violations = cmdline.build_cmd(profile, defs)
        except Exception as ex:
            return {"kind": "build_error", "message": "Build failed:%s" % ex}
        self.last_cmd = " ".join(cmdline.qa(x) for x in lst)
        if not lst:
            return {"kind": "empty", "message": "Empty"}
        confirm = {}
        if violations:
            confirm["violations"] = violations
        est = cmdline.estimate_vram_needed(profile, defs, monitor.query_gpu)
        if est and est["avail_bytes"] > 0 and est["est_bytes"] > est["avail_bytes"]:
            confirm["vram"] = est["detail"]
        qw = cmdline.qwen35_partial_offload_warning(profile, defs)
        if qw:
            confirm["qwen35"] = qw
        if confirm:
            return {"kind": "confirm", "cmd": lst, **confirm}
        return {"kind": "ok", "cmd": lst}

    def start(self, profile, cmd_list=None):
        defs = self.schema["defs"]
        if cmd_list is None:
            cmd_list, violations = cmdline.build_cmd(profile, defs)
            if violations:
                return False, "参数依赖检查未通过"
        if self.running:
            return False, "Already running"
        self._vram_breaker_reset()
        self._tps.clear(); self._ttft.clear(); self._pf.clear()
        self._metrics_last = {}
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
        m = re.search(r"--port\s+(\d+)", self.last_cmd)
        self.port = int(m.group(1)) if m else int(cmdline.norm_tok(
            st.role_text("port") or "1234") or 1234)
        self._pump(cmd_list)
        threading.Thread(target=self._verify_after, args=(3.0,), daemon=True).start()
        self.hub.log("--- 正在启动 Llama ---\n" + cmdline.fmt_cmd(cmd_list))
        state.save_state({"LlamaPid": p.pid})
        return True, None

    def _pump(self, cmd_list):
        p = self.proc
        for stream, tag in ((p.stdout, "[llama] "), (p.stderr, "[llama] ")):
            t = threading.Thread(target=self._pump_line, args=(stream, tag), daemon=True)
            t.start()
            self._pump_threads.append(t)
        threading.Thread(target=self._wait_exit, daemon=True).start()

    def _pump_line(self, stream, tag):
        try:
            for line in stream:
                line = line.rstrip("\r\n")
                if not line:
                    continue
                self.hub.log(tag + line)
                self._parse_timings(line)
        except Exception:
            pass

    def _wait_exit(self):
        code = self.proc.wait()
        self.exit_code = code
        self.status = "crashed" if code != 0 else "stopped"
        if code != 0:
            self.hub.log("--- Llama 异常退出 (退出码 %s) --- 请查看上方日志,通常是显存不足或参数错误导致 ---" % code)
            self.status = "crashed"
        else:
            self.hub.log("--- Llama 已退出 ---")
            self.status = "stopped"
        self.proc = None
        state.clear_state_for("LlamaPid")

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

    def stop(self):
        if self.proc is None:
            return
        self.hub.log("--- 正在停止 Llama ---")
        procman.kill_process_tree(self.proc.pid)
        self.proc = None
        self.status = "stopped"
        self.hub.log("--- 已停止 ---")
        state.clear_state_for("LlamaPid")

    # ---- 性能解析(Prompt/首Token/Prefill/TPS) ----
    def _parse_timings(self, line):
        self._perf_buf = (self._perf_buf + "\n" + line)[-4000:] if self._perf_buf else line
        for m in _RE_PROMPT_TPS.finditer(self._perf_buf):
            ms, toks = float(m.group(1)), int(m.group(2))
            if ms > 0:
                self._ttft.append(ms)
                self._pf.append(toks / (ms / 1000.0))
        for m in _RE_EVAL_TPS.finditer(self._perf_buf):
            self._tps.append(float(m.group(1)))
        if self._perf_buf.count("\n") > 8:
            self._perf_buf = self._perf_buf.split("\n", 4)[-1]

    def perf_snapshot(self):
        def avg(dq):
            return round(sum(dq) / len(dq), 1) if dq else None
        return {"tps": avg(self._tps), "ttft": avg(self._ttft), "prefill": avg(self._pf)}

    def _metrics_fallback(self):
        """日志无样本时从 /metrics 计数差值估算(原版同款兜底)。"""
        if not self.running:
            self._metrics_last = {}
            return {}
        try:
            import urllib.request
            req = urllib.request.Request("http://127.0.0.1:%d/metrics" % self.port)
            body = urllib.request.urlopen(req, timeout=2).read().decode("utf-8", "replace")
        except Exception:
            return {}
        vals = {}
        for key in ("llamacpp:prompt_tokens_total", "llamacpp:prompt_seconds_total",
                    "llamacpp:tokens_predicted_total", "llamacpp:tokens_predicted_seconds_total"):
            m = re.search(r"^%s\s+([\d.]+)" % re.escape(key), body, re.M)
            if m:
                vals[key] = float(m.group(1))
        prev = self._metrics_last
        out = {}
        if prev:
            dt_tok = vals.get("llamacpp:tokens_predicted_total", 0) - prev.get("llamacpp:tokens_predicted_total", 0)
            dt_sec = vals.get("llamacpp:tokens_predicted_seconds_total", 0) - prev.get("llamacpp:tokens_predicted_seconds_total", 0)
            pf_tok = vals.get("llamacpp:prompt_tokens_total", 0) - prev.get("llamacpp:prompt_tokens_total", 0)
            pf_sec = vals.get("llamacpp:prompt_seconds_total", 0) - prev.get("llamacpp:prompt_seconds_total", 0)
            if dt_sec > 0 and dt_tok >= 0:
                out["tps"] = round(dt_tok / dt_sec, 1)
            if pf_sec > 0 and pf_tok >= 0:
                out["prefill"] = round(pf_tok / pf_sec, 1)
        self._metrics_last = vals
        return out

    def _vram_breaker_reset(self):
        self._breaker = False


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
        self.rebuild_thread = None
        self.retries_left = 0
        self.last_profile = {}
        self._tailer_fh = None

    @property
    def running(self):
        if self.tracker is not None:
            return self.tracker.poll() is None
        return self.proc is not None and self.proc.poll() is None

    # ------------------------------------------------------------ 锁/基线 --
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
        """返回 'compiled-js'/'compiled-tsx'/'npx'。"""
        if (profile.get("DshMode") or 0) == 1:
            return "npx"
        dsh_dir = (profile.get("DshDir") or "").strip().strip('"') or "deepseek-harness"
        if os.path.isfile(os.path.join(dsh_dir, "apps", "cli", "lib", "bin.js")) and self.retries_left > 0:
            return "compiled-js"
        return "compiled-tsx"

    # ------------------------------------------------------------ 启动 ------
    def start(self, profile):
        if self.running:
            return False, "Already running"
        self.last_profile = profile
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

    def _start_inner(self, profile):
        mode = self.resolve_mode(profile)
        dsh_dir = (profile.get("DshDir") or "").strip().strip('"') or "deepseek-harness"
        self.proc = None
        self.tracker = None
        self.status = "starting"
        self.verify_stop.clear()
        self._deadline = time.time() + 30.0

        if profile.get("DshAdminMode"):
            # Linux 版管理员模式 = sudo -n(非交互);失败不回退普通启动(与原版原则一致)
            inner = {"compiled-js": "node apps/cli/lib/bin.js web",
                     "compiled-tsx": "pnpm dsh web",
                     "npx": "npx --yes @deepseek-ai/dsh web"}[mode]
            if not procman.which("sudo"):
                self.hub.log("[DSH] 管理员模式需要 sudo,当前环境不可用 → 启动失败(不回退普通启动)")
                self.status = "failed"
                return False, "sudo 不可用"
            script = "cd \"%s\" && exec %s" % (dsh_dir, inner)
            self.hub.log("--- 正在启动 DSH (%s · sudo 提权) ---" % mode)
            ok = self._spawn(["sudo", "-n", "bash", "-c", script], detached=True)
            if not ok:
                self.hub.log("[DSH] sudo 提权启动失败(可能需要密码),不回退普通启动")
                self.status = "failed"
                state.clear_state_for("DshPid")
                return False, "sudo 启动失败"
            self.admin_mode = True
        else:
            self.admin_mode = False
            if mode == "compiled-js":
                script = "cd \"%s\" && exec node apps/cli/lib/bin.js web" % dsh_dir
            elif mode == "compiled-tsx":
                if os.path.isfile(os.path.join(dsh_dir, "apps", "cli", "lib", "bin.js")):
                    self.hub.log("--- 编译版启动失败,本次回退到 pnpm dsh web ---")
                script = "cd \"%s\" && exec pnpm dsh web" % dsh_dir
            else:
                script = "exec npx --yes @deepseek-ai/dsh web"
            self.hub.log("--- 正在启动 DSH (%s · 分离模式) ---" % mode)
            if not self._spawn(["bash", "-c", script], detached=True):
                self.status = "failed"
                return False, "启动失败"
        self._start_tailer()
        threading.Thread(target=self._verify_loop, daemon=True).start()
        return True, None

    def _spawn(self, argv, detached=True):
        try:
            fh = open(DSH_BOOT_LOG, "ab")
            kwargs = {}
            if detached:
                kwargs["start_new_session"] = True
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
                    if size < pos:  # 日志被轮转/清空
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
                state.save_state({"DshPid": pid, "DshDir": self.last_profile.get("DshDir", "")})
                self.hub.log("DSH real PID:%d" % pid)
                return
            if fast_fail or time.time() >= self._deadline:
                self.verify_stop.set()
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
            state.save_state({"DshPid": pid})

    def stop(self):
        self.verify_stop.set()
        self._stop_tailer()
        pid = self.tracker.pid if self.tracker else (self.proc.pid if self.proc else None)
        if pid:
            self.hub.log("--- 正在停止 DSH ---")
            procman.kill_process_tree(pid)
            if procman.alive(pid):
                if procman.which("sudo"):
                    try:
                        subprocess.Popen(["sudo", "-n", "kill", "-9", str(pid)],
                                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                        self.hub.log("--- 提权 DSH 进程普通停止无效,已下发 sudo kill ---")
                    except OSError:
                        self.hub.log("[DSH] 无法停止进程 PID %d,请手动结束" % pid)
                else:
                    self.hub.log("[DSH] 进程 PID %d 仍存活,请手动结束" % pid)
        self.tracker = None
        self.proc = None
        self.status = "stopped"
        self.hub.log("--- 已停止 DSH ---")
        state.clear_state_for("DshPid")

    # ------------------------------------------------------------ 重建 ------
    def rebuild(self, profile, on_done=None):
        """三步重建:pnpm clean → build:lib → vite build(原版等价)。返回 (ok, msg)。"""
        dsh_dir = (profile.get("DshDir") or "").strip().strip('"')
        if not dsh_dir or not os.path.isdir(os.path.join(dsh_dir, "apps", "cli")):
            return False, "无效的 DSH 目录(缺少 apps/cli): %s" % dsh_dir
        head = self.git_head(dsh_dir)
        if not head:
            return False, "无法读取 git HEAD(不是有效的 git 仓库)"
        if self.running:
            self.stop()
            for _ in range(10):  # 等待 ≤20s
                if not self.running:
                    break
                time.sleep(2)
            if self.running:
                return False, "DSH 仍在运行,中止重建"
        script = "\n".join([
            "#!/usr/bin/env bash",
            "cd \"%s\" || exit 1" % dsh_dir,
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
    def __init__(self, hub, llama: LlamaRunner, dsh: DshRunner):
        self.hub = hub
        self.llama = llama
        self.dsh = dsh
        self.active_profile = {}
        self.breaker_triggered = False
        self.snapshot = {}

    def loop(self):
        while True:
            try:
                cm = monitor.cpu_mem_usage()
                g = monitor.query_gpu()
                llama_alive = self.llama.running
                if not llama_alive:
                    self.breaker_triggered = False
                if llama_alive and not self.llama._tps:
                    met = self.llama._metrics_fallback()
                    for k, v in met.items():
                        dq = {"tps": self.llama._tps, "prefill": self.llama._pf}.get(k)
                        if dq is not None:
                            dq.append(v)
                perf = self.llama.perf_snapshot()
                snap = {
                    "cpu": cm["cpu"], "mem_pct": cm["mem_pct"],
                    "mem_used": cm["mem_used"], "mem_total": cm["mem_total"],
                    "gpu_util": g.get("gpu_util"), "gpu_temp": g.get("gpu_temp"),
                    "gpu_temp_src": g.get("gpu_temp_src"),
                    "vram_used": g.get("vram_used"), "vram_total": g.get("vram_total"),
                    **perf,
                }
                # 显存熔断
                brk = self.active_profile or {}
                if (brk.get("VramBreakerEnabled") and llama_alive
                        and g.get("vram_total", 0) > 0 and not self.breaker_triggered):
                    used, total = g.get("vram_used", 0), g.get("vram_total")
                    val = brk.get("VramBreakerValue", 90.0)
                    hit = ((brk.get("VramBreakerType", 0) == 0 and used / total * 100.0 >= val)
                           or (brk.get("VramBreakerType", 0) == 1 and used / (1 << 30) >= val))
                    if hit:
                        self.breaker_triggered = True
                        self.hub.log("[熔断] 显存使用超限,自动停止 Llama 服务")
                        self.llama.stop()
                snap["breaker_pct"] = round(100.0 * g.get("vram_used", 0) / g["vram_total"], 1) if g.get("vram_total") else 0.0
                snap["breaker_triggered"] = self.breaker_triggered
                self.snapshot = snap
            except Exception as ex:
                self.hub.log("[Perf] Error: %s" % ex)
            time.sleep(2.0)


# ====================================================== safetensors 转换 ====

class Converter:
    """safetensors → GGUF 分离转换(Linux:python convert_hf_to_gguf.py,输出走日志文件)。"""

    def __init__(self, hub):
        self.hub = hub
        self.running = False
        self._log_pos = 0

    def find_convert_script(self):
        cands = [
            os.path.join(TOOLS_DIR, "convert_hf_to_gguf.py"),
        ]
        # llama.cpp 仓库自带脚本
        for root in (os.path.join(os.path.expanduser("~"), "llama.cpp"), "/opt/llama.cpp", "/usr/local/share/llama.cpp"):
            cands.append(os.path.join(root, "convert_hf_to_gguf.py"))
        for c in cands:
            if os.path.isfile(c):
                return c
        return None

    def start(self, src_dir, outtype, on_done):
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
        # 依赖检查:gguf/torch/transformers
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
        cmd = "python3 \"%s\" \"%s\" --outfile \"%s\" --outtype %s >> \"%s\" 2>&1; echo EXITCODE:$? >> \"%s\"" % (
            script, src_dir, out_file, outtype, log_path, log_path)
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
            try:
                with open(log_path, "r", encoding="utf-8", errors="replace") as f:
                    f.seek(self._log_pos)
                    chunk = f.read()
                    self._log_pos = f.tell()
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
