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
import datetime
import fcntl
import signal
import secrets
import argparse
import threading
import traceback
import subprocess
import webbrowser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import unquote

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

# 本机 API 鉴权 token: POST 面能启停进程/pnpm 装插件/浏览全盘,
# 此前仅 Host 白名单——本机任意进程即可驱动全部 API(等价本机 RCE)。
# 浏览器经 index.html 注入获得; client.py/tray.py 经 token 文件(0600)获得。
API_TOKEN = secrets.token_urlsafe(24)
TOKEN_FILE = os.path.join(os.path.expanduser("~"), ".dsh_launcher_token")
# 机器可读的发现文件: 与 Windows 侧 Services/AgentApi.cs 同名同字段(port/token/enabled,
# 见 docs/API-CONTRACT.md)。~/.dsh_launcher_token 保留作向后兼容(client.py/tray.py 仍读它),
# 新调用方应优先读 agent.json —— 那里连**实际监听端口**都有: 默认 8788 被占时端口会顺延
# (port + i), 只按默认端口去连会连到别的进程或连不上。
AGENT_FILE = os.path.join(applog.LOG_DIR, "agent.json")


def _write_token_file():
    try:
        fd = os.open(TOKEN_FILE, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, "w") as f:
            f.write(API_TOKEN)
    except OSError as ex:
        # token 文件是托盘/客户端拿鉴权凭证的**唯一**来源(网页端走 index.html 注入)。
        # 写不出来时它们每次调用都 401, 而现场只看得到"unauthorized" —— 必须把原因
        # (最常见是 HOME 只读/以 root 启动导致错位)落在 error.log 与 stdout 上。
        applog.error("[鉴权] token 文件写入失败(%s: %s), 路径 %s —— 托盘与网页端会持续 401"
                     % (type(ex).__name__, ex, TOKEN_FILE))
        print("⚠ token 文件写入失败(%s): 托盘与网页端会持续 401, 路径 %s" % (ex, TOKEN_FILE))


def _write_agent_file(port):
    """写端口 + token 的发现文件(Windows 侧同路径同字段, 见 docs/API-CONTRACT.md)。

    enabled 在 Linux 侧恒为 true: Windows 的 agent 接口是可关掉的旁路(LLAMADSH_AGENT_DISABLE=1
    或 agent.json 里 enabled=false 就干脆不监听), Linux 的这个 HTTP 服务本身就是主窗口,
    没有"监听但不服务"的形态 —— 字段照样要写, 否则调用方得按平台分支。

    必须在**绑定成功之后**调用: 端口被占会顺延(port + i), 写默认端口等于骗调用方。
    """
    try:
        # 不依赖 applog 顺带建目录这个副作用: 目录不在就直接写会 FileNotFoundError。
        os.makedirs(os.path.dirname(AGENT_FILE), exist_ok=True)
        fd = os.open(AGENT_FILE, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, "w") as f:
            json.dump({"port": int(port), "token": API_TOKEN, "enabled": True}, f)
        try:
            # 文件已存在时 O_CREAT 的 mode 不起作用(权限由前任写入者 + umask 决定),
            # 补一次 chmod: 这文件里躺着"本机任意进程可启停服务/退出启动器"的 token。
            os.chmod(AGENT_FILE, 0o600)
        except OSError as ex:
            applog.error("[鉴权] agent.json 权限收紧失败(%s: %s), 路径 %s"
                         % (type(ex).__name__, ex, AGENT_FILE))
    except OSError as ex:
        # 与 token 文件同口径: 写不出来时外部 cli/curl 只能瞎猜端口, 现场却看不到原因。
        applog.error("[鉴权] agent.json 写入失败(%s: %s), 路径 %s —— 外部调用方(cli/curl)"
                     "无法发现端口与 token" % (type(ex).__name__, ex, AGENT_FILE))
        print("⚠ agent.json 写入失败(%s): 外部调用方(cli/curl)无法发现端口与 token, 路径 %s"
              % (ex, AGENT_FILE))


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
        # 长驻进程慢性泄漏: 只保留最近 20 个 job
        try:
            done = [k for k, v in JOBS.items() if v["status"] in ("done", "error")]
            done.sort(key=lambda k: JOBS[k]["started"])
            for k in done[:-20]:
                JOBS.pop(k, None)
        except Exception:
            pass
    threading.Thread(target=wrap, daemon=True).start()
    return jid




