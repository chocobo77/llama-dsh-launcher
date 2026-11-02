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
import procman
import monitor
import nodetool
import schema as schema_mod
import state
import toolchain
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
    import procman as _pm
    if st.get("LlamaPid") and procman_alive(st["LlamaPid"]) and _pm.process_name(st["LlamaPid"]) in ("llama-server", "llama-server.exe"):
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
    LLAMA.persist_config = persist_config
    HUB.log("[配置] 已加载 %s(方案: %s,参数 %d 项)" % (state.config_file_path(), name, len(SCHEMA["defs"])))


def persist_config():
    """OOM 降层落盘: 把内存中的 CONFIG(含降层后的 gpu-layers)写盘。"""
    try:
        state.save_config(CONFIG)
        HUB.log("[配置] 已保存: %s" % state.config_file_path())
    except Exception as ex:
        HUB.log("[配置] 保存失败: %s" % ex)


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
        lst, violations, flag_args = cmdline.build_cmd(prof, defs)
        err = None
    except Exception as ex:
        lst, violations, flag_args, err = [], [], {}, str(ex)
    qw = cmdline.qwen35_partial_offload_warning(prof, defs) if not err else None
    est = cmdline.estimate_vram_needed(prof, defs, monitor.query_gpu) if not err else None
    fmt = cmdline.fmt_cmd(lst, defs) if not err else ""
    if err:
        fmt = "Error:" + err
    elif not lst:
        fmt = ("(预览为空——请先在「📂 运行环境」里设置 llama.cpp 目录;"
               "模型路径可选,设置后命令会在此实时生成)")
    if qw:
        fmt += "\n⚠ " + qw
    return {"fmt_cmd": fmt, "cmd_line": " ".join(cmdline.qa(x) for x in lst),
            "violations": violations, "qwen35": qw, "flag_args": flag_args,
            "vram": {"detail": est["detail"], "fits": bool(est.get("fits", True)),
                     "over": (not est.get("fits", True)) and est["avail_bytes"] > 0} if est else None}


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
    """插件智能检测(🧩 按钮): 结构化审计 + 可修复项摘要。"""
    prof = body.get("profile") or {}
    dsh_dir = _dsh_dir_of(prof)
    plugins = compat.audit_plugins("web", dsh_dir)
    lines = []
    compat.enforce_pinned("web", lines.append)
    items = []
    for p in plugins:
        notes = []
        if p["peer_issues"]:
            notes.append("peer 不兼容: " + "; ".join("%s %s" % (x["dep"], x["range"]) for x in p["peer_issues"]))
        if p["dead_peers"]:
            notes.append("死依赖: " + ", ".join(p["dead_peers"]))
        if p["missing_specs"]:
            notes.append("缺依赖: " + ", ".join(p["missing_specs"]))
        if p["errors"]:
            notes.append("; ".join(p["errors"]))
        items.append({"name": p["name"], "note": "; ".join(notes) or p["state_label"],
                      "level": "error" if (p["errors"] or p["dead_peers"]) else
                               ("warn" if notes else "ok")})
    return {"report": {"items": items, "count": len(plugins)}, "log": lines}


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
    if MON.lightweight:
        return {
            "llama": {"status": LLAMA.status, "pid": LLAMA.proc.pid if LLAMA.proc else None},
            "dsh": {"status": DSH.status, "pid": DSH.tracker.pid if DSH.tracker else None,
                    "url": "http://127.0.0.1:%d" % DSH_PORT,
                    "rebuilding": DSH.rebuild_running(),
                    "converting": CONVERTER.running},
            "monitor": {"lightweight": True, "llama_status": LLAMA.status,
                        "dsh_status": DSH.status,
                        "vram": MON.snapshot.get("vram", {"used": 0, "total": 0, "pct": 0}),
                        "breaker_triggered": MON.breaker_triggered},
            "profile": name,
            "lightweight": True,
            "dsh_stale_lock": DSH.stale_lock_present(),
        }
    return {
        "llama": {"status": LLAMA.status, "pid": LLAMA.proc.pid if LLAMA.proc else None},
        "dsh": {"status": DSH.status, "pid": dsh_pid,
                "url": "http://127.0.0.1:%d" % DSH_PORT,
                "rebuilding": DSH.rebuild_running(),
                "converting": CONVERTER.running},
        "monitor": MON.snapshot,
        "profile": name,
        "lightweight": MON.lightweight,
        "dsh_stale_lock": DSH.stale_lock_present(),
    }


