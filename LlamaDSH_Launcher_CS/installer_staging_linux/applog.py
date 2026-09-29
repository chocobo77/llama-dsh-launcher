# -*- coding: utf-8 -*-
"""AppLog — 与原版 Logging.cs 等价的应用日志。

日志目录 ~/.dsh_launcher_logs/{info,run,error,crash}.log
每行格式 [yyyy-MM-dd HH:mm:ss] 内容;所有 IO 吞异常。
"""
import os
import threading
import datetime

LOG_DIR = os.path.join(os.path.expanduser("~"), ".dsh_launcher_logs")

_lock = threading.Lock()


def _write(fname: str, text: str):
    try:
        os.makedirs(LOG_DIR, exist_ok=True)
        ts = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        with _lock:
            with open(os.path.join(LOG_DIR, fname), "a", encoding="utf-8", errors="replace") as f:
                f.write("[%s] %s\n" % (ts, text))
    except Exception:
        pass


def info(text: str):
    _write("info.log", text)


def run(text: str):
    _write("run.log", text)


def error(text: str):
    _write("error.log", text)


def crash(text: str):
    _write("crash.log", text)


def error_ex(ex: Exception, ctx: str):
    import traceback
    _write("error.log", "%s: %s: %s\n%s" % (ctx, type(ex).__name__, ex, traceback.format_exc()))