def takeover_on_boot():
    """LoadState 等价:Llama 按 PID 接管;DSH 按端口→state→孤儿扫描。"""
    st = state.load_state()
    import procman as _pm
    if st.get("LlamaPid") and procman_alive(st["LlamaPid"]) and _pm.process_name(st["LlamaPid"]) in ("llama-server", "llama-server.exe"):
        LLAMA.proc = _fake_proc(st["LlamaPid"])
        LLAMA.status = "taken_over"
        HUB.log("[接管] 检测到 Llama 进程(PID %d),已接管" % st["LlamaPid"])
    else:
        if st.get("LlamaPid"):
            HUB.log("[接管] 状态文件中的 Llama PID %s 已失效(进程退出或 PID 被复用),跳过接管" % st["LlamaPid"])
        state.clear_state_for("LlamaPid")
    pid = procman_find_port()
    dsh_dir_st = st.get("DshDir", "")
    if pid:
        # 端口上有进程 ≠ 那是 DSH: 无身份校验就接管, 用户自己跑在 3080 的服务会被标成
        # "接管运行中", 点"停止"即被 kill_process_tree 杀掉整棵树(Windows 侧同一族缺陷)。
        ok, why = _pm.dsh_identity(pid, dsh_dir_st, DSH_PORT)
        if ok:
            DSH.attach(pid, "[接管] 端口 %d 已有 DSH 在运行,直接接管(%s)" % (DSH_PORT, why))
        else:
            HUB.log("[接管] 端口 %d 上的 PID %d 拒绝接管: %s" % (DSH_PORT, pid, why))
    if not DSH.running:
        if st.get("DshPid") and procman_alive(st["DshPid"]):
            ok, why = _pm.dsh_identity(st["DshPid"], dsh_dir_st, DSH_PORT)
            if ok:
                DSH.attach(st["DshPid"], "[接管] 恢复 DSH 进程(%s)" % why, save_state=True)
            else:
                HUB.log("[接管] 状态文件中的 DSH PID %s 判定失败: %s" % (st["DshPid"], why))
        if not DSH.running and st.get("DshPid") and not procman_alive(st["DshPid"]):
            HUB.log("[接管] 状态文件中的 DSH PID %s 已失效(进程退出或 PID 被复用),跳过接管" % st["DshPid"])
            state.clear_state_for("DshPid")
        if not DSH.running and st.get("DshPid") and procman_alive(st["DshPid"]):
            # 判定不成立但进程还活着: 保留状态, 下次启动(或提权后)还能认出来
            HUB.log("[接管] 保留了状态里的 DSH PID %s(判定不确定时不清状态)" % st["DshPid"])
    if not DSH.running and not procman_find_port():
        orphans = procman_find_orphans(st.get("DshDir", ""))
        if orphans:
            DSH.attach(orphans[0], "[接管] 发现孤儿 DSH 进程")
        elif not st.get("DshPid"):
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
            # fits 保持三态(None = 探针无显存数据); over 只在**确认**超出时为 True
            "vram": {"detail": est["detail"], "fits": est.get("fits"),
                     "over": est.get("fits") is False} if est else None}


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
        if need.get("kind") in ("safetensors", "empty", "build_error", "confirm"):
            return {"need_confirm": need}
        cmd_list = need["cmd"]
    else:
        # 用户已在前端确认(剔除/显存/qwen35): 跳过二次硬拒,
        # 否则"确认后启动"被 build 时的 violations 检查静默拒绝
        cmd_list = None
    if confirm.get("convert_dir"):
        outtype = confirm.get("outtype") or "f16"
        convert_dir = confirm["convert_dir"]

        def after(path):
            if path:
                _, saved = current_profile()
                saved["ModelPath"] = path
                state.save_config(CONFIG)
                ok, msg = LLAMA.start(saved, skip_vram_confirm=True)
                if not ok:
                    HUB.log("[转换] 转换完成但启动失败: %s" % msg)
        if not CONVERTER.start(convert_dir, outtype, after):
            # start() 返回 False = 转换根本没排上(目录不存在/依赖装不上/已有转换在跑);
            # 以前一律回 converting:true, 前端就永远等一个不会来的转换。
            return {"started": False, "error": "转换未能启动(详见运行日志: 目录不存在、"
                                               "依赖装不上或已有转换在进行)"}
        return {"started": True, "converting": True}
    ok, msg = LLAMA.start(prof, cmd_list, skip_vram_confirm=True)
    if not ok:
        return {"started": False, "error": msg}
    return {"started": True}




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
    # 对齐 C# BuildPluginSummary 的头部口径: 异常/市场停用/失效链接分开计数
    _abn = sum(1 for p in plugins if p["errors"] or p["missing_specs"] or p["dead_peers"]
               or (p["peer_issues"] and not p["exempted"]))
    _links = compat._link_issues(dsh_dir=dsh_dir)
    _mkt = 0
    try:
        _mp = os.path.join(compat.profile_dir("web"), ".dsh-market", "state.json")
        if os.path.isfile(_mp):
            with open(_mp, encoding="utf-8") as _fh:
                _mkt = len((json.load(_fh) or {}).get("disabled") or [])
    except Exception:
        _mkt = -1
    items = []
    for p in plugins:
        notes = []
        if p["peer_issues"]:
            notes.append("peer 不兼容: " + "; ".join("%s %s" % (x["dep"], x["range"]) for x in p["peer_issues"]))
        if p["dead_peers"]:
            notes.append("死依赖: " + ", ".join(p["dead_peers"]))
        if p.get("peer_unknown"):
            # DSH 目录没填/不是源码树: 只能报"未判定", 报"死依赖"是替用户下结论
            notes.append("依赖是否已被上游删除未判定(DSH 目录未指明或非源码树): "
                         + ", ".join(p["peer_unknown"]))
        if p["missing_specs"]:
            notes.append("缺依赖: " + ", ".join(p["missing_specs"]))
        if p["errors"]:
            notes.append("; ".join(p["errors"]))
        items.append({"name": p["name"], "note": "; ".join(notes) or p["state_label"],
                      "level": "error" if (p["errors"] or p["dead_peers"]) else
                               ("warn" if notes else "ok")})
    return {"report": {"items": items, "count": len(plugins)},
            "counts": {"abnormal": _abn, "market": _mkt, "links": len(_links)},
            "log": lines}


def api_search(body):
    which = body.get("target")
    if which == "llama":
        return {"result": updates.find_llama_server()}
    return {"result": updates.find_dsh_project()}


def api_check_update(body):
    which = body.get("target")
    d = (body.get("dir") or "").strip().strip('"')
    if which == "llama":
        local = updates.llama_local_version(d)
        probe = updates.llama_update_probe()
        if probe["build"] is None:
            # 以前只回"无法获取上游版本(网络?)", 而真因可能是镜像全挂/仓库无预编译包/解析不出
            # build 号 —— 把"查不到"说成"网络问题"会让人白等、白重启。
            return {"error": "无法获取上游版本: " + probe["error"], "cause": "upstream",
                    "hint": "上游探针与工具链走同一条链(GitHub API 直连+镜像, 带候选粘滞与冷却); "
                            "若显示候选近期全失败, 稍后自动恢复, 不必重启服务",
                    "local": local, "local_unknown": local is None}
        return {"local": local, "latest": probe["build"], "tag": probe["tag"],
                "up_to_date": local is not None and local >= probe["build"],
                "local_unknown": local is None,
                "local_hint": "" if local is not None else
                ("目录 %s 里跑不出 build 号(没装/不是 llama.cpp 目录?), 这次只能比出上游版本"
                 % (d or "(未填)"))}
    return updates.dsh_check_update(d)