def api_logs(body):
    try:
        after = int(body.get("after") or 0)
    except (TypeError, ValueError):
        after = 0
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


def api_lightweight(body):
    """轻量化模式开关(客户端最小化到托盘时调用)。"""
    MON.lightweight = bool(body.get("on"))
    HUB.log("[轻量化] %s(采样间隔: %s)" % (
        "开——后台低频保活" if MON.lightweight else "关——恢复正常监控",
        "无服务 60s / 有服务 30s" if MON.lightweight else "3s"))
    return {"lightweight": MON.lightweight,
            "llama_status": LLAMA.status, "dsh_status": DSH.status}


def api_monitor(body):
    """监测页快照(3s 轮询)。打点 monitor_visible 以启用 3s 快速采样。"""
    MON.monitor_visible = bool(body.get("visible", True))
    return MON.snapshot


def api_args_map(body):
    """逐参数真实启动参数回显(48a9a2f)。"""
    prof = body.get("profile") or {}
    name = CONFIG.get("CurrentProfile")
    if name in (CONFIG.get("Profiles") or {}):
        CONFIG["Profiles"][name] = prof
    sync_active_profile()
    return {"flag_args": LLAMA.args_map(prof)}


# ------------------------------------------------ Toolchain / Node API ----

def _active_variant_profile():
    name, prof = current_profile()
    return prof


def api_toolchain_variants(_):
    prof = _active_variant_profile()
    toolchain.ensure_default_variant(prof)
    out = []
    for v in prof.get("LlamaVariants") or []:
        lb = toolchain.local_build(v.get("Dir") or "") if v.get("Dir") else None
        out.append({"name": v.get("Name", ""), "repo": v.get("Repo", ""),
                    "dir": v.get("Dir", ""), "installed": bool(toolchain.find_llama_server_exe(v.get("Dir") or "")),
                    "local_build": lb})
    active = prof.get("ActiveLlamaVariant")
    if not active and out:
        active = out[0]["name"]
    _, prof0 = current_profile()
    return {"variants": out, "active": active,
            "backend": toolchain.resolve_backend(prof0),
            "backend_auto": toolchain.backend_preference(),
            "backend_choice": (prof0.get("LlamaBackend") or "").lower(),
            "gpu_name": monitor.gpu_friendly_name()}


def api_toolchain_switch(body):
    name = body.get("name") or ""
    _, prof = current_profile()
    if LLAMA.running:
        return {"error": "Llama 服务运行中,请先停止再切换变体"}
    ok, msg, _d = toolchain.switch_variant(prof, name)
    if ok:
        persist_config()
        HUB.log("[变体] " + msg)
    return {"ok": ok, "msg": msg, "error": None if ok else msg}


def api_toolchain_remove(body):
    name = body.get("name") or ""
    _, prof = current_profile()
    vs = prof.get("LlamaVariants") or []
    if len(vs) <= 1:
        return {"error": "至少保留一个变体(只移除列表项,不删磁盘文件)"}
    victim = next((x for x in vs if x.get("Name") == name), None)
    if victim is None:
        return {"error": "变体不存在: %s" % name}
    prof["LlamaVariants"] = [x for x in vs if x.get("Name") != name]
    if prof.get("ActiveLlamaVariant") == name:
        rest = prof["LlamaVariants"][0]
        prof["ActiveLlamaVariant"] = rest.get("Name")
        prof["LlamaDir"] = rest.get("Dir") or ""
    persist_config()
    HUB.log("[变体] 已移除列表项: %s(目录文件保留)" % name)
    return {"ok": True}


def api_toolchain_parse_repo(body):
    r = toolchain.parse_repo_input(body.get("url") or "")
    if not r:
        return {"error": "无法识别仓库链接(支持 主页/.git/tree/releases 深链/裸 owner/repo)"}
    owner, name = r.split("/", 1)
    _, prof = current_profile()
    return {"repo": r, "name": name,
            "dir": toolchain.suggest_variant_dir(prof.get("LlamaDir"), owner),
            "backend": toolchain.backend_preference()}


