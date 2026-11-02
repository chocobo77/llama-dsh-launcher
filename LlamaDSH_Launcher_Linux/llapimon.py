# -*- coding: utf-8 -*-
"""LlamaApiMonitor — llama-lens LogPoller 状态机的 Python 移植(CS LlamaApiMonitor.cs)。

两半:
- HTTP 半: 快轮询 /slots(端口自动发现链)、慢轮询 /props + /v1/models(30s 节流),
  每槽差分算速;失败 3 次判离线;发现失败节流 10s。
- 日志半: feed_log_line 状态机,13 条正则(llama-lens log_poller 同集),
  相位机 + 任务落账(LastTaskInfo)+ MTP 接受率(0-1 原始,显示/历史 ×100)。

线程模型: 采集线程调 poll_fast/poll_slow;日志行从 stdout 泵线程喂入;
一把锁保护共享状态;端口只在 UI/配置侧解析一次后写线程安全缓存。
"""
import json
import re
import threading
import time
import urllib.request

HIST_MAX = 60
EVENTS_MAX = 200

# ------------------------------------------------------------ 日志正则集 --

RE_DECODE = re.compile(r"slot\s+print_timing:\s+id\s+(\d+)\s*\|\s*task\s+(\d+)\s*\|\s*n_decoded\s*=\s*(\d+),\s*tg\s*=\s*([\d.]+)\s*t/s,\s*tg_3s\s*=\s*([\d.]+)\s*t/s")
RE_PROMPT = re.compile(r"slot\s+print_timing:\s+id\s+(\d+)\s*\|\s*task\s+(\d+)\s*\|\s*prompt processing,\s*n_tokens\s*=\s*(\d+),\s*progress\s*=\s*([\d.]+),\s*t\s*=\s*([\d.]+)\s*s\s*/\s*([\d.]+)\s*tokens per second")
RE_PROMPT_SUM = re.compile(r"prompt eval time\s*=\s*([\d.]+)\s*ms\s*/\s*(\d+)\s*tokens\s*\(\s*([\d.]+)\s*ms per token,\s*([\d.]+)\s*tokens per second\)")
RE_EVAL_SUM = re.compile(r"(?<!prompt )eval time\s*=\s*([\d.]+)\s*ms\s*/\s*(\d+)\s*tokens\s*\(\s*([\d.]+)\s*ms per token,\s*([\d.]+)\s*tokens per second\)")
RE_TOTAL_SUM = re.compile(r"total time\s*=\s*([\d.]+)\s*ms\s*/\s*(\d+)\s*tokens")
RE_MTP = re.compile(r"draft acceptance\s*=\s*([\d.]+)\s*\(\s*(\d+) accepted\s*/\s*(\d+) generated\),\s*mean len\s*=\s*([\d.]+)")
RE_GRAPHS = re.compile(r"graphs reused\s*=\s*(\d+)")
RE_RELEASE = re.compile(r"slot\s+release:\s+id\s+(\d+)\s*\|\s*task\s+(\d+)\s*\|\s*stop processing:\s*n_tokens\s*=\s*(\d+),\s*truncated\s*=\s*(\d+)")
RE_LAUNCH = re.compile(r"slot\s+launch_slot_:\s+id\s+(\d+)\s*\|\s*task\s+(\d+)\s*\|\s*processing task,\s*is_child\s*=\s*(\d+)")
RE_BOOT_MODEL = re.compile(r"loading model\s+'(.+?)'")
RE_BOOT_SLOTS = re.compile(r"n_slots\s*=\s*(\d+),\s*n_ctx_slot\s*=\s*(\d+),\s*kv_unified\s*=\s*'(\w+)'")
RE_BOOT_LISTEN = re.compile(r"listening on\s+(\S+)")

# OOM/降层检测(core.py NoteLlamaMemoryLine 语义,放一起便于复用)
RE_OFFLOADED = re.compile(r"offloaded\s+(\d+)/(\d+)\s+layers")
OOM_HINTS = ("failed to fit params", "out of memory", "failed to allocate")


class Hist:
    """60 点环形序列,(值|None, 时间戳) 双列;时间戳承载真实采样节奏。"""

    def __init__(self):
        self.values = []
        self.stamps = []

    def push(self, v):
        now = time.time()
        self.values.append(v)
        self.stamps.append(now)
        if len(self.values) > HIST_MAX:
            self.values.pop(0)
            self.stamps.pop(0)

    def clear(self):
        self.values = []
        self.stamps = []

    def last(self):
        for v in reversed(self.values):
            if v is not None:
                return v
        return None

    def snapshot(self):
        return list(self.values), list(self.stamps)


