# -*- coding: utf-8 -*-
"""LlamaApiMonitor — llama-server 运行态采集器(Python 端)。

2026-11-04 起为本仓库独立实现(clean-room 重写): 仅依据 llama.cpp 公开文档化的
HTTP 端点(/slots /props /v1/models)与其公开 stdout 日志行格式编写, 不参照任何
第三方监控项目的代码结构、命名或算法。

数据契约(core.py 与 webui 直接序列化消费, 改键名需同步 webui/app.js):
- slots: [{id, processing, task_id, n_ctx, n_prompt_tokens, n_prompt_processed,
           n_prompt_cache, n_decoded, n_remain, ctx_used, ctx_pct,
           gen_speed, prompt_speed, speculative}]
- last_task: {slot, task_id, prompt_ms, prompt_tokens, prompt_speed, eval_ms,
              decoded_tokens, gen_speed, total_ms, total_tokens, duration,
              avg_tps, truncated, end_time}
- boot: {model, n_slots, n_ctx_slot, kv_unified, listening}
- model_info(): {path, name, vocab_type, n_embd, n_vocab, n_ctx, n_ctx_train,
                 vision, video, audio, capabilities, n_params, size}

线程模型: poll_fast/poll_slow_if_needed 由性能线程调用; feed_log_line 由 stdout
泵线程调用; slots/events 读侧经 events_snapshot/model_info/锁 保护。
"""
import json
import re
import threading
import time
import urllib.request

HIST_MAX = 60
EVENT_CAP = 200

# llama.cpp 公开日志行格式(与 llama.cpp 服务端输出逐字对应)
_RE_TIMING_GEN = re.compile(
    r"print_timing:\s*id\s+\d+\s*\|\s*task\s+(?P<task>\d+)\s*\|\s*"
    r"n_decoded\s*=\s*(?P<nd>\d+)\s*,\s*tg\s*=\s*(?P<tg>[\d.]+)\s*t/s\s*,\s*tg_3s\s*=\s*(?P<tg3>[\d.]+)")
_RE_TIMING_PF = re.compile(
    r"print_timing:\s*id\s+\d+\s*\|\s*task\s+(?P<task>\d+)\s*\|\s*"
    r"prompt processing\s*,\s*n_tokens\s*=\s*(?P<n>\d+)\s*,\s*progress\s*=\s*(?P<prog>[\d.]+)\s*,\s*"
    r"t\s*=\s*(?P<t>[\d.]+)\s*s\s*/\s*(?P<spd>[\d.]+)\s*tokens per second")
_RE_PF_EVAL = re.compile(
    r"prompt eval time\s*=\s*(?P<ms>[\d.]+)\s*ms\s*/\s*(?P<n>\d+) tokens\s*\("
    r"\s*[\d.]+\s*ms per token\s*,\s*(?P<spd>[\d.]+)\s*tokens per second\s*\)")
_RE_GEN_EVAL = re.compile(
    r"(?<!prompt )eval time\s*=\s*(?P<ms>[\d.]+)\s*ms\s*/\s*(?P<n>\d+) tokens\s*\("
    r"\s*[\d.]+\s*ms per token\s*,\s*(?P<spd>[\d.]+)\s*tokens per second\s*\)")
_RE_TOTAL = re.compile(r"total time\s*=\s*(?P<ms>[\d.]+)\s*ms\s*/\s*(?P<n>\d+) tokens")
_RE_DRAFT = re.compile(
    r"draft acceptance\s*=\s*(?P<acc>[\d.]+)\s*\(\s*(?P<ok>\d+) accepted\s*/\s*(?P<gen>\d+) generated\s*\)"
    r"\s*,\s*mean len\s*=\s*(?P<len>[\d.]+)")
_RE_GRAPHS = re.compile(r"graphs reused\s*=\s*(?P<n>\d+)")
_RE_RELEASE = re.compile(
    r"slot release:\s*id\s+(?P<slot>\d+)\s*\|\s*task\s+(?P<task>\d+)\s*\|\s*"
    r"stop processing\s*:\s*n_tokens\s*=\s*(?P<n>\d+)\s*,\s*truncated\s*=\s*(?P<tr>\d)")
_RE_LAUNCH = re.compile(
    r"launch_slot\w*:\s*id\s+\d+\s*\|\s*task\s+(?P<task>\d+)\s*\|\s*"
    r"processing task\s*,\s*is_child\s*=\s*(?P<child>\d)")