def api_toolchain_install(body):
    _, prof = current_profile()
    if LLAMA.running:
        return {"error": "Llama 服务运行中,请先停止再安装变体"}
    repo_input = body.get("repo") or body.get("url") or ""
    name = (body.get("name") or "").strip()
    dir_ = (body.get("dir") or "").strip()
    def job(jl):
        ok, msg = toolchain.install_variant(prof, repo_input, name, dir_, jl)
        if ok:
            persist_config()
            HUB.log("[变体] " + msg.splitlines()[0])
        return {"ok": ok, "msg": msg}
    return {"job": run_job("variant-install", job)}


def api_toolchain_check_update(body):
    """按激活变体的仓库 + 显卡后端查更新。"""
    _, prof = current_profile()
    toolchain.ensure_default_variant(prof)
    active = prof.get("ActiveLlamaVariant") or ""
    v = next((x for x in prof.get("LlamaVariants") or [] if x.get("Name") == active), None) \
        or (prof.get("LlamaVariants") or [{}])[0]
    repo = v.get("Repo") or toolchain.DEFAULT_REPO
    dir_ = v.get("Dir") or prof.get("LlamaDir") or ""
    backend = toolchain.resolve_backend(prof)
    try:
        release = toolchain.fetch_latest_llama_release(repo, backend, None)
    except Exception as ex:
        return {"error": "查询失败(%s): %s" % (repo, ex)}
    local = toolchain.local_build(dir_)
    latest = release.get("build", 0)
    mismatch = bool(local is not None and latest > 0 and local != latest and local > latest)
    return {"local": local, "latest": latest, "tag": release.get("tag"),
            "repo": repo, "backend": backend, "asset_name": release.get("asset_name"),
            "size": release.get("size"),
            "up_to_date": local is not None and latest > 0 and local >= latest,
            "mismatch": mismatch}


def api_toolchain_update(body):
    _, prof = current_profile()
    if LLAMA.running:
        return {"error": "Llama 服务运行中,请先停止再更新"}
    toolchain.ensure_default_variant(prof)
    active = prof.get("ActiveLlamaVariant") or ""
    v = next((x for x in prof.get("LlamaVariants") or [] if x.get("Name") == active), None) \
        or (prof.get("LlamaVariants") or [{}])[0]
    repo = v.get("Repo") or toolchain.DEFAULT_REPO
    dir_ = v.get("Dir") or prof.get("LlamaDir") or ""
    if not dir_:
        return {"error": "未设置 llama.cpp 目录"}
    backend = toolchain.resolve_backend(prof)
    def job(jl):
        ok, msg = updates.llama_update(dir_, HUB.log, jl, repo=repo, backend=backend)
        return {"ok": ok, "msg": msg}
    return {"job": run_job("llama-update", job)}


def api_node_status(_):
    st = nodetool.detect()
    base, v = nodetool.latest_v24()
    st["latest_v24"] = v
    return st


def api_node_install(_):
    return {"job": run_job("node-install", lambda jl: nodetool.install_node_env(jl))}


def _dsh_dir_of(prof):
    return (prof.get("DshDir") or "").strip().strip('"')


def _dsh_running_guard():
    """pnpm 会写 node_modules, 运行中拒绝(C# RequireDshStopped)。"""
    if procman.find_process_by_port(DSH_PORT) or procman.node_host_running():
        return False
    return True


def api_plugins_audit(body):
    """插件管理器: 结构化逐插件审计(含锁定回写强制)。"""
    prof = body.get("profile") or {}
    dsh_dir = _dsh_dir_of(prof)
    writebacks = compat.enforce_pinned("web", HUB.log)
    plugins = compat.audit_plugins("web", dsh_dir)
    errs = sum(1 for p in plugins if p["errors"])
    warns = sum(1 for p in plugins if p["peer_issues"] or p["missing_specs"] or p["dead_peers"])
    locked = sum(1 for p in plugins if p["locked"])
    ver = compat.get_dsh_version(dsh_dir)
    return {"plugins": plugins, "writebacks": writebacks,
            "summary": "DSH %s · 插件 %d 个 · 错误 %d · 警告 %d · 锁定停用 %d" % (
                ver or "?", len(plugins), errs, warns, locked),
            "dsh_running": not _dsh_running_guard()}


def api_plugins_set_active(body):
    ok, msg = compat.set_bundle_active(body.get("profile") or "web",
                                       body.get("name") or "", bool(body.get("active")))
    return {"ok": ok, "msg": msg}


