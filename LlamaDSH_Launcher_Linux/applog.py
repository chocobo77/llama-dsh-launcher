# -*- coding: utf-8 -*-
"""AppLog — 与原版 Logging.cs 等价的应用日志。

日志目录 ~/.dsh_launcher_logs/{info,run,error,crash}.log
每行格式 [yyyy-MM-dd HH:mm:ss] 内容;所有 IO 吞异常。
"""
import os
import sys
import threading
import datetime

LOG_DIR = os.path.join(os.path.expanduser("~"), ".dsh_launcher_logs")

_lock = threading.Lock()
_write_warned = False


def _write(fname: str, text: str):
    global _write_warned
    try:
        os.makedirs(LOG_DIR, exist_ok=True)
        ts = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        with _lock:
            with open(os.path.join(LOG_DIR, fname), "a", encoding="utf-8", errors="replace") as f:
                f.write("[%s] %s\n" % (ts, text))
    except Exception as ex:
        # 日志是这四个文件的全族地基: HOME 只读/错位/盘满时它们会**整体静默归零**,
        # 于是"到底有没有异常发生"都无从判断(2026-11-05 排障时被这一点坑过)。
        # 至少要往 stderr 说一次, 且只说一次, 免得真刷屏时反而看不清。
        if not _write_warned:
            _write_warned = True
            try:
                sys.stderr.write("[applog] ⚠ 日志写入失败(%s: %s), 目录 %s;"
                                 " 后续同类失败不再提示\n" % (type(ex).__name__, ex, LOG_DIR))
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