def fmt_tps(t):
    """FT(t): <10 → 一位小数; ≥10 → 整数; 负值截 0。"""
    if t is None:
        return None
    t = max(0.0, t)
    return round(t, 1) if t < 10 else round(t)


def fmt_eta(sec):
    if sec is None or sec < 0:
        return "—"
    sec = int(sec)
    if sec < 60:
        return "%ds" % sec
    if sec < 3600:
        return "%dm%02ds" % (sec // 60, sec % 60)
    return "%dh%02dm" % (sec // 3600, (sec % 3600) // 60)


def fmt_tok(n):
    if n is None:
        return "—"
    if n >= 1e9:
        return "%.1fB" % (n / 1e9)
    if n >= 1e6:
        return "%.1fM" % (n / 1e6)
    if n >= 1e3:
        return "%.1fK" % (n / 1e3)
    return str(int(n))


class LlamaApiMonitor:
    def __init__(self, port_getter):
        self._port_getter = port_getter   # () -> int;只读线程安全缓存,绝不触碰 UI
        self._lock = threading.Lock()
        self._active_port = 0
        self._fail_count = 0
        self._discover_next_try = 0.0
        self._last_slow_poll = 0.0
        self._slow_busy = False
        self.online = False

        # /slots 聚合
        self.slots = []
        self.gen_tps = 0.0
        self.prompt_tps = 0.0
        self.speed_source = ""
        self.ctx_used = -1
        self.ctx_total = 0

        # 日志状态机
        self.phase = "idle"           # idle / decoding / prompt_processing
        self.log_task_id = None
        self.log_n_decoded = None
        self.tg_tps = None            # 任务均速
        self.tg3s_tps = None          # 3s 滑窗
        self.log_prompt_speed = None
        self.prompt_progress = None
        self.prompt_total_tokens = None
        self.prompt_elapsed_s = None
        self.mtp_acceptance = None    # 0-1
        self.mtp_accepted = None
        self.mtp_generated = None
        self.mtp_mean_len = None
        self.graphs_reused = None
        self.log_ctx_used = None
        self.log_ctx_truncated = None
        self.task_started_at = None
        self.is_child = False
        self.last_task = None
        self.last_prefill_speed = None
        self.last_prefill_tokens = None
        self.boot = {"model": "", "n_slots": 0, "n_ctx_slot": 0, "kv_unified": "", "listening": ""}

        # 任务累加器(乱序累积: 汇总行可能先于/后于 launch)
        self._acc = {}

        self.events = []
        self.total_events = 0
        # 每槽差分状态
        self._slot_states = {}
        # /props + /v1/models 合成的模型信息
        self._model = {}

    # ---------------------------------------------------------- 事件流 --
    def add_event(self, level, msg):
        with self._lock:
            self.events.append({"ts": time.strftime("%H:%M:%S"), "level": level, "msg": msg})
            if len(self.events) > EVENTS_MAX:
                self.events.pop(0)
            self.total_events += 1

    def events_snapshot(self):
        with self._lock:
            return list(self.events), self.total_events

    # ---------------------------------------------------------- HTTP 半 --
    def _get_json(self, port, path, timeout=2.0):
        try:
            req = urllib.request.Request("http://127.0.0.1:%d%s" % (port, path),
                                         headers={"User-Agent": "LlamaDSH-Launcher"})
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return json.loads(r.read().decode("utf-8", "replace"))
        except Exception:
            return None

    def poll_fast(self, llama_alive=True):
        """快轮询 /slots。llama 进程不在时跳过采集但保持状态。"""
        now = time.monotonic()
        allow_discover = now >= self._discover_next_try
        ports = []
        for p in (self._port_getter(), self._active_port):
            if p and 0 < p < 65536 and p not in ports:
                ports.append(p)
        if allow_discover:
            for p in (8080, 8081, 1234):
                if p not in ports:
                    ports.append(p)
        tried = []
        for p in ports:
            tried.append(p)
            data = self._get_json(p, "/slots")
            if data is None:
                continue
            slots = self._extract_slots(data)
            if slots is None and data is not None:
                # 解析失败但端口通: 不计失败
                self._mark_online(p)
                return
            self._mark_online(p)
            self._process_slots(slots, time.monotonic())
            return
        # 全部失败
        if allow_discover:
            self._discover_next_try = time.monotonic() + 10.0
        self._fail_count += 1
        if self._fail_count >= 3 and self.online:
            self._mark_offline(tried)

    def _mark_online(self, port):
        self._active_port = port
        self._fail_count = 0
        if not self.online:
            self.online = True
            self.add_event("ok", "llama-server 在线(端口 %d)" % port)

    def _mark_offline(self, tried):
        self.online = False
        self.gen_tps = self.prompt_tps = 0.0
        with self._lock:
            for s in self.slots:
                s["gen_speed"] = s["prompt_speed"] = 0.0
        self.phase = "idle"
        self.tg_tps = self.tg3s_tps = None
        self.log_prompt_speed = self.prompt_progress = None
        self.add_event("error", "llama-server 离线(连续 3 次采集失败, 尝试过端口 %s)" %
                       "/".join(str(p) for p in tried))

    @staticmethod
    def _extract_slots(data):
        """/slots 三种形态: 根数组 / {"slots": [...]} / {"default": {...}}。"""
        try:
            if isinstance(data, list):
                return data
            if isinstance(data, dict):
                if isinstance(data.get("slots"), list):
                    return data["slots"]
                if isinstance(data.get("default"), dict):
                    return [data["default"]]
        except Exception:
            pass
        return None

    @staticmethod
    def _slot_field(s, *names, default=0):
        for n in names:
            if n in s:
                return s[n]
        return default

    def _process_slots(self, slots, now):
        out = []
        ctx_used_max = -1
        ctx_total_max = 0
        gen_max = 0.0
        prompt_max = 0.0
        any_speed = False
        for s in slots:
            if not isinstance(s, dict):
                continue
            sid = self._slot_field(s, "id", default=0)
            st = self._slot_states.setdefault(sid, {})
            processing = bool(s.get("is_processing"))
            task_id = s.get("id_task")
            n_decoded = self._slot_field(s, "n_decoded", default=None)
            if n_decoded is None:
                nt = s.get("next_token")
                if isinstance(nt, list) and nt and isinstance(nt[0], dict):
                    n_decoded = nt[0].get("n_decoded")
            if n_decoded is None:
                n_decoded = 0
            n_prompt = self._slot_field(s, "n_prompt_tokens", default=0)
            n_processed = self._slot_field(s, "n_prompt_tokens_processed", default=0)
            n_cache = self._slot_field(s, "n_prompt_tokens_cache", default=0)
            n_remain = self._slot_field(s, "n_remain", default=0)
            n_ctx = self._slot_field(s, "n_ctx", default=0)

            dt = now - st["prev_ts"] if st.get("prev_ts") else 0.0
            if processing:
                if st.get("prev_task_id") is None:
                    st["task_start_ts"] = now
                    self.add_event("info", "任务 #%s 开始 · prompt %d tok" % (task_id, n_prompt))
                if task_id != st.get("prev_task_id") or (n_decoded or 0) < (st.get("prev_decoded") or 0):
                    # 新任务或 n_decoded 回绕 → 复位差分基线,速度归零
                    st["prev_task_id"] = task_id
                    st["prev_decoded"] = n_decoded
                    st["prev_prompt_processed"] = n_processed
                    st["gen_speed"] = st["prompt_speed"] = 0.0
                elif dt > 0:
                    st["gen_speed"] = max(0.0, ((n_decoded or 0) - (st.get("prev_decoded") or 0)) / dt)
                    st["prompt_speed"] = max(0.0, (n_processed - (st.get("prev_prompt_processed") or 0)) / dt)
                    st["prev_decoded"] = n_decoded
                st["prev_prompt_processed"] = n_processed
            else:
                st["prev_task_id"] = None
                st["task_start_ts"] = None
                st["gen_speed"] = st["prompt_speed"] = 0.0
            st["prev_ts"] = now

            has_decoded = "n_decoded" in s
            ctx_used = (n_prompt + (n_decoded or 0)) if has_decoded else n_prompt
            ctx_pct = round(ctx_used * 100.0 / n_ctx, 2) if n_ctx > 0 else None
            if ctx_used >= 0 and ctx_used > ctx_used_max:
                ctx_used_max = ctx_used
            if n_ctx > ctx_total_max:
                ctx_total_max = n_ctx
            gs = st.get("gen_speed", 0.0)
            ps = st.get("prompt_speed", 0.0)
            if processing and gs > gen_max:
                gen_max = gs
            if processing and ps > prompt_max:
                prompt_max = ps
            if gs > 0 or ps > 0:
                any_speed = True
            speculative = False
            params = s.get("params")
            if isinstance(params, dict):
                spec = params.get("speculative")
                if isinstance(spec, dict) and isinstance(spec.get("types"), list) and spec["types"]:
                    speculative = True
            out.append({
                "id": sid, "processing": processing, "task_id": task_id,
                "n_ctx": n_ctx, "n_prompt_tokens": n_prompt,
                "n_prompt_processed": n_processed, "n_prompt_cache": n_cache,
                "n_decoded": n_decoded, "n_remain": n_remain,
                "ctx_used": ctx_used, "ctx_pct": ctx_pct,
                "gen_speed": round(gs, 2), "prompt_speed": round(ps, 2),
                "speculative": speculative,
            })
        with self._lock:
            self.slots = out
            self._slot_states = {k: v for k, v in self._slot_states.items()
                                 if k in {s["id"] for s in out}}
        self.gen_tps = round(gen_max, 2)
        self.prompt_tps = round(prompt_max, 2)
        self.speed_source = "api" if any_speed else ""
        if ctx_used_max >= 0:
            self.ctx_used = ctx_used_max
        self.ctx_total = ctx_total_max

    def poll_slow_if_needed(self):
        now = time.monotonic()
        if now - self._last_slow_poll < 30.0 or self._slow_busy:
            return
        self._last_slow_poll = now
        self._slow_busy = True
        try:
            self._poll_slow_core()
        finally:
            self._slow_busy = False

    def _poll_slow_core(self):
        port = self._port_getter()
        if port <= 0:
            return
        props = self._get_json(port, "/props")
        if isinstance(props, dict):
            # 兼容 {props: {...}} 或根对象两种形态
            p = props.get("props") if isinstance(props.get("props"), dict) else props
            model_path = p.get("model_path") or ""
            default_name = ""
            if model_path:
                default_name = model_path.replace("\\", "/").rstrip("/").rsplit("/", 1)[-1]
            alias = p.get("model_alias") or ""
            name = alias or default_name
            modalities = p.get("modalities") or []
            with self._lock:
                old_path = (self._model or {}).get("path") or ""
                self._model = {
                    "path": model_path, "name": name,
                    "n_embd": self._try_long(p.get("n_embd")),
                    "n_vocab": self._try_long(p.get("n_vocab")),
                    "n_ctx": self._try_long(p.get("n_ctx")),
                    "n_ctx_train": self._try_long(p.get("n_ctx_train")),
                    "vocab_type": p.get("vocab_type") or "",
                    "vision": "image" in modalities, "video": "video" in modalities,
                    "audio": "audio" in modalities,
                    "capabilities": list(p.get("capabilities") or []),
                    "n_params": (self._model or {}).get("n_params"),
                    "size": (self._model or {}).get("size"),
                }
            if old_path and model_path and old_path != model_path:
                self.add_event("warn", "模型变更 → %s" % model_path)
        models = self._get_json(port, "/v1/models")
        if isinstance(models, dict) and isinstance(models.get("data"), list) and models["data"]:
            d0 = models["data"][0] or {}
            meta = d0.get("meta") or {}
            n_params = self._try_long(d0.get("n_params"))
            size = self._try_long(d0.get("size"))
            with self._lock:
                m = getattr(self, "_model", None)
                if m is None:
                    self._model = {}
                    m = self._model
                if n_params is not None:
                    m["n_params"] = n_params
                if size is not None:
                    m["size"] = size
                if m.get("n_params") is None and meta.get("n_params") is not None:
                    m["n_params"] = self._try_long(meta.get("n_params"))
                if m.get("size") is None and meta.get("size") is not None:
                    m["size"] = self._try_long(meta.get("size"))

    @staticmethod
    def _try_long(v):
        if isinstance(v, bool):
            return None
        if isinstance(v, int):
            return v
        if isinstance(v, float) and v == int(v):
            return int(v)
        return None

    def model_info(self):
        with self._lock:
            return dict(getattr(self, "_model", None) or {})

    # ---------------------------------------------------------- 合成速度 --
    def speeds(self):
        """日志优先合成: decoding 用 tg_3s,prompt 用日志速度;否则 /slots 差分。"""
        gen, prompt = self.gen_tps, self.prompt_tps
        source = self.speed_source or "api"
        if self.phase == "decoding" and self.tg3s_tps is not None:
            gen = self.tg3s_tps
            source = "log"
        if self.phase == "prompt_processing" and self.log_prompt_speed is not None:
            prompt = self.log_prompt_speed
            source = "log"
        return gen, prompt, source

    def ctx_view(self):
        """上下文占用合并链: /slots 实时 → 日志 release n_tokens。"""
        used = self.ctx_used if self.ctx_used >= 0 else (self.log_ctx_used if self.log_ctx_used is not None else -1)
        total = self.ctx_total if self.ctx_total > 0 else (self.boot.get("n_ctx_slot") or 0)
        return used, total

    # ---------------------------------------------------------- 日志状态机 --
    def feed_log_line(self, line):
        """单行解析,首中即返;绝不抛出。"""
        try:
            self._parse_log_line(line)
        except Exception:
            pass

    def _parse_log_line(self, line):
        m = RE_DECODE.search(line)
        if m:
            self.phase = "decoding"
            self.log_task_id = int(m.group(2))
            self.log_n_decoded = int(m.group(3))
            self.tg_tps = float(m.group(4))
            self.tg3s_tps = float(m.group(5))
            return
        m = RE_PROMPT.search(line)
        if m:
            self.phase = "prompt_processing"
            self.log_task_id = int(m.group(2))
            self.prompt_progress = float(m.group(4))
            self.log_prompt_speed = float(m.group(6))
            self.prompt_total_tokens = int(m.group(3))
            self.prompt_elapsed_s = float(m.group(5))
            self.last_prefill_speed = float(m.group(6))
            self.last_prefill_tokens = int(m.group(3))
            if self._prefill_task != self.log_task_id:
                self._prefill_task = self.log_task_id
                self.add_event("info", "任务 #%d 预填充开始 (prompt %d tokens)" % (self.log_task_id, self.prompt_total_tokens))
            return
        m = RE_RELEASE.search(line)
        if m:
            self._rel = (int(m.group(1)), int(m.group(2)), int(m.group(3)), int(m.group(4)))
            self._on_task_end()
            return
        m = RE_LAUNCH.search(line)
        if m:
            self.log_task_id = int(m.group(2))
            self.is_child = m.group(3) != "0"
            self.task_started_at = time.time()
            self._acc = {}
            self._prefill_task = None
            self.add_event("info", "任务 #%d 开始%s" % (self.log_task_id, "(· 子任务)" if self.is_child else ""))
            return
        m = RE_PROMPT_SUM.search(line)
        if m:
            a = self._acc
            a["prompt_ms"] = float(m.group(1))
            a["prompt_tokens"] = int(m.group(2))
            a["prompt_spd"] = float(m.group(4))
            return
        m = RE_EVAL_SUM.search(line)
        if m:
            a = self._acc
            a["eval_ms"] = float(m.group(1))
            a["decoded_tokens"] = int(m.group(2))
            a["gen_spd"] = float(m.group(4))
            return
        m = RE_TOTAL_SUM.search(line)
        if m:
            a = self._acc
            a["total_ms"] = float(m.group(1))
            a["total_tokens"] = int(m.group(2))
            return
        m = RE_MTP.search(line)
        if m:
            self.mtp_acceptance = float(m.group(1))
            self.mtp_accepted = int(m.group(2))
            self.mtp_generated = int(m.group(3))
            self.mtp_mean_len = float(m.group(4))
            return
        m = RE_GRAPHS.search(line)
        if m:
            self.graphs_reused = int(m.group(1))
            return
        m = RE_BOOT_SLOTS.search(line)
        if m:
            self.boot["n_slots"] = int(m.group(1))
            self.boot["n_ctx_slot"] = int(m.group(2))
            self.boot["kv_unified"] = m.group(3)
            return
        m = RE_BOOT_MODEL.search(line)
        if m:
            self.boot["model"] = m.group(1)
            return
        m = RE_BOOT_LISTEN.search(line)
        if m:
            self.boot["listening"] = m.group(1)
            self.add_event("ok", "llama-server 监听 %s" % m.group(1))

    _prefill_task = None
    _rel = None

    def _on_task_end(self):
        rel_slot, rel_task, rel_tokens, rel_truncated = self._rel
        self.log_ctx_used = rel_tokens
        self.log_ctx_truncated = rel_truncated
        a = self._acc
        interrupted = a.get("total_ms", 0) <= 0 and a.get("eval_ms", 0) <= 0
        dur = (time.time() - self.task_started_at) if self.task_started_at else 0.0

        def g(key, cast=float):
            v = a.get(key)
            return cast(v) if v is not None else None

        t = {
            "slot": rel_slot, "task_id": rel_task,
            "prompt_ms": g("prompt_ms"), "prompt_tokens": g("prompt_tokens", int),
            "prompt_speed": g("prompt_spd"),
            "eval_ms": g("eval_ms"), "decoded_tokens": g("decoded_tokens", int),
            "gen_speed": g("gen_spd"),
            "total_ms": g("total_ms"), "total_tokens": g("total_tokens", int),
            "duration": round(dur, 1),
            "avg_tps": None,
            "truncated": bool(rel_truncated), "end_time": time.time(),
        }
        if dur > 0 and t["decoded_tokens"]:
            t["avg_tps"] = round(t["decoded_tokens"] / dur, 1)
        self.last_task = t
        self.phase = "idle"
        self.log_n_decoded = None
        self.tg_tps = self.tg3s_tps = None
        self.log_prompt_speed = self.prompt_progress = None
        self.task_started_at = None
        self._acc = {}
        out_tok = t["total_tokens"] or t["decoded_tokens"] or 0
        msg = "任务 #%s 完成 · %s tok" % (rel_task, fmt_tok(out_tok))
        if dur > 0:
            msg += " · %.1fs" % dur
        if t["avg_tps"]:
            msg += " · 均 %.1f tok/s" % t["avg_tps"]
        if interrupted:
            msg += " · 已中断"
        if self.mtp_acceptance is not None:
            msg += " · MTP %.1f%%" % (self.mtp_acceptance * 100.0)
        self.add_event("warn" if interrupted else "ok", msg)

    def reset_log_machine(self):
        """llama 启动 / 进程退出两处调用。"""
        self.mtp_acceptance = self.mtp_accepted = self.mtp_generated = self.mtp_mean_len = None
        self.graphs_reused = None
        self.phase = "idle"
        self.log_task_id = None
        self.log_n_decoded = None
        self.tg_tps = self.tg3s_tps = None
        self.log_prompt_speed = self.prompt_progress = None
        self.prompt_total_tokens = self.prompt_elapsed_s = None
        self.task_started_at = None
        self.is_child = False
        self.last_task = None
        self._acc = {}
        self._prefill_task = None
        self._rel = None
        self.log_ctx_used = None
        self.log_ctx_truncated = None
        self.online = False
        self._fail_count = 0
        self._active_port = 0
        with self._lock:
            self._slot_states = {}
            self.slots = []
        self.gen_tps = self.prompt_tps = 0.0
        self.speed_source = ""
        self.boot = {"model": "", "n_slots": 0, "n_ctx_slot": 0, "kv_unified": "", "listening": ""}

    # ------------------------------------------------------------ OOM 检测 --
    def note_memory_line(self, line, state):
        """OOM 降层检测(core.NoteLlamaMemoryLine 语义)。

        state: {"oom_seen": bool, "offloaded": int, "total_layers": int,
                "buffers": {"model": MiB, "kv": MiB, "rs": MiB, "compute": MiB}}
        返回提示文案或 None。
        """
        low = line.lower()
        hint = None
        for h in OOM_HINTS:
            if h in low:
                if not state.get("oom_seen"):
                    hint = "检测到显存不足(建议调小 ubatch 或把更多专家放到 CPU)"
                state["oom_seen"] = True
                break
        m = RE_OFFLOADED.search(line)
        if m:
            state["offloaded"] = int(m.group(1))
            state["total_layers"] = int(m.group(2))
        # buffer 汇总行(排除 CPU/host 侧)
        if " buffer size = " in low and not low.startswith("cpu_") and "_host " not in low and "blas" not in low:
            mm = re.search(r"=\s*([\d.]+)\s*MiB", line)
            if mm:
                mib = float(mm.group(1))
                if " model buffer size " in low:
                    state["buffers"]["model"] = mib
                elif " kv buffer size " in low:
                    state["buffers"]["kv"] = mib
                elif " rs buffer size " in low:
                    state["buffers"]["rs"] = mib
                elif " compute buffer size " in low:
                    state["buffers"]["compute"] = mib
                b = state["buffers"]
                if b.get("model") and b.get("compute"):
                    total = sum(b.get(k, 0) for k in ("model", "kv", "rs", "compute"))
                    hint = "[显存实测] llama 实际分配: 权重 %.2f + KV %.2f + RS %.2f + 计算图 %.2f = %.2f GiB" % (
                        b["model"] / 1024.0, b.get("kv", 0) / 1024.0, b.get("rs", 0) / 1024.0,
                        b["compute"] / 1024.0, total / 1024.0)
        return hint