def api_do_update(body):
    which = body.get("target")
    if which == "llama":
        d = (body.get("dir") or "").strip().strip('"')
        return {"job": run_job("llama-update", lambda jl: updates.llama_update(d, HUB.log, jl))}
    d = (body.get("dir") or "").strip().strip('"')
    ok, msg = updates.dsh_pull(d, HUB.log)
    return {"ok": ok, "msg": msg}


def api_job(body):
    """任务模型查询: {"id":"<id>"} → {"ok","id","name","status","log","result"}。

    字段与 Windows 侧 AgentJobJson 对齐(docs/API-CONTRACT.md「任务模型」): 分钟级操作只回
    `{"job":"<id>"}`, 由调用方轮询这里。id 不存在时按 ok:false + error 说清楚, 而不是回一个
    看似正常的 `status:"unknown"` —— 服务重启后旧 id 必然查不到, 含糊的形状会让人以为任务还在跑。
    """
    jid = str(body.get("id"))
    j = JOBS.get(jid)
    if j is None:
        return {"ok": False, "id": jid, "status": "unknown",
                "error": "未知任务 id: %s; 长操作先 POST 对应端点取 job id" % jid}
    return {"ok": j.get("status") != "error", "id": jid, "name": j.get("name"),
            "status": j.get("status"),
            "started_at": j.get("started"),
            "started_at_utc": _utc_iso(j.get("started")),
            "log": j.get("log", [])[-200:], "result": j.get("result")}


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
    # status 与 monitor 双路轮询: status 只回状态摘要(前端 pollStatus 只用这几个
    # 字段), 完整快照(12 条历史/事件流/slots)只走 /api/monitor
    return {
        "llama": {"status": LLAMA.status,
                  # running(bool) 与 status(字符串) 并存: Windows 侧 status 就是这个形状,
                  # 跨端脚本不必按平台分支(见 docs/API-CONTRACT.md「字段别名口径」)
                  "running": bool(LLAMA.running),
                  "pid": LLAMA.proc.pid if LLAMA.proc else None},
        "dsh": {"status": DSH.status, "running": bool(DSH.running), "pid": dsh_pid,
                "url": "http://127.0.0.1:%d" % DSH_PORT,
                "rebuilding": DSH.rebuild_running(),
                "converting": CONVERTER.running},
        "monitor": {"lightweight": MON.lightweight,
                    "llama_status": LLAMA.status, "dsh_status": DSH.status,
                    "breaker_triggered": MON.breaker_triggered,
                    "breaker_pct": MON.snapshot.get("vram", {}).get("pct", 0),
                    # 这帧显存/熔断数字的年龄: 轻量化休眠分支不重新采样, 不给出年龄就等于
                    # 把几小时前的显存当成"现在" (与"查不到 ≠ 知道"同一族)
                    "sampled_at": MON.snapshot.get("sampled_at"),
                    "sampled_at_utc": _utc_iso(MON.snapshot.get("sampled_at")),
                    "snapshot_age": (round(time.time() - MON.snapshot["sampled_at"], 1)
                                     if MON.snapshot.get("sampled_at") else None),
                    "vram": MON.snapshot.get("vram", {"used": 0, "total": 0, "pct": 0})},
        # profile 历来是"当前方案名"字符串(网页端依赖它, 不能改类型);
        # profile_name 是契约里的正式名, Windows 侧 profile 是对象 + profile_name 是字符串
        "profile": name,
        "profile_name": name,
        "lightweight": MON.lightweight,
        "dsh_stale_lock": DSH.stale_lock_present(),
    }




def api_logs(body):
    """运行日志读取: 游标(after)与尾部(n)两种口径同用一个端点, 另有 level/grep 过滤。

    Windows 侧是 GET /api/logs?n=200(纯尾部), Linux 侧历来是 POST {"after":N}(增量游标)。
    调用方(双端互查的脚本、cli)不该为同一个端点写两套分支, 所以两个参数都收:
    给了 n 就按"尾部 N 条"取(仍走同一个 seq 计数器, 返回结构不变: lines 带 seq/level/text),
    这样 n 与 after 也能叠用 = "游标之后的尾部 N 条"(与 Windows 的 after 分支同口径)。

    level(error/warn/info, 大小写不敏感;省略或 all = 不过滤)与 grep(子串, 大小写不敏感)
    必须先过滤再截尾部 N 条, 否则命中行会在过滤前就被截掉。
    boot / seq_first_available / gap 是给轮询客户端的: seq 每次进程启动都从 1 重来,
    客户端手上的 after 是上一轮的大数时会永久返回空且不报错 —— 比对 boot 就知道重启过了。
    """
    try:
        after = int(body.get("after") or 0)
    except (TypeError, ValueError):
        after = 0
    n = body.get("n")
    if n is None:
        limit = 400
    else:
        # 上限 2000 与 Windows 同口径(环形缓冲本身也只有 2000 条)
        try:
            limit = max(1, min(int(n), 2000))
        except (TypeError, ValueError):
            limit = 200
    want = str(body.get("level") or "").strip().upper()
    if want == "WARNING":                      # Windows 侧同口径: WARNING 归一成 WARN
        want = "WARN"
    needle = str(body.get("grep") or "")
    low = needle.lower()
    items, _ = HUB.since(after, HUB.RING_SIZE)   # 先拿游标之后的全部, 过滤后再截尾部
    kept = [it for it in items
            if (not want or want == "ALL" or it[1] == want)
            and (not needle or low in it[2].lower())]
    if len(kept) > limit:
        kept = kept[-limit:]
    first_avail = HUB.seq_first_available()
    return {"lines": [{"seq": s, "level": lv, "text": t} for s, lv, t in kept],
            "count": len(kept),
            "after": (kept[-1][0] if kept else after),
            "boot": HUB.boot,
            "ring": HUB.RING_SIZE,
            "seq_first_available": first_avail,
            # 游标落在现存缓冲之前 = 中间的行已被挤出环(重启另看 boot)
            "gap": after < first_avail - 1}


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