def api_plugins_exempt(body):
    ok, msg = compat.exempt_plugin(body.get("profile") or "web",
                                   body.get("name") or "", body.get("version") or "")
    return {"ok": ok, "msg": msg}


def api_plugins_preview_update(body):
    prof = body.get("profile") or {}
    return compat.preview_plugin_update("web", body.get("name") or "", _dsh_dir_of(prof))


def api_plugins_upgrade(body):
    prof = body.get("profile") or {}
    if not _dsh_running_guard():
        return {"error": "该操作需要先停止 DSH(会运行 pnpm 修改插件目录)。"}
    ok, msg = compat.upgrade_plugin("web", body.get("name") or "", _dsh_dir_of(prof))
    return {"ok": ok, "msg": msg}


def api_plugins_uninstall(body):
    if not _dsh_running_guard():
        return {"error": "该操作需要先停止 DSH(会运行 pnpm 修改插件目录)。"}
    ok, msg = compat.uninstall_plugin("web", body.get("name") or "")
    return {"ok": ok, "msg": msg}


def api_plugins_install_local(body):
    if not _dsh_running_guard():
        return {"error": "该操作需要先停止 DSH(会运行 pnpm 修改插件目录)。"}
    ok, msg = compat.install_local_plugin("web", body.get("source") or "", HUB.log)
    return {"ok": ok, "msg": msg}


def api_plugins_open_dir(body):
    """打开插件目录(C# 打开目录按钮;失败仅记日志不打断)。"""
    path = body.get("dir") or ""
    if not path or not os.path.isdir(path):
        return {"error": "目录不存在: %s" % path}
    try:
        subprocess.Popen(["xdg-open", path],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        return {"ok": True}
    except Exception as ex:
        applog.error("打开插件目录失败: %s -> %s" % (path, ex))
        return {"error": str(ex)}


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
    ("monitor",): api_monitor,
    ("lightweight",): api_lightweight,
    ("args_map",): api_args_map,
    ("toolchains/variants",): api_toolchain_variants,
    ("toolchains/variants/switch",): api_toolchain_switch,
    ("toolchains/variants/remove",): api_toolchain_remove,
    ("toolchains/parse_repo",): api_toolchain_parse_repo,
    ("toolchains/variants/install",): api_toolchain_install,
    ("toolchains/check_update",): api_toolchain_check_update,
    ("toolchains/update",): api_toolchain_update,
    ("node/status",): api_node_status,
    ("node/install",): api_node_install,
    ("dsh/plugins/audit",): api_plugins_audit,
    ("dsh/plugins/open_dir",): api_plugins_open_dir,
    ("dsh/plugins/set_active",): api_plugins_set_active,
    ("dsh/plugins/exempt",): api_plugins_exempt,
    ("dsh/plugins/preview_update",): api_plugins_preview_update,
    ("dsh/plugins/upgrade",): api_plugins_upgrade,
    ("dsh/plugins/uninstall",): api_plugins_uninstall,
    ("dsh/plugins/install_local",): api_plugins_install_local,
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
        # GET /api/status: 供客户端自举探测与轻量轮询使用(客户端 _probe 走 GET)
        if path == "/api/status":
            try:
                return self._send(200, api_status({}))
            except Exception as ex:
                return self._send(500, {"error": str(ex)})
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

    # sudo 血统切断(对齐 C# App.OnStartup 的安装器血统检测): 以 root 运行时
    # HOME=/root, 配置/检索/DSH_HOME/日志全部错位。检测到经 sudo 发起时,
    # 自动以发起 sudo 的桌面用户身份重启自己, 不再以 root 继续。
    if os.geteuid() == 0 and os.environ.get("SUDO_USER"):
        user = os.environ["SUDO_USER"]
        home = "/home/" + user
        if os.path.isdir(home):
            argv = [sys.executable, os.path.abspath(sys.argv[0])] + sys.argv[1:]
            print("检测到以 sudo(root)启动——所有 ~ 路径会错位,自动切换为用户 %s 重新启动…" % user)
            os.execvp("sudo", ["sudo", "-u", user,
                               "env", "HOME=" + home, "USER=" + user,
                               "LANG=" + os.environ.get("LANG", "C.UTF-8")] + argv)
            # execvp 成功则不再返回
    elif os.geteuid() == 0:
        print("⚠ 正在以 root 运行:配置/检索/日志都会指向 /root,建议改用桌面用户启动")

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
            server.daemon_threads = True
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
