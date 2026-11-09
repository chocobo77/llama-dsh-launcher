# -*- coding: utf-8 -*-
"""AppLog — 与原版 Logging.cs 等价的应用日志。

日志目录 ~/.dsh_launcher_logs/{info,run,error,crash}.log
每行格式 [yyyy-MM-dd HH:mm:ss] 内容;所有 IO 吞异常。
单文件超过 MAX_BYTES 滚动出 <file>.1/.2/.3(保留 3 份历史), 与 Windows 侧同口径。
落盘一律先过 redact(): DSH 启动横幅会把 `dsh web: http://127.0.0.1:3080/?token=…`
原样打出来, 不脱敏就会同时进 run.log 和 agent 的 logs 响应。
"""
import os
import re
import sys
import threading
import datetime

LOG_DIR = os.path.join(os.path.expanduser("~"), ".dsh_launcher_logs")

# 与 Windows 侧 Logging.cs 的 MaxBytes/KeepBackups 同口径
MAX_BYTES = 4 * 1024 * 1024
KEEP_BACKUPS = 3

_lock = threading.Lock()
_write_warned = False

# 凭据的三种落盘形态(URL 查询串 / JSON 字段 / Authorization 头)
_TOKEN_QUERY = re.compile(r"([?&]token=)[^&\s\"'<>|]+", re.IGNORECASE)
_TOKEN_JSON = re.compile(r'("token"\s*:\s*")[^"]*(")', re.IGNORECASE)
_BEARER = re.compile(r"(Bearer\s+)[A-Za-z0-9+_\-/=.]{12,}", re.IGNORECASE)
_MASK = "‹已隐藏›"


def redact(text: str) -> str:
    """抹掉日志文本里的访问凭据, 三种形态各替换为 ‹已隐藏›。

    只改日志文本, 不影响真正用来打开浏览器/鉴权的那个 URL 字符串。
    """
    if not text:
        return text or ""
    out = _TOKEN_QUERY.sub(r"\1" + _MASK, text)
    out = _TOKEN_JSON.sub(r"\1" + _MASK + r"\2", out)
    return _BEARER.sub(r"\1" + _MASK, out)


def _rotate(path: str):
    """超过 MAX_BYTES 时滚动: 当前→.1, 旧的 .1→.2、.2→.3, 最老的 .3 丢弃。

    必须在 append 之前做(之后再查大小会把本次写入算进上限)。失败(文件被别的
    实例占用等)只忽略, 绝不影响本次日志写入。
    """
    try:
        if os.path.getsize(path) <= MAX_BYTES:
            return
        try:
            os.remove(path + "." + str(KEEP_BACKUPS))
        except OSError:
            pass
        for i in range(KEEP_BACKUPS - 1, 0, -1):
            src = path + "." + str(i)
            if os.path.exists(src):
                os.replace(src, path + "." + str(i + 1))
        os.replace(path, path + ".1")
    except Exception:
        pass


def _write(fname: str, text: str):
    global _write_warned
    try:
        os.makedirs(LOG_DIR, exist_ok=True)
        ts = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        path = os.path.join(LOG_DIR, fname)
        with _lock:
            _rotate(path)
            with open(path, "a", encoding="utf-8", errors="replace") as f:
                f.write("[%s] %s\n" % (ts, redact(text)))
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