def _open_external(url):
    """交系统默认浏览器打开(仅 http/https), 返回是否成功交出去。

    "打开 Web" 这类动作必须由**服务端**做, 不能只靠前端 window.open ——
    主窗口是 WebKit2 客户端, 未处理 create 信号时 window.open 会被静默吞掉,
    表现就是"按钮点了没反应"(而在浏览器里打开同一页面时它又能用, 所以只改一边不够)。
    """
    if not url.startswith(("http://", "https://")):
        return False
    try:
        if webbrowser.open(url):
            return True
    except Exception:
        pass
    try:  # 无 $BROWSER/部分桌面下 webbrowser 会失败, xdg-open 兜底
        p = subprocess.Popen(["xdg-open", url], stdin=subprocess.DEVNULL,
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        time.sleep(0.6)
        return p.poll() is None or p.returncode == 0
    except Exception:
        return False


def api_open_dsh(_):
    """与 Windows 版 OpenDsh_Click 同语义: 由程序把 DSH 首页交系统默认浏览器。"""
    url = "http://127.0.0.1:%d" % DSH_PORT
    opened = _open_external(url)
    HUB.log("[DSH] %s Web: %s" % ("已交系统浏览器打开" if opened else "打开失败", url))
    return {"url": url, "opened": opened}


_SHUTDOWN_LATCH = threading.Lock()
_SHUTTING_DOWN = False
# 收尾看门狗: 必须比 stop() 的最坏合法路径(≈32s)长, 又必须比"用户能忍"短。
# 与 tray.py 兜底 SIGTERM 后的等待窗共用同一个数量级, 别让两侧互相误判。
_EXIT_WATCHDOG_S = 45


def _stop_all_and_exit(tag, stop_services=True):
    """停 llama.cpp 与 DSH、交代在途任务, 然后结束服务进程。

    **所有**退出路径都必须走这里: 以前 Ctrl+C 与 SIGTERM 直接杀进程, llama.cpp 与 DSH
    就成了没人管的孤儿, DSH 还留下 stale lock, 下次启动表现为撞端口"启动失败"。

    stop_services=False(只来自 /api/quit {"stop_services":false})时**不碰** llama.cpp 与
    DSH, 只结束服务自己 —— 与 Windows 侧 quit 同一口径。这时看门狗也不再预备补杀那些
    PID, 否则"不停服务退出"会变成"照样把它们杀掉"。

    幂等 + 看门狗(两条都是 review 找出来的):
    · 连按两次 Ctrl+C、托盘 45s 后补发的 SIGTERM、前端重复点"关闭服务并退出"都会二次
      进入这里; 两个收尾线程并发跑 kill + os._exit 会互相打断 —— 先退出的一方掐掉
      另一方正在进行的 SIGKILL, 恰好又造出孤儿。故第二次直接返回。
    · stop() 内部可能卡在 ss/lsof(各 10s)或坏掉的 IO 上, 那样进程永不退出, 托盘只能
      干等。故挂一个看门狗兜底(托盘那侧会按 PID 复核并把残留点名)。
    """
    global _SHUTTING_DOWN
    with _SHUTDOWN_LATCH:
        if _SHUTTING_DOWN:
            return
        _SHUTTING_DOWN = True
    # 停之前把 PID 抄下来: 看门狗到点时要**补杀**再退出, 不能只 os._exit。
    # (C# Window_Closing 9237-9246 同语义: "Shutdown 不等后台线程池", 退出前按记录
    #  的 PID 同步补杀; 只 _exit 等于把 stop() 打断在途的那一半留下当孤儿。)
    svc_pids = []
    if stop_services:
        for runner in (LLAMA, DSH):
            for handle in (getattr(runner, "proc", None), getattr(runner, "tracker", None)):
                pid = getattr(handle, "pid", None)
                if pid:
                    svc_pids.append(int(pid))

    def _watchdog_fire():
        try:
            HUB.log("[退出] ⚠ 收尾超过 %d 秒仍未完成, 补发 SIGKILL 并强制退出: %s"
                    % (_EXIT_WATCHDOG_S,
                       svc_pids or ("未记录 PID" if stop_services else "本次不停服务")))
            for pid in svc_pids:
                try:
                    victims = procman.child_pids(pid)
                except Exception:
                    victims = None
                if victims is None:       # 整树枚举不出来(root 属主): 只能点名根 PID
                    victims = []
                if pid not in victims:
                    victims.append(pid)
                for q in victims:
                    try:
                        os.kill(q, signal.SIGKILL)
                    except OSError:
                        pass
        except Exception:
            pass
        os._exit(1)

    # 看门狗预算必须**大于** stop() 的最坏合法路径: llama 侧 ss 查端口 10s + 杀树 3s,
    # DSH 侧杀树 3s + sudo kill 15s + 复核 0.6s ≈ 32s。以前取 30s 会在 sudo 还在途时
    # 抢先 os._exit —— 拦腰打断一次"正常但慢"的收尾, 恰好留下活着的 DSH。
    watchdog = threading.Timer(_EXIT_WATCHDOG_S, _watchdog_fire)
    watchdog.daemon = True
    watchdog.start()
    if stop_services:
        for name, runner in (("llama.cpp", LLAMA), ("DSH", DSH)):
            try:
                runner.stop()
            except Exception as ex:
                HUB.log("[%s] 停止 %s 失败: %s" % (tag, name, ex))
    else:
        HUB.log("[%s] 按要求**不**停止 llama.cpp 与 DSH: 它们继续运行, 由后续实例接管" % tag)
    try:
        with JOB_LOCK:                 # 直接迭代 JOBS 会和 run_job 的插入/pop 竞态
            snapshot = dict(JOBS)     # (RuntimeError 被下面吞掉 → 警告静默消失)
        busy = ["%s(#%s)" % (j["name"], k) for k, j in snapshot.items()
                if j["status"] == "running"]
    except Exception:
        busy = []
    # 插件安装/卸载/升级是**同步**跑在 HTTP 线程里的 pnpm 写操作, 不在 JOBS 里 ——
    # 而这恰恰是收尾时最危险的一种"在途"(把 package.json 写到一半就没了)。
    try:
        busy += compat.inflight_ops()
    except Exception:
        pass
    if busy:
        HUB.log("[%s] ⚠ 仍有任务在进行(%s); 后台任务以独立会话启动会自行跑完, "
                "但 pnpm 写依赖如果在中途被打断, 请到插件列表重新审计一次"
                % (tag, ", ".join(busy)))
    HUB.log("[%s] 服务进程退出" % tag)
    try:
        sys.stdout.flush()
    except Exception:
        pass
    watchdog.cancel()      # 正常收尾完成: 别让已经排好的看门狗在 os._exit 前抢跑
    os._exit(0)


def _quit_stop_services(body):
    """`stop_services` 的取值口径: **只有 JSON true 算 true**, 缺省也是 true。

    与 Windows 侧 AgentQuitOp 同一判定(`v.ValueKind == JsonValueKind.True`): 传 false 只结束
    服务进程, llama.cpp 与 DSH 继续跑(下次启动按 PID/端口接管)。之所以不收 `1`/`"true"`,
    是因为那样两端会得出相反结论(Windows 一律按 false)。
    """
    if isinstance(body, dict) and "stop_services" in body:
        return body["stop_services"] is True
    return True


def api_shutdown(body):
    """退出端点: 契约名是 `quit`, `shutdown` 是保留别名(旧托盘/脚本在用)。

    先把响应送达再动手(0.8s): 否则调用方只看到连接断开,
    分不清是"退出成功"还是"请求失败"。
    """
    stop = _quit_stop_services(body)
    HUB.log("[退出] 关闭服务并退出(stop_services=%s): %s"
            % ("true" if stop else "false",
               "停止 llama.cpp 与 DSH…" if stop else "两个服务保持运行…"))
    threading.Timer(0.8, _stop_all_and_exit, ("退出", stop)).start()
    return {"ok": True, "stopping": True, "stop_services": stop,
            "msg": "正在退出(stop_services=%s)" % ("true" if stop else "false")}


def api_convert_start(body):
    """独立触发 safetensors 转换(不自动启动)。"""
    d = (body.get("dir") or "").strip().strip('"')
    outtype = body.get("outtype") or "f16"
    ok = CONVERTER.start(d, outtype, lambda p: HUB.log("[转换] 结束: %s" % p))
    return {"ok": bool(ok), "error": None if ok else "转换未能启动(详见运行日志)"}


def api_browse(body):
    """服务端目录浏览(替代 WPF OpenFileDialog/FolderBrowserDialog)。"""
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
    return {"path": p, "dirs": dirs[:400], "files": files[:500],
            "files_truncated": len(files) > 500, "roots": drives}




def _mounts():
    try:
        with open("/proc/mounts") as f:
            for ln in f:
                parts = ln.split()
                # and 优先级高于 or: 长度守卫必须括住全部分支
                if len(parts) > 1 and (parts[1].startswith("/media") or parts[1] == "/" or parts[1].startswith("/home")):
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
    """监测页快照(3s 轮询)。打点 monitor_visible 以启用 3s 快速采样。

    打点同时记下时刻: 页面关掉时那句 visible:false 不一定发得出(卸载时的 fetch 不可靠),
    粘滞的 True 会让 30s 空转档与轻量化都进不去 —— 采样端按 8s 时效判定(见 Monitor._watched)。
    """
    MON.monitor_visible = bool(body.get("visible", True))
    MON.monitor_seen_at = time.time() if MON.monitor_visible else 0.0
    # 契约别名(不改 Linux 原有键, 只补): Windows 侧历史序列字典叫 hist、时间戳给 ISO-8601 UTC,
    # Linux 侧历来叫 hists、只给 epoch。跨端脚本要的是"同一个名字都能取到", 所以两个都发。
    # 浅拷贝: MON.snapshot 是监控线程反复替换的活动对象, 就地加键会与之竞态。
    out = dict(MON.snapshot)
    if "hists" in out and "hist" not in out:
        out["hist"] = out["hists"]
    ts = out.get("sampled_at")
    if ts and "sampled_at_utc" not in out:
        out["sampled_at_utc"] = _utc_iso(ts)
    return out


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
        return {"error": "查询失败(%s): %s" % (repo, ex), "cause": "upstream",
                "hint": "上游查询走同一条链: 直连 + 三个镜像, 上次成功的候选优先, 明确失败过的候选"
                        "先进冷却(传输类失败镜像 4 分钟 / 直连 60 秒, 端点级 4xx 只挡 45 秒)后自动恢复,"
                        "不必重启服务。"
                        "如果错误里写\u201c未找到可用的资产\u201d, 那是该仓库没有这个后端的预编译包,"
                        "与网络无关, 换个后端(变体)即可。"}
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
    """pnpm 会写 node_modules, 运行中拒绝(C# RequireDshStopped)。

    除了端口/cmdline 探测, 还要看服务自己手里的 DSH 句柄与状态: procman 的 cmdline
    探测带 10s 缓存(`_all_cmdlines`), 于是"DSH 刚 exec、端口还没监听"的窗口里两条外部
    探测都报假 —— pnpm 就可能在 DSH 启动/重建过程中改 node_modules。

    句柄判定必须走 `DSH.running`(poll 感知, 且 tracker 带 StartTime 校验), 不能只判
    "句柄不是 None": DSH 启动失败时 `_verify_loop` 会留下 proc 句柄, 接管来的 DSH 被
    外部杀掉后 tracker 也还在 —— 那样这个 guard 会**永久**拒绝插件操作, 用户只能先点
    一次"停止"才能解锁。
    """
    if DSH.running:
        return False
    if DSH.status in ("starting", "running", "running_admin", "rebuilding", "stop_failed"):
        return False
    if procman.find_process_by_port(DSH_PORT) or procman.node_host_running():
        return False
    if not procman.port_probe_ok():
        # 探针本身不可用(ss 与 lsof 都没有)时, find_process_by_port 只会返回 None ——
        # 那是"查不到", 不是"端口空闲"。这里取保守方向: 拒绝 pnpm 去写一个可能正在
        # 运行的 DSH 的 node_modules(与"宁可说不确定, 也不谎报空闲"一致)。
        return False
    return True


def api_plugins_audit(body):
    """插件管理器: 结构化逐插件审计(含锁定回写强制)。"""
    prof = body.get("profile") or {}
    dsh_dir = _dsh_dir_of(prof)
    writebacks = compat.enforce_pinned("web", HUB.log)
    plugins = compat.audit_plugins("web", dsh_dir)
    errs = sum(1 for p in plugins if p["errors"])
    warns = sum(1 for p in plugins if p["peer_issues"] or p["missing_specs"]
                or p["dead_peers"] or p.get("peer_unknown"))
    locked = sum(1 for p in plugins if p["locked"])
    ver = compat.get_dsh_version(dsh_dir)
    return {"plugins": plugins, "writebacks": writebacks,
            "summary": "DSH %s · 插件 %d 个 · 错误 %d · 警告 %d · 锁定停用 %d" % (
                ver or "?", len(plugins), errs, warns, locked),
            "dsh_running": not _dsh_running_guard()}


def _profile_name(_body):
    """这几个端点要的是 DSH 的 profile **名**, 而前端发上来的是整个 profile 对象。

    Linux 复刻只管理 DSH 的 web profile(audit_plugins/enforce_pinned/install_local_plugin
    也都写死 "web")。以前把 body["profile"](一个 dict)原样透传给 set_bundle_active/
    exempt_plugin → profile_dir 对 dict 抛 ValueError("非法 profile 名") → 插件列表里的
    激活/停用/豁免三个按钮全部 500, error.log 里留下整段栈。
    """
    return "web"


def api_plugins_set_active(body):
    ok, msg = compat.set_bundle_active(_profile_name(body),
                                       body.get("name") or "", bool(body.get("active")))
    return {"ok": ok, "msg": msg}


def api_plugins_exempt(body):
    ok, msg = compat.exempt_plugin(_profile_name(body),
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
    # dsh_dir 传上: 激活守卫读的审计里, 死依赖(dead_peers)判定需要 DSH 源码树位置
    # (peer 兼容本身只看 node_modules, 不依赖它)
    ok, msg = compat.install_local_plugin("web", body.get("source") or "", HUB.log,
                                          _dsh_dir_of(current_profile()[1]))
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
    # DSH 是分离会话启动的, **没有** exit 观察线程(C# 靠 PumpProcessStreams→OnDshDone):
    # 它自己崩掉/被外部杀掉后, status 会永远停在"运行中" —— 面板假在线, 且
    # _dsh_running_guard 从此永久拒绝所有插件操作。这里按 5s 一次把状态落回。
    if DSH.status.startswith("running") and not DSH.running:
        DSH.status = "stopped"
        DSH.tracker = None
        DSH.proc = None
        DSH._stop_tailer()
        state.clear_state_for("DshPid")
        HUB.log("--- DSH 进程已不在(非启动器停止), 状态已回落; 若不是您手动关掉的, "
                "请看 DSH 日志确认崩溃原因 ---")
    return {"ok": True}


_BODY_DICT_KEYS = ("profile", "config")
_BODY_STR_KEYS = ("name", "dir", "target", "url", "repo", "path", "source", "version", "outtype")


def _body_shape_error(body):
    """POST 请求体里已知字段的类型校验; 返回错误文案或 None。"""
    if not isinstance(body, dict):
        return "请求体必须是 JSON 对象(当前是 %s)" % type(body).__name__
    for k in _BODY_DICT_KEYS:
        v = body.get(k)
        if k in body and v is not None and not isinstance(v, dict):
            return "%s 必须是对象(当前是 %s)" % (k, type(v).__name__)
    for k in _BODY_STR_KEYS:
        v = body.get(k)
        if k in body and v is not None and not isinstance(v, str):
            return "%s 必须是字符串(当前是 %s)" % (k, type(v).__name__)
    return None


def _utc_iso(ts):
    """epoch 秒 → ISO-8601 UTC(毫秒精度, 末尾 Z), 与 Windows 侧 sampled_at_utc 同一族写法。

    时间戳两个口径都给: 只有 epoch 时人肉看响应要做换算, 只有 ISO 时脚本要解析字符串。
    ts 缺失/非法一律回空串而不是 None —— 前端直接把它拼进文本, None 会显示成 "null"。
    """
    if not ts:
        return ""
    try:
        dt = datetime.datetime.fromtimestamp(float(ts), datetime.timezone.utc)
        return dt.isoformat(timespec="milliseconds").replace("+00:00", "Z")
    except (TypeError, ValueError, OSError):
        return ""


def _with_ok(result):
    """响应出口收口: 发出去的 JSON 一律带 `ok`, 业务失败也是 HTTP 200 + ok:false。

    以前失败只带 {"error": ...}, 调用方分不清"没有这个字段"与"没有出错"; 现在跨端调用
    (cli / Windows↔Linux 互查 / webui)只看 ok 一个字段。这里只**增**字段, 不删也不改名,
    所以现存的 `if (r.error)` 判断(webui 里几十处)照旧成立。

    处理器自己写了 ok 就尊重它(例如 dsh/rebuild 明确回 ok:false + error); 否则按 error 的
    **值**推断: api_load_config 那种 {"error": null} 是"成功, 顺带说明一句", 应得 ok:true,
    所以不能按"有没有 error 这个键"判。
    不动原对象: MON.snapshot / CONFIG 这类是被其它线程反复读写的活动对象。
    """
    if isinstance(result, dict) and "ok" not in result:
        out = dict(result)
        out["ok"] = not result.get("error")
        return out
    return result


def _query_body(query):
    """GET 的 ?k=v&k2=v2 → 处理器要看的 dict(值一律是字符串)。

    Windows 侧 GET 处理器收到的就是去掉 '?' 的 query 串, Linux 侧处理器历来吃 POST 的 JSON
    对象。桥接时不做类型猜测: logs/job 这两个只读处理器自己会把 after/n/id 转 int/str。
    """
    out = {}
    for part in (query or "").split("&"):
        if not part:
            continue
        k, _, v = part.partition("=")
        if not k:
            continue
        out[k] = unquote(v)
    return out


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
    # quit 是契约里的正式端点名(与 Windows 侧同名同参数); shutdown 保留为别名,
    # 旧托盘/脚本还在 POST /api/shutdown, 删掉等于把它们打挂。
    ("quit",): api_shutdown,
    ("shutdown",): api_shutdown,
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

    def handle_error(self, request, client_address):
        """客户端中途断开(刷新页面/放弃慢请求)是常态: 静默, 别把栈打到 stderr。"""
        ex = sys.exc_info()[1]
        if isinstance(ex, (BrokenPipeError, ConnectionResetError)):
            return
        BaseHTTPRequestHandler.handle_error(self, request, client_address)

    def _send(self, code, body, ctype="application/json; charset=utf-8"):
        if not isinstance(body, bytes):
            # 出口处统一收口: 无论处理器写没写, 发出去的 JSON 一定有 ok(见 _with_ok)。
            # 业务失败一律 HTTP 200 + ok:false, 处理器抛异常才是 500 + ok:false。
            body = _with_ok(body)
        data = body if isinstance(body, bytes) else json.dumps(body, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(data)

    # Host 白名单: 挡跨站 POST(text/plain 表单能拼合法 JSON)与 DNS rebinding。
    # **GET 也必须过**: 否则 rebinding 场景(evil.example → 127.0.0.1)下攻击者页面是
    # 同源, 可直接读走 index.html 里注入的 API token —— 实测旧版 GET 完全不看 Host。
    _ALLOWED_HOSTS = ("127.0.0.1", "localhost", "::1")

    def _host_ok(self):
        h = (self.headers.get("Host") or "").strip()
        if h.startswith("["):
            h = h[1:].split("]")[0]         # IPv6 字面量写法是 [::1]:8788
        else:
            h = h.split(":")[0]             # 127.0.0.1:8788 / localhost:8788
        return h in self._ALLOWED_HOSTS

    # GET 侧受理的 API 端点。Windows 端 logs 是 GET ?n=200、job 是 GET ?id=3, Linux 端历来
    # 只有 POST; 两种写法都受理, 跨端脚本才不必按平台分支(POST 那半边一直是主力, webui 只用 POST)。
    # 除 ping/status 外一律要 token: GET 不鉴权等于把运行日志与任务输出降级成"本机任意页面可读"
    # —— POST 面当初就是因为同样的问题才加上 X-Auth 的。
    _GET_API = ("logs", "job")

    def _api_get(self, key):
        if key == "ping":
            # 探活/端口发现, 免 token(与 Windows 侧 ping 同口径)。Linux 端没有内置 cli,
            # cli 字段照样给但留空, 调用方可以无条件读这个键。
            return self._send(200, {"ok": True, "app": "llamadsh-launcher-linux", "cli": ""})
        if key == "status":
            # GET /api/status: 供客户端自举探测与轻量轮询使用(客户端 _probe 走 GET, 不带 token)
            try:
                return self._send(200, api_status({}))
            except Exception as ex:
                return self._send(500, {"ok": False, "error": str(ex)})
        if key not in self._GET_API:
            return self._send(404, {"ok": False, "error": "unknown api: " + key})
        if (self.headers.get("X-Auth") or "") != API_TOKEN:
            return self._send(401, {"ok": False, "error": "unauthorized"})
        try:
            result = ROUTES[(key,)](_query_body(self.path.partition("?")[2]))
        except Exception as ex:
            HUB.error_ex(ex, "API GET %s" % key)
            return self._send(500, {"ok": False, "error": str(ex)})
        return self._send(200, result)

    def do_GET(self):
        if not self._host_ok():
            return self._send(403, {"ok": False, "error": "forbidden host"})
        path = self.path.split("?")[0]
        if path in ("/", "/index.html"):
            return self._static_index()
        if path == "/favicon.ico":
            return self._send(204, b"", "image/x-icon")
        if path.startswith("/api/"):
            return self._api_get(path[len("/api/"):].strip("/"))
        safe = os.path.normpath(path.lstrip("/"))
        base = os.path.realpath(WEBUI_DIR)
        full = os.path.realpath(os.path.join(base, safe))
        # 必须按 realpath 比对再放行: 旧写法是 join 之后**不归一**直接 startswith, 于是
        # safe="../main.py" 时那个串照样以 webui/ 开头 —— 实测 GET /../main.py 与
        # /x/../../main.py 都返回 200 把服务端源码发出去。realpath 同时把 webui 里
        # 被投下的符号链接(指向树外)一并挡掉。
        if full != base and full.startswith(base + os.sep) and os.path.isfile(full):
            return self._static(full)
        self._send(404, {"ok": False, "error": "not found"})

    def _static(self, full):
        ext = os.path.splitext(full)[1]
        try:
            with open(full, "rb") as f:
                self._send(200, f.read(), MIME.get(ext, "application/octet-stream"))
        except OSError:
            self._send(404, {"ok": False, "error": "not found"})

    def _static_index(self):
        """index.html + 注入本次启动的 API token(app.js 统一带 X-Auth 头)。"""
        try:
            with open(os.path.join(WEBUI_DIR, "index.html"), encoding="utf-8") as f:
                html = f.read()
        except OSError:
            return self._send(404, {"ok": False, "error": "not found"})
        inject = "<script>window.__DSH_TOKEN=%s;</script>" % json.dumps(API_TOKEN)
        if "</head>" in html:
            html = html.replace("</head>", inject + "</head>", 1)
        else:
            html = inject + html
        self._send(200, html.encode("utf-8"), MIME[".html"])

    def do_POST(self):
        path = self.path.split("?")[0].strip("/")
        if path.startswith("api/"):
            path = path[4:]
        try:
            n = int(self.headers.get("Content-Length") or 0)
            if n > 10_000_000:
                return self._send(413, {"ok": False, "error": "payload too large"})
            raw = self.rfile.read(n) if n else b""
            body = json.loads(raw.decode("utf-8")) if raw else {}
        except Exception:
            return self._send(400, {"ok": False, "error": "bad request"})
        # CSRF: 浏览器可跨站提交 text/plain 表单拼合法 JSON 驱动全部 POST API;
        # 校验 Host 白名单(DNS rebinding 同时被挡)。与 GET 用同一个 _host_ok,
        # 免得两半白名单各自漂移(IPv6 字面量 [::1]:8788 以前在 POST 侧会被误拒)。
        if not self._host_ok():
            return self._send(403, {"ok": False, "error": "forbidden host"})
        # 鉴权: 本机任意进程此前可无凭证驱动全部 POST(启停进程/装插件/浏览全盘)
        if (self.headers.get("X-Auth") or "") != API_TOKEN:
            return self._send(401, {"ok": False, "error": "unauthorized"})
        fn = ROUTES.get((path,))
        if fn is None:
            return self._send(404, {"ok": False, "error": "unknown api: " + path})
        # 形状校验: 字段类型错在旧版会走到 prof.get(...)/.strip() → AttributeError → 500,
        # 并把整段 traceback 写进 error.log(排障时看着像服务自己坏了)。类型错就当类型错
        # 处理: 明确 400 说是哪个字段、发成了什么。
        bad = _body_shape_error(body)
        if bad:
            return self._send(400, {"ok": False, "error": bad})
        try:
            result = fn(body)
        except Exception as ex:
            HUB.error_ex(ex, "API %s" % path)
            code, result = 500, {"ok": False, "error": str(ex)}
        else:
            code = 200
        try:
            self._send(code, result)
        except (BrokenPipeError, ConnectionResetError):
            # 客户端刷新页面/放弃长请求(如 check_update 走镜像慢)→ 管道断了。
            # 这不是服务端故障, 打 ERROR + 栈只会污染 error.log
            HUB.log("[API] %s: 客户端已断开(响应未送达)" % path)


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
        # "a" 模式: 第二实例 flock 失败即退出, 但 open("w") 已截断运行实例写的 pid
        _lockfh = open(LOCK_FILE, "a")
        fcntl.flock(_lockfh, fcntl.LOCK_EX | fcntl.LOCK_NB)
        # 拿到锁后清空旧 pid 再写本次的, 残留旧 pid 不再误导排障
        _lockfh.truncate(0)
        _lockfh.write(str(os.getpid()))
        _lockfh.flush()
    except OSError:
        print("已经在运行中(锁文件 %s)。如确认未运行,请删除该文件。" % LOCK_FILE)
        sys.exit(1)

    _write_token_file()
    # 状态写盘失败要有日志出口: LlamaPid/DshPid 落不了盘时, 下次启动就接管不到进程,
    # 面板显示"未运行", 用户点启动变成"端口已被占用/启动失败" —— 真因必须看得见。
    state.set_error_sink(HUB.log)
    toolchain.cleanup_update_tmp()   # 上次崩溃留下的 .part/解压目录, 启动时清掉
    takeover_on_boot()
    monitor.prewarm_disks()          # 踢一次磁盘行首刷, 否则第一帧面板显示成"没有盘"
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
    # 绑定成功之后才写发现文件: 端口被占会顺延(port + i), 写默认端口等于骗调用方。
    _write_agent_file(port)
    url = "http://127.0.0.1:%d" % port
    print("LlamaDSH Launcher(Linux 复刻版)已启动")
    print("主窗口: %s" % url)
    print("Llama 服务端口: 1234(默认) | DSH Web: http://127.0.0.1:%d" % DSH_PORT)
    applog.info("Web UI: %s" % url)
    try:
        webbrowser.open(url)
    except Exception:
        pass
    # SIGTERM/Ctrl+C 也要先停 llama.cpp 与 DSH: 以前直接进程了事, 两者变成孤儿进程,
    # DSH 的 lock 文件还留着(stale lock), 下次启动会撞端口误报"启动失败"。
    # 收尾放独立线程: 信号处理器运行在 serve_forever 所在的线程里, 那里 stop() 的
    # 等待与日志不该阻塞解释器状态。
    def _on_term(signum, _frame):
        tag = "收到 SIGINT" if signum == signal.SIGINT else "收到 SIGTERM"
        try:
            HUB.log("[%s] 开始收尾" % tag)
        except Exception:
            pass
        threading.Thread(target=_stop_all_and_exit, args=(tag,), daemon=True).start()

    for sig in (signal.SIGTERM, signal.SIGINT):
        try:
            signal.signal(sig, _on_term)
        except (ValueError, OSError) as ex:
            # 挂不上就等于整条"收到信号 → 停 llama/DSH → 退出"的收尾链**不存在**: 服务被
            # kill 或注销桌面会话时两者变孤儿, DSH 还留 stale lock(正是这段代码原本要修的
            # 故障)。以前静默 pass, 事后连"有没有收到过信号"都看不出来。
            applog.error("[收尾] 信号处理器注册失败(%s: %s) —— kill/注销不会走收尾, "
                         "llama.cpp 与 DSH 会变孤儿" % (type(ex).__name__, ex))
            print("⚠ 无法注册 SIGTERM/SIGINT 收尾处理器(%s): 结束服务时不会停止 llama.cpp/DSH" % ex)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        _stop_all_and_exit("收到 Ctrl+C")


def _health_loop():
    last_err = 0.0
    while True:
        try:
            api_health({})
        except Exception as ex:
            # 这条 5s 对账是"DSH 崩了 / llama 短命退出"唯一的状态回落点: 它一断, 面板就
            # 永远显示运行中, 插件操作也被 _dsh_running_guard 永久拒绝。以前连"对账本身
            # 在报错"都看不见, 只能看到用户报的"明明没跑却装不了插件"。限流 60s 以免刷盘。
            if time.time() - last_err > 60.0:
                last_err = time.time()
                HUB.error_ex(ex, "健康对账异常(此后每 60s 最多报一次, 对账仍在继续)")
        time.sleep(5)


if __name__ == "__main__":
    main()