_RE_BOOT_MODEL = re.compile(r"loading model\s+'(?P<path>[^']+)'")
_RE_BOOT_SLOTS = re.compile(
    r"n_slots\s*=\s*(?P<slots>\d+)\s*,\s*n_ctx_slot\s*=\s*(?P<ctx>\d+)\s*,\s*kv_unified\s*=\s*'(?P<kv>\w+)'")
_RE_LISTEN = re.compile(r"listening on\s+(?P<addr>\S+)")

# OOM 关键词 / 显存实测(core.py 共用): offload 与 buffer 汇总行
_RE_OFFLOAD = re.compile(r"offloaded\s+(\d+)/(\d+)\s+layers")
OOM_MARKERS = ("failed to fit params", "out of memory", "failed to allocate")


class Hist:
    """60 点滚动窗口: 值与采样时刻成对保存, 图表按真实节奏渲染。"""

    def __init__(self):
        self.values = []
        self.stamps = []

    def push(self, v):
        self.values.append(v)
        self.stamps.append(time.time())
        if len(self.values) > HIST_MAX:
            del self.values[0]
            del self.stamps[0]

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
    """tps 展示: <10 保留一位小数, 否则取整; 负数按 0。"""
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
        self._port_getter = port_getter      # () -> int, 只读线程安全缓存
        self._lock = threading.Lock()
        # 原先这里还有一把从未被 acquire 的 _http_lock: 死字段 + 与 _lock 形成相似名对,
        # 正是下面那处"锁内调 add_event"误用的诱因(425 行前后), 删掉以免再踩。
        self._active_port = 0
        self._fail_streak = 0
        self._rescan_at = 0.0
        self._slow_at = 0.0
        self._slow_busy = False
        self.online = False

        # /slots 差分聚合
        self.slots = []
        self.gen_tps = 0.0
        self.prompt_tps = 0.0
        self.speed_source = ""
        self.ctx_used = -1
        self.ctx_total = 0

        # 日志状态机(相位/实时)
        self.phase = "idle"
        self.log_task_id = None
        self.log_n_decoded = None
        self.tg_tps = None
        self.tg3s_tps = None
        self.log_prompt_speed = None
        self.prompt_progress = None
        self.prompt_total_tokens = None
        self.prompt_elapsed_s = None
        self.mtp_acceptance = None           # 0-1 原值, 展示侧 ×100
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

        self._diff = {}          # slot id -> 差分状态
        self._acc = {}           # 当前任务汇总行暂存
        self._pf_task = None     # 预填充事件去重
        self._pending_rel = None
        self._model = {}
        self._events = []
        self.total_events = 0

    # ------------------------------------------------------------- 事件流
    def add_event(self, level, msg):
        with self._lock:
            self._events.append({"ts": time.strftime("%H:%M:%S"), "level": level, "msg": msg})
            while len(self._events) > EVENT_CAP:
                del self._events[0]
            self.total_events += 1

    def events_snapshot(self):
        with self._lock:
            return list(self._events), self.total_events

    # ------------------------------------------------------------- HTTP 半
    def _get_json(self, port, path, timeout=2.0):
        try:
            req = urllib.request.Request("http://127.0.0.1:%d%s" % (port, path),
                                         headers={"User-Agent": "LlamaDSH-Launcher"})
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                return json.loads(resp.read().decode("utf-8", "replace"))
        except Exception:
            return None

    def _ports(self, with_defaults):
        out = []
        for p in (self._port_getter(), self._active_port):
            if p and 0 < p < 65536 and p not in out:
                out.append(p)
        if with_defaults:
            for p in (8080, 8081, 1234):   # llama-server 常见默认端口
                if p not in out:
                    out.append(p)
        return out

    def poll_fast(self, llama_alive=True):
        """一拍 /slots 采集 + 逐槽差分; llama 不在时保持状态不动。"""
        if not llama_alive:
            return
        now = time.monotonic()
        rescan = now >= self._rescan_at
        tried = []
        for p in self._ports(rescan):
            tried.append(p)
            data = self._get_json(p, "/slots")
            if data is None:
                continue
            self._mark_online(p)
            rows = self._as_slot_array(data)
            if rows is not None:
                self._diff_slots(rows, time.monotonic())
            return
        if rescan:
            self._rescan_at = time.monotonic() + 10.0
        self._fail_streak += 1
        if self._fail_streak >= 3 and self.online:
            self._mark_offline(tried)

    def _mark_online(self, port):
        self._active_port = port
        self._fail_streak = 0
        if not self.online:
            self.online = True
            self.add_event("ok", "llama-server API 已连通 (端口 %d)" % port)

    def _mark_offline(self, tried):
        self.online = False
        self.gen_tps = self.prompt_tps = 0.0
        with self._lock:
            for s in self.slots:
                s["gen_speed"] = s["prompt_speed"] = 0.0
        self.phase = "idle"
        self.tg_tps = self.tg3s_tps = None
        self.log_prompt_speed = self.prompt_progress = None
        self.add_event("error", "llama-server API 连续采集失败, 判定离线 (探测过 %s)"
                       % "/".join(str(p) for p in tried))

    @staticmethod
    def _as_slot_array(data):
        """兼容 /slots 三种历史形态: 根数组 / {"slots":[...]} / {"default":{...}}。"""
        if isinstance(data, list):
            return data
        if isinstance(data, dict):
            if isinstance(data.get("slots"), list):
                return data["slots"]
            if isinstance(data.get("default"), dict):
                return [data["default"]]
        return None

    @staticmethod
    def _num(slot, key):
        v = slot.get(key)
        if isinstance(v, bool):
            return 0
        if isinstance(v, (int, float)):
            return int(v)
        return 0

    def _diff_slots(self, rows, now):
        fresh = []
        used_max = -1
        ctx_max = 0
        gen_best = 0.0
        pf_best = 0.0
        api_speed = False
        for row in rows:
            if not isinstance(row, dict):
                continue
            sid = self._num(row, "id")
            st = self._diff.setdefault(sid, {})
            busy = bool(row.get("is_processing"))
            task = row.get("id_task")
            decoded = row.get("n_decoded")
            if decoded is None:
                nxt = row.get("next_token")
                if isinstance(nxt, list) and nxt and isinstance(nxt[0], dict):
                    decoded = nxt[0].get("n_decoded")
            decoded = decoded if isinstance(decoded, (int, float)) else 0
            pf = self._num(row, "n_prompt_tokens")
            pf_done = self._num(row, "n_prompt_tokens_processed")
            cache = self._num(row, "n_prompt_tokens_cache")
            remain = self._num(row, "n_remain")
            nctx = self._num(row, "n_ctx")
            gap = now - st["last_at"] if st.get("last_at") else 0.0

            if busy:
                if not st.get("busy"):
                    st["busy"] = True
                    st["task"] = task
                    st["decoded"] = decoded
                    st["pf_done"] = pf_done
                    self.add_event("info", "槽 %d 任务 #%s 开始 (prompt %d tok)" % (sid, task, pf))
                if task != st.get("task") or decoded < st.get("decoded", 0):
                    st["task"] = task
                    st["decoded"] = decoded
                    st["pf_done"] = pf_done
                    st["gen"] = st["pf_spd"] = 0.0
                elif gap > 0:
                    st["gen"] = max(0.0, (decoded - st.get("decoded", 0)) / gap)
                    st["pf_spd"] = max(0.0, (pf_done - st.get("pf_done", 0)) / gap)
                    st["decoded"] = decoded
                    if st["gen"] > 0 or st["pf_spd"] > 0:
                        api_speed = True
                st["pf_done"] = pf_done
            else:
                if st.get("busy"):
                    st["busy"] = False
                    st["task"] = None
                st["gen"] = st["pf_spd"] = 0.0
            st["last_at"] = now

            # 上下文占用: 标准 build 顶层带 n_decoded 且 n_prompt 不含 decoded → 相加;
            # 无 n_decoded 字段的自定义 build 其 n_prompt 已含 decoded → 直接用 n_prompt
            used = pf + decoded if "n_decoded" in row else pf
            params = row.get("params")
            spec = False
            if isinstance(params, dict):
                sp = params.get("speculative")
                if isinstance(sp, dict) and isinstance(sp.get("types"), list) and sp["types"]:
                    spec = True
            fresh.append({
                "id": sid, "processing": busy, "task_id": task,
                "n_ctx": nctx, "n_prompt_tokens": pf,
                "n_prompt_processed": pf_done, "n_prompt_cache": cache,
                "n_decoded": decoded, "n_remain": remain,
                "ctx_used": used,
                "ctx_pct": round(used * 100.0 / nctx, 2) if nctx > 0 else None,
                "gen_speed": round(st.get("gen", 0.0), 2),
                "prompt_speed": round(st.get("pf_spd", 0.0), 2),
                "speculative": spec,
            })
            used_max = max(used_max, used)
            ctx_max = max(ctx_max, nctx)
            if busy:
                gen_best = max(gen_best, st.get("gen", 0.0))
                pf_best = max(pf_best, st.get("pf_spd", 0.0))

        with self._lock:
            self.slots = fresh
            alive = {r["id"] for r in fresh}
            self._diff = {k: v for k, v in self._diff.items() if k in alive}
        self.gen_tps = round(gen_best, 2)
        self.prompt_tps = round(pf_best, 2)
        self.speed_source = "api" if (gen_best > 0 or pf_best > 0) else ""
        if used_max >= 0:
            self.ctx_used = used_max
        self.ctx_total = ctx_max

    def poll_slow_if_needed(self):
        """30s 节流的 /props + /v1/models。慢轮询(最长各 2s 超时)挪到独立线程:
        否则每 30s 有一拍被拉到 ~7s, tps 差分与监测刷新都会抖。"""
        now = time.monotonic()
        if now - self._slow_at < 30.0 or self._slow_busy:
            return
        self._slow_at = now
        self._slow_busy = True

        def run():
            try:
                self._fetch_props_and_models()
            finally:
                self._slow_busy = False
        threading.Thread(target=run, daemon=True).start()

    def _try_long(self, v):
        if isinstance(v, bool):
            return None
        if isinstance(v, int):
            return v
        if isinstance(v, float) and v == int(v):
            return int(v)
        return None

    def _fetch_props_and_models(self):
        ports = self._ports(False)
        if not ports:
            return
        port = ports[0]
        props = None
        for p in ports:
            props = self._get_json(p, "/props")
            if props is not None:
                port = p
                break
        if isinstance(props, dict):
            body = props.get("props") if isinstance(props.get("props"), dict) else props
            path = body.get("model_path") or ""
            leaf = path.replace("\\", "/").rstrip("/").rsplit("/", 1)[-1] if path else ""
            name = (body.get("model_alias") or "") or leaf
            mods = body.get("modalities") or []
            changed_to = ""
            with self._lock:
                prev = self._model.get("path") or ""
                self._model = {
                    "path": path, "name": name,
                    "n_embd": self._try_long(body.get("n_embd")),
                    "n_vocab": self._try_long(body.get("n_vocab")),
                    "n_ctx": self._try_long(body.get("n_ctx")),
                    "n_ctx_train": self._try_long(body.get("n_ctx_train")),
                    "vocab_type": body.get("vocab_type") or "",
                    "vision": "image" in mods, "video": "video" in mods, "audio": "audio" in mods,
                    "capabilities": list(body.get("capabilities") or []),
                    "n_params": self._model.get("n_params"),
                    "size": self._model.get("size"),
                }
                if prev and path and prev != path:
                    changed_to = path
            # 事件必须在锁**外**发: add_event 自己也要拿这把非重入 Lock, 在 with 块里调它
            # 就是当场自死锁 —— 运行中换一次模型, 采集线程永久卡在这里, 且 _lock 一直被
            # 持有, 于是 events_snapshot / 日志泵 add_event 全部阻塞: 监测与 UI 事件流整体
            # 静默瘫痪, 没有任何异常或日志(2026-11-05 review 发现)。
            if changed_to:
                self.add_event("warn", "服务加载的模型路径已变化 → %s" % changed_to)
        models = self._get_json(port, "/v1/models")
        if isinstance(models, dict) and isinstance(models.get("data"), list) and models["data"]:
            d0 = models["data"][0] or {}
            meta = d0.get("meta") or {}
            with self._lock:
                np_ = self._try_long(d0.get("n_params"))
                sz = self._try_long(d0.get("size"))
                if np_ is not None:
                    self._model["n_params"] = np_
                if sz is not None:
                    self._model["size"] = sz
                if self._model.get("n_params") is None:
                    self._model["n_params"] = self._try_long(meta.get("n_params"))
                if self._model.get("size") is None:
                    self._model["size"] = self._try_long(meta.get("size"))

    def model_info(self):
        with self._lock:
            return dict(self._model)

    # ------------------------------------------------------------ 速度合成
    def speeds(self):
        """服务端自报(日志)优先, /slots 差分兜底。"""
        gen, pf = self.gen_tps, self.prompt_tps
        source = self.speed_source or "api"
        if self.phase == "decoding" and self.tg3s_tps is not None:
            gen = self.tg3s_tps
            source = "log"
        if self.phase == "prompt_processing" and self.log_prompt_speed is not None:
            pf = self.log_prompt_speed
            source = "log"
        return gen, pf, source

    def ctx_view(self):
        """上下文占用合并: /slots 实时优先, 日志 release 的 n_tokens 兜底。"""
        used = self.ctx_used if self.ctx_used >= 0 else (
            self.log_ctx_used if self.log_ctx_used is not None else -1)
        total = self.ctx_total if self.ctx_total > 0 else (self.boot.get("n_ctx_slot") or 0)
        return used, total

    # ------------------------------------------------------------- 日志半
    def feed_log_line(self, line):
        try:
            self._consume(line)
        except Exception:
            pass

    def _consume(self, line):
        # 按 llama.cpp 日志固定前缀分派, 每行最多命中一类
        if "print_timing" in line:
            m = _RE_TIMING_GEN.search(line)
            if m:
                self.phase = "decoding"
                self.log_task_id = int(m["task"])
                self.log_n_decoded = int(m["nd"])
                self.tg_tps = float(m["tg"])
                self.tg3s_tps = float(m["tg3"])
                return
            m = _RE_TIMING_PF.search(line)
            if m:
                self.phase = "prompt_processing"
                self.log_task_id = int(m["task"])
                self.prompt_total_tokens = int(m["n"])
                self.prompt_progress = float(m["prog"])
                self.prompt_elapsed_s = float(m["t"])
                self.log_prompt_speed = float(m["spd"])
                self.last_prefill_speed = self.log_prompt_speed
                self.last_prefill_tokens = self.prompt_total_tokens
                if self._pf_task != self.log_task_id:
                    self._pf_task = self.log_task_id
                    self.add_event("info", "任务 #%d 预填充启动 (%d tok)"
                                   % (self.log_task_id, self.prompt_total_tokens))
                return
            # 汇总行(prompt eval time / eval time / total time / draft acceptance /
            # graphs reused)同样以 "slot print_timing:" 打头 —— 两条逐拍正则都不中时
            # 必须落到下面的分派, 直接 return 会把任务落账/MTP/graphs 整段吞掉
        if "launch_slot" in line:
            m = _RE_LAUNCH.search(line)
            if m:
                self.log_task_id = int(m["task"])
                self.is_child = m["child"] != "0"
                self.task_started_at = time.time()
                self._acc = {}
                self._pf_task = None
                self.add_event("info", "任务 #%d 开始%s"
                               % (self.log_task_id, " (子任务)" if self.is_child else ""))
            return
        if "slot release" in line:
            m = _RE_RELEASE.search(line)
            if m:
                self._pending_rel = (int(m["slot"]), int(m["task"]), int(m["n"]), m["tr"] == "1")
                self._close_task()
            return
        if "eval time" in line:
            m = _RE_PF_EVAL.search(line) if "prompt eval time" in line else _RE_GEN_EVAL.search(line)
            if m:
                if "prompt eval time" in line:
                    self._acc["prompt_ms"] = float(m["ms"])
                    self._acc["prompt_tokens"] = int(m["n"])
                    self._acc["prompt_spd"] = float(m["spd"])
                else:
                    self._acc["eval_ms"] = float(m["ms"])
                    self._acc["decoded_tokens"] = int(m["n"])
                    self._acc["gen_spd"] = float(m["spd"])
            return
        if "total time" in line:
            m = _RE_TOTAL.search(line)
            if m:
                self._acc["total_ms"] = float(m["ms"])
                self._acc["total_tokens"] = int(m["n"])
            return
        if "draft acceptance" in line:
            m = _RE_DRAFT.search(line)
            if m:
                self.mtp_acceptance = float(m["acc"])
                self.mtp_accepted = int(m["ok"])
                self.mtp_generated = int(m["gen"])
                self.mtp_mean_len = float(m["len"])
            return
        if "graphs reused" in line:
            m = _RE_GRAPHS.search(line)
            if m:
                self.graphs_reused = int(m["n"])
            return
        if "loading model" in line:
            m = _RE_BOOT_MODEL.search(line)
            if m:
                self.boot["model"] = m["path"]
            return
        if "n_slots" in line:
            m = _RE_BOOT_SLOTS.search(line)
            if m:
                self.boot["n_slots"] = int(m["slots"])
                self.boot["n_ctx_slot"] = int(m["ctx"])
                self.boot["kv_unified"] = m["kv"]
            return
        if "listening on" in line:
            m = _RE_LISTEN.search(line)
            if m:
                self.boot["listening"] = m["addr"]
                self.add_event("ok", "llama-server 已监听 %s" % m["addr"])

    def _close_task(self):
        slot, task, tokens, trunc = self._pending_rel
        self.log_ctx_used = tokens
        self.log_ctx_truncated = trunc
        a = self._acc
        abrupt = a.get("total_ms", 0) <= 0 and a.get("eval_ms", 0) <= 0
        wall = (time.time() - self.task_started_at) if self.task_started_at else 0.0

        def pick(key, cast=None):
            v = a.get(key)
            if v is None:
                return None
            return cast(v) if cast else v

        t = {
            "slot": slot, "task_id": task,
            "prompt_ms": pick("prompt_ms"), "prompt_tokens": pick("prompt_tokens"),
            "prompt_speed": pick("prompt_spd"),
            "eval_ms": pick("eval_ms"), "decoded_tokens": pick("decoded_tokens"),
            "gen_speed": pick("gen_spd"),
            "total_ms": pick("total_ms"), "total_tokens": pick("total_tokens"),
            "duration": round(wall, 1),
            "avg_tps": round(a["decoded_tokens"] / wall, 1) if wall > 0 and a.get("decoded_tokens") else None,
            "truncated": bool(trunc), "end_time": time.time(),
        }
        self.last_task = t
        self.phase = "idle"
        self.log_n_decoded = None
        self.tg_tps = self.tg3s_tps = None
        self.log_prompt_speed = self.prompt_progress = None
        self.task_started_at = None
        self._acc = {}
        out = t["total_tokens"] or t["decoded_tokens"] or 0
        msg = "任务 #%s 结束 · %s tok" % (task, fmt_tok(out))
        if wall > 0:
            msg += " · 历时 %.1fs" % wall
        if t["avg_tps"]:
            msg += " · 平均 %.1f t/s" % t["avg_tps"]
        if abrupt:
            msg += " · 未正常收尾"
        if self.mtp_acceptance is not None:
            msg += " · 草稿接受 %.1f%%" % (self.mtp_acceptance * 100.0)
        self.add_event("warn" if abrupt else "ok", msg)

    def reset_log_machine(self):
        """llama 启动 / 退出两处调用: 清掉上一进程的残影。"""
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
        self._pf_task = None
        self._pending_rel = None
        self.log_ctx_used = None
        self.log_ctx_truncated = None
        self.online = False
        self._fail_streak = 0
        self._active_port = 0
        with self._lock:
            self._diff = {}
            self.slots = []
        self.gen_tps = self.prompt_tps = 0.0
        self.speed_source = ""
        self.boot = {"model": "", "n_slots": 0, "n_ctx_slot": 0, "kv_unified": "", "listening": ""}

    # ------------------------------------------------------------- OOM 检测
    def note_memory_line(self, line, state):
        """显存不足提示 / 实测分配(core.py 调用)。

        state: {"offloaded": int, "total_layers": int,
                "buffers": {"model": MiB, "kv": MiB, "rs": MiB, "compute": MiB}}
        返回给用户看的提示文案, 无提示返回 None。
        """
        low = line.lower()
        hint = None
        for marker in OOM_MARKERS:
            if marker in low:
                hint = "检测到显存不足(建议调小 ubatch 或把更多专家放到 CPU)"
                break
        m = _RE_OFFLOAD.search(line)
        if m:
            state["offloaded"] = int(m.group(1))
            state["total_layers"] = int(m.group(2))
        # buffer 汇总行: 只统计设备侧(排除 cpu_/host/blas)
        if " buffer size = " in low and not low.startswith("cpu_") and "_host " not in low and "blas" not in low:
            mm = re.search(r"=\s*([\d.]+)\s*MiB", line)
            if mm:
                mib = float(mm.group(1))
                buckets = state["buffers"]
                if " model buffer size " in low:
                    buckets["model"] = mib
                elif " kv buffer size " in low:
                    buckets["kv"] = mib
                elif " rs buffer size " in low:
                    buckets["rs"] = mib
                elif " compute buffer size " in low:
                    buckets["compute"] = mib
                if buckets.get("model") and buckets.get("compute"):
                    total = sum(buckets.get(k, 0) for k in ("model", "kv", "rs", "compute"))
                    hint = "[显存实测] llama 实际分配: 权重 %.2f + KV %.2f + RS %.2f + 计算图 %.2f = %.2f GiB" % (
                        buckets["model"] / 1024.0, buckets.get("kv", 0) / 1024.0,
                        buckets.get("rs", 0) / 1024.0, buckets["compute"] / 1024.0, total / 1024.0)
        return hint
