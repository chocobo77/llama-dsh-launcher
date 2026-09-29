#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""LlamaDSH Launcher — Linux 复刻版(原版为 .NET 8 WPF,见 ../LlamaDSH_Launcher_CS)。

运行:python3 main.py [-p 端口]
浏览器打开 http://127.0.0.1:8788 即是主窗口。
"""
import os
import sys
import json
import time
import fcntl
import argparse
import threading
import traceback
import subprocess
import webbrowser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import applog
import compat
import core
import monitor
import schema as schema_mod
import state
import updates
import cmdline

APP_DIR = os.path.dirname(os.path.abspath(__file__))
WEBUI_DIR = os.path.join(APP_DIR, "webui")
LOCK_FILE = os.path.join(os.path.expanduser("~"), ".llamadsh_launcher_linux.lock")
DSH_PORT = 3080
DEFAULT_PORT = 8788

applog.info("========== LlamaDSH Launcher (Linux) 启动 ==========")

# ---------------------------------------------------------------- 全局状态 --

SCHEMA = schema_mod.load()
HUB = core.LogHub()
LLAMA = core.LlamaRunner(HUB, SCHEMA)
DSH = core.DshRunner(HUB, SCHEMA)
CONVERTER = core.Converter(HUB)
MON = core.Monitor(HUB, LLAMA, DSH)
CONFIG = {}
JOBS = {}
JOB_SEQ = 0
JOB_LOCK = threading.Lock()

_LOG_SEQ_SEEN = 0


def refresh_schema():
    global SCHEMA
    SCHEMA = schema_mod.load()
    LLAMA.schema = SCHEMA
    DSH.schema = SCHEMA
    state.migrate_profile_flags_in_config(CONFIG, SCHEMA["defs"])


def current_profile():
    name = CONFIG.get("CurrentProfile") or "默认方案"
    profs = CONFIG.get("Profiles") or {}
    return name, profs.get(name) or state.default_profile()


def sync_active_profile():
    _, p = current_profile()
    MON.active_profile = p


# ------------------------------------------------------------------ 任务 ----

def run_job(name, fn):
    global JOB_SEQ
    with JOB_LOCK:
        JOB_SEQ += 1
        jid = str(JOB_SEQ)
        JOBS[jid] = {"name": name, "status": "running", "log": [], "result": None,
                     "started": time.time()}
    def wrap():
        try:
            res = fn(lambda t: JOBS[jid]["log"].append(str(t)))
            JOBS[jid]["result"] = res
            JOBS[jid]["status"] = "done"
        except Exception as ex:
            JOBS[jid]["result"] = str(ex)
            JOBS[jid]["status"] = "error"
            HUB.error_ex(ex, "后台任务 %s" % name)
    threading.Thread(target=wrap, daemon=True).start()
    return jid


# ------------------------------------------------------------- 启动接管 ----

def takeover_on_boot():
    """LoadState 等价:Llama 按 PID 接管;DSH 按端口→state→孤儿扫描。"""
    st = state.load_state()
    if st.get("LlamaPid") and procman_alive(st["LlamaPid"]):
        LLAMA.proc = _fake_proc(st["LlamaPid"])
        LLAMA.status = "taken_over"
        HUB.log("[接管] 检测到 Llama 进程(PID %d),已接管" % st["LlamaPid"])
    else:
        state.clear_state_for("LlamaPid")
    pid = procman_find_port()
    if pid:
        DSH.attach(pid, "[接管] 端口 %d 已有 DSH 在运行,直接接管" % DSH_PORT)
    elif st.get("DshPid") and procman_alive(st["DshPid"]):
        DSH.attach(st["DshPid"], "[接管] 恢复 DSH 进程", save_state=True)
    else:
        dsh_dir = st.get("DshDir", "")
        orphans = procman_find_orphans(dsh_dir)
        if orphans:
            DSH.attach(orphans[0], "[接管] 发现孤儿 DSH 进程")
        else:
            state.clear_state()
    # 载入配置
    global CONFIG
    CONFIG, err = state.load_config()
    if err:
        HUB.log("[配置] 读取失败(%s),已使用默认配置" % err)
    refresh_schema()
    sync_active_profile()
    name, _ = current_profile()
    HUB.log("[配置] 已加载 %s(方案: %s,参数 %d 项)" % (state.config_file_path(), name, len(SCHEMA["defs"])))


def procman_alive(pid):
    import procman as _p
    return _p.alive(int(pid))


def procman_find_port():
    import procman as _p
    return _p.find_process_by_port(DSH_PORT)


def procman_find_orphans(dsh_dir):
    import procman as _p
    return _p.find_orphaned_dsh_processes(dsh_dir)


def _fake_proc(pid):
    """接管的外部进程包装(仅用于 running 判断与 kill)。"""
    class FakeProc:
        def __init__(self, pid):
            self.pid = int(pid)
        def poll(self):
            return None if procman_alive(self.pid) else 0
        def wait(self):
            while procman_alive(self.pid):
                time.sleep(1)
            return 0
        def kill(self):
            import procman as _p
            _p.kill_process_tree(self.pid)
    return FakeProc(pid)


# ----------------------------------------------------------------- API ------

def api_bootstrap(_):
    name, prof = current_profile()
    return {
        "schema": {"groups": SCHEMA["groups"], "params": SCHEMA["defs"],
                   "errors": SCHEMA["errors"], "warnings": SCHEMA["warnings"],
                   "source": SCHEMA["source"]},
        "config": CONFIG, "current_profile": name, "config_dir": state.get_config_dir(),
        "config_path": state.config_file_path(),
        "dsh_port": DSH_PORT, "log_dir": applog.LOG_DIR,
        "override_path": schema_mod.OVERRIDE_PATH,
        "llama_status": LLAMA.status, "dsh_status": DSH.status,
        "llama_pid": LLAMA.proc.pid if LLAMA.proc else None,
        "dsh_pid": DSH.tracker.pid if DSH.tracker else None,
    }


def api_preview(body):
    prof = body.get("profile") or {}
    _, saved = current_profile()
    # UI 是编辑中的唯一事实来源:预览也把方案写回运行时副本(供监控取端口)
    name = CONFIG.get("CurrentProfile")
    if name and name in (CONFIG.get("Profiles") or {}):
        CONFIG["Profiles"][name] = prof
    sync_active_profile()
    defs = SCHEMA["defs"]
    try:
        lst, violations = cmdline.build_cmd(prof, defs)
        err = None
    except Exception as ex:
        lst, violations, err = [], [], str(ex)
    st = cmdline.ParamState(prof, defs)
    qw = cmdline.qwen35_partial_offload_warning(prof, defs) if not err else None
    est = cmdline.estimate_vram_needed(prof, defs, monitor.query_gpu) if not err else None
    fmt = cmdline.fmt_cmd(lst) if not err else ""
    if err:
        fmt = "Error:" + err
    if qw:
        fmt += "\n⚠ " + qw
    return {"fmt_cmd": fmt, "cmd_line": " ".join(cmdline.qa(x) for x in lst),
            "violations": violations, "qwen35": qw,
            "vram": {"detail": est["detail"],
                     "over": est["est_bytes"] > est["avail_bytes"] and est["avail_bytes"] > 0} if est else None}


def api_llama_start(body):
    prof = body.get("profile") or {}
    confirm = body.get("confirm") or {}
    name = CONFIG.get("CurrentProfile")
    if name in (CONFIG.get("Profiles") or {}):
        CONFIG["Profiles"][name] = prof
    sync_active_profile()
    # 确认链:safetensors → 依赖剔除 → 显存 → qwen35(与原版同序)
    if not confirm.get("done") and not confirm.get("skip_checks"):
        need = LLAMA.precheck(prof)
        if need.get("kind") in ("safetensors", "empty", "build_error"):
            return {"need_confirm": need}
        if need.get("kind") == "confirm":
            return {"need_confirm": need}
        cmd_list = need["cmd"]
    else:
        cmd_list = None
    if confirm.get("convert_dir"):
        # 走 safetensors 转换流程
        outtype = confirm.get("outtype") or "f16"
        convert_dir = confirm["convert_dir"]

        def after(path):
            if path:
                _, saved = current_profile()
                saved["ModelPath"] = path
                state.save_config(CONFIG)
                LLAMA.start(saved)
        CONVERTER.start(convert_dir, outtype, after)
        return {"started": True, "converting": True}
    ok, msg = LLAMA.start(prof, cmd_list)
    return {"started": ok, "error": msg}


def api_llama_stop(_):
    LLAMA.stop()
    return {"ok": True}


def api_dsh_start(body):
    prof = body.get("profile") or {}
    name = CONFIG.get("CurrentProfile")
    if name in (CONFIG.get("Profiles") or {}):
        CONFIG["Profiles"][name] = prof
    sync_active_profile()
    res = DSH.start(prof)
    if isinstance(res, dict) and res.get("need_rebuild"):
        return {"need_rebuild": res}
    ok, msg = res
    return {"started": ok, "error": msg}


def api_dsh_start_inner(body):
    """跳过重建提示后直接启动(用户在重建询问里选"否"时走这里)。"""
    prof = body.get("profile") or {}
    DSH.retries_left = max(DSH.retries_left, 1)
    res = DSH._prepare_then_start(prof)
    if isinstance(res, dict):  # 理论不会再要重建;防循环直接启动
        ok, msg = DSH._start_inner(prof)
    else:
        ok, msg = res
    return {"started": bool(ok), "error": msg if not ok else None}


def api_dsh_rebuild_and_start(body):
    prof = body.get("profile") or {}
    def on_done(ok):
        if ok:
            DSH._prepare_then_start(prof)
    ok, msg = DSH.rebuild(prof, on_done)
    return {"ok": ok, "error": msg}


def api_dsh_stop(_):
    DSH.stop()
    return {"ok": True}


def api_dsh_rebuild(body):
    prof = body.get("profile") or {}
    ok, msg = DSH.rebuild(prof)
    return {"ok": ok, "error": msg}


def api_compat(body):
    prof = body.get("profile") or {}
    lines = []
    report = compat.audit((prof.get("DshDir") or "").strip().strip('"'), lines.append)
    return {"report": report, "log": lines}


def api_compat_repair(body):
    prof = body.get("profile") or {}
    dsh_dir = (prof.get("DshDir") or "").strip().strip('"')
    lines = []
    report = compat.audit(dsh_dir, lines.append)
    fixed = compat.repair(dsh_dir, report, lines.append)
    lines.append("[兼容] 修复完成 %d 项,自动复检" % fixed)
    report2 = compat.audit(dsh_dir, lines.append)
    return {"report": report2, "log": lines, "fixed": fixed}


def api_plugins(body):
    prof = body.get("profile") or {}
    lines = []
    res = compat.plugin_check((prof.get("DshDir") or "").strip().strip('"'), lines.append)
    return {"report": res, "log": lines}


def api_search(body):
    which = body.get("target")
    if which == "llama":
        return {"result": updates.find_llama_server()}
    return {"result": updates.find_dsh_project()}


def api_check_update(body):
    which = body.get("target")
    if which == "llama":
        d = (body.get("dir") or "").strip().strip('"')
        local = updates.llama_local_version(d)
        latest, tag = updates.llama_latest_version()
        if latest is None:
            return {"error": "无法获取上游版本(网络?)"}
        return {"local": local, "latest": latest, "tag": tag,
                "up_to_date": local is not None and local >= latest}
    d = (body.get("dir") or "").strip().strip('"')
    return updates.dsh_check_update(d)


def api_do_update(body):
    which = body.get("target")
    if which == "llama":
        d = (body.get("dir") or "").strip().strip('"')
        return {"job": run_job("llama-update", lambda jl: updates.llama_update(d, HUB.log, jl))}
    d = (body.get("dir") or "").strip().strip('"')
    ok, msg = updates.dsh_pull(d)
    return {"ok": ok, "msg": msg}


def api_job(body):
    jid = str(body.get("id"))
    j = JOBS.get(jid) or {"status": "unknown"}
    return {"status": j.get("status"), "log": j.get("log", [])[-200:], "result": j.get("result")}


def api_save_config(body):
    global CONFIG
    cfg = body.get("config")
    if not cfg:
        return {"error": "缺少 config"}
    CONFIG = cfg
    for name, p in (cfg.get("Profiles") or {}).items():
        base = state.default_profile()
        base.update(p or {})
        cfg["Profiles"][name] = base
    sync_active_profile()
    path = state.save_config(cfg)
    HUB.log("[配置] 已保存: " + path)
    return {"ok": True, "path": path}


def api_load_config(_):
    global CONFIG
    CONFIG, err = state.load_config()
    if err:
        HUB.log("[配置] 读取失败(%s),已使用默认配置" % err)
    refresh_schema()
    sync_active_profile()
    name, _ = current_profile()
    HUB.log("[配置] 已加载: %s(方案: %s)" % (state.config_file_path(), name))
    return {"config": CONFIG, "error": err, "current_profile": name}


def api_set_config_dir(body):
    d = body.get("dir") or ""
    try:
        state.set_config_dir(d)
        HUB.log("[配置] 配置目录已更改为: " + d)
        return {"ok": True, "dir": state.get_config_dir()}
    except Exception as ex:
        return {"error": str(ex)}


def api_schema_reload(body):
    if body.get("profile") and CONFIG.get("CurrentProfile") in (CONFIG.get("Profiles") or {}):
        CONFIG["Profiles"][CONFIG["CurrentProfile"]] = body["profile"]
    refresh_schema()
    sync_active_profile()
    HUB.log("[参数] 配置已重载,来源: %s,参数 %d 项 / 组 %d 个" % (
        SCHEMA["source"], len(SCHEMA["defs"]), len(SCHEMA["groups"])))
    msgs = SCHEMA["errors"] + SCHEMA["warnings"]
    return {"schema": {"groups": SCHEMA["groups"], "params": SCHEMA["defs"],
                       "errors": SCHEMA["errors"], "warnings": SCHEMA["warnings"],
                       "source": SCHEMA["source"]}, "messages": msgs}


def api_status(_):
    name, prof = current_profile()
    dsh_pid = None
    if DSH.tracker is not None:
        dsh_pid = DSH.tracker.pid
    elif DSH.proc is not None:
        dsh_pid = DSH.proc.pid
    return {
        "llama": {"status": LLAMA.status, "pid": LLAMA.proc.pid if LLAMA.proc else None},
        "dsh": {"status": DSH.status, "pid": dsh_pid,
                "url": "http://127.0.0.1:%d" % DSH_PORT,
                "rebuilding": DSH.rebuild_running(),
                "converting": CONVERTER.running},
        "monitor": MON.snapshot,
        "profile": name,
        "dsh_stale_lock": DSH.stale_lock_present(),
    }


def api_logs(body):
    after = int(body.get("after") or 0)
    items, new_after = HUB.since(after, 400)
    return {"lines": [{"seq": s, "text": t} for s, t in items], "after": new_after}


def api_logs_clear(_):
    HUB.ring.clear()
    HUB.log("[日志] 已清空(历史见 run.log)")
    return {"ok": True}


def api_open_logs(_):
    try:
        subprocess.Popen(["xdg-open", applog.LOG_DIR],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        return {"ok": True}
    except Exception as ex:
        return {"error": str(ex)}


def api_open_dsh(_):
    return {"url": "http://127.0.0.1:%d" % DSH_PORT}


def api_convert_start(body):
    """独立触发 safetensors 转换(不自动启动)。"""
    d = (body.get("dir") or "").strip().strip('"')
    outtype = body.get("outtype") or "f16"
    CONVERTER.start(d, outtype, lambda p: HUB.log("[转换] 结束: %s" % p))
    return {"ok": True}


def api_browse(body):
    """服务端目录浏览(替代 WPF OpenFileDialog/FolderBrowserDialog)。"""
    import glob as _glob
    p = (body.get("path") or os.path.expanduser("~")).strip()
    if not os.path.isdir(p):
        p = os.path.dirname(p) or os.path.expanduser("~")
    p = os.path.abspath(p)
    dirs, files = [], []
    try:
        for e in sorted(os.scandir(p), key=lambda x: (not x.is_dir(), x.name.lower())):
            if e.name.startswith("."):
                continue
            if e.is_dir():
                dirs.append(e.name)
            elif e.is_file():
                try:
                    sz = e.stat().st_size
                except OSError:
                    sz = 0
                files.append({"name": e.name, "size": sz})
    except OSError as ex:
        return {"error": str(ex), "path": p}
    drives = ["/"] + sorted(set(mountpoint for mountpoint in _mounts()))
    return {"path": p, "dirs": dirs, "files": files[:500], "roots": drives}


def _mounts():
    try:
        with open("/proc/mounts") as f:
            for ln in f:
                parts = ln.split()
                if len(parts) > 1 and parts[1].startswith("/media") or parts[1] == "/" or parts[1].startswith("/home"):
                    yield parts[1]
    except OSError:
        return


def api_health(_):
    """HealthCheck(5s):接管/短命进程的存活对账。"""
    if LLAMA.status in ("running", "starting", "taken_over") and LLAMA.proc and LLAMA.proc.poll() is not None:
        LLAMA.status = "failed"
    return {"ok": True}


ROUTES = {
    ("bootstrap",): api_bootstrap,
    ("preview",): api_preview,
    ("llama/start",): api_llama_start,
    ("llama/stop",): api_llama_stop,
    ("dsh/start",): api_dsh_start,
    ("dsh/start_inner",): api_dsh_start_inner,
    ("dsh/rebuild",): api_dsh_rebuild,
    ("dsh/rebuild_and_start",): api_dsh_rebuild_and_start,
    ("dsh/stop",): api_dsh_stop,
    ("dsh/compat",): api_compat,
    ("dsh/compat/repair",): api_compat_repair,
    ("dsh/plugins",): api_plugins,
    ("search",): api_search,
    ("update/check",): api_check_update,
    ("update/do",): api_do_update,
    ("job",): api_job,
    ("config/save",): api_save_config,
    ("config/load",): api_load_config,
    ("config/setdir",): api_set_config_dir,
    ("schema/reload",): api_schema_reload,
    ("status",): api_status,
    ("logs",): api_logs,
    ("logs/clear",): api_logs_clear,
    ("open/logs",): api_open_logs,
    ("open/dsh",): api_open_dsh,
    ("convert/start",): api_convert_start,
    ("browse",): api_browse,
    ("health",): api_health,
}

MIME = {".html": "text/html; charset=utf-8", ".js": "application/javascript; charset=utf-8",
        ".css": "text/css; charset=utf-8", ".yml": "text/yaml; charset=utf-8",
        ".ico": "image/x-icon", ".svg": "image/svg+xml"}


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        pass

    def _send(self, code, body, ctype="application/json; charset=utf-8"):
        data = body if isinstance(body, bytes) else json.dumps(body, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        path = self.path.split("?")[0]
        if path in ("/", "/index.html"):
            return self._static("index.html")
        if path == "/favicon.ico":
            return self._send(204, b"", "image/x-icon")
        safe = os.path.normpath(path.lstrip("/"))
        full = os.path.join(WEBUI_DIR, safe)
        if full.startswith(WEBUI_DIR) and os.path.isfile(full):
            return self._static(safe)
        self._send(404, {"error": "not found"})

    def _static(self, rel):
        full = os.path.join(WEBUI_DIR, rel)
        ext = os.path.splitext(full)[1]
        try:
            with open(full, "rb") as f:
                self._send(200, f.read(), MIME.get(ext, "application/octet-stream"))
        except OSError:
            self._send(404, {"error": "not found"})

    def do_POST(self):
        path = self.path.split("?")[0].strip("/")
        if path.startswith("api/"):
            path = path[4:]
        try:
            n = int(self.headers.get("Content-Length") or 0)
            raw = self.rfile.read(n) if n else b""
            body = json.loads(raw.decode("utf-8")) if raw else {}
        except Exception:
            return self._send(400, {"error": "bad request"})
        fn = ROUTES.get((path,))
        if fn is None:
            return self._send(404, {"error": "unknown api: " + path})
        try:
            self._send(200, fn(body))
        except Exception as ex:
            HUB.error_ex(ex, "API %s" % path)
            self._send(500, {"error": str(ex), "trace": traceback.format_exc()[-1500:]})


def main():
    ap = argparse.ArgumentParser(description="LlamaDSH Launcher (Linux 复刻版)")
    ap.add_argument("-p", "--port", type=int, default=DEFAULT_PORT)
    args = ap.parse_args()

    # 单实例
    try:
        _lockfh = open(LOCK_FILE, "w")
        fcntl.flock(_lockfh, fcntl.LOCK_EX | fcntl.LOCK_NB)
        _lockfh.write(str(os.getpid()))
        _lockfh.flush()
    except OSError:
        print("已经在运行中(锁文件 %s)。如确认未运行,请删除该文件。" % LOCK_FILE)
        sys.exit(1)

    takeover_on_boot()
    threading.Thread(target=MON.loop, daemon=True).start()
    threading.Thread(target=_health_loop, daemon=True).start()

    port = args.port
    server = None
    for i in range(20):
        try:
            server = ThreadingHTTPServer(("127.0.0.1", port + i), Handler)
            port = port + i
            break
        except OSError:
            continue
    if server is None:
        print("无法绑定端口 %d~%d" % (args.port, args.port + 19))
        sys.exit(1)
    url = "http://127.0.0.1:%d" % port
    print("LlamaDSH Launcher(Linux 复刻版)已启动")
    print("主窗口: %s" % url)
    print("Llama 服务端口: 1234(默认) | DSH Web: http://127.0.0.1:%d" % DSH_PORT)
    applog.info("Web UI: %s" % url)
    try:
        webbrowser.open(url)
    except Exception:
        pass
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


def _health_loop():
    while True:
        try:
            api_health({})
        except Exception:
            pass
        time.sleep(5)


if __name__ == "__main__":
    main()
