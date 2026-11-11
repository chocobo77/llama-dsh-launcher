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


_LOG_FILES = ("run.log", "info.log", "error.log", "crash.log")

# 字节级形态, 与上面三个文本模式一一对应。脱敏上线前的历史日志要整文件重写一遍，
# 而**按文本读写会永久损坏非 UTF-8 字节**：errors="replace" 把非法字节换成 U+FFFD，
# 老日志里被捕获的子进程输出(GBK 的控制台输出、被截断的多字节字符)就再也回不来了
# (2026-11-11 review 实测: \xff\xfe 重写后变成 ef bf bd)。要改就按字节改 —— 凭据本身
# 全是 ASCII，模式在字节上同样成立。
_TOKEN_QUERY_B = re.compile(rb"([?&]token=)[^&\s\"'<>|]+", re.IGNORECASE)
_TOKEN_JSON_B = re.compile(rb'("token"\s*:\s*")[^"]*(")', re.IGNORECASE)
_BEARER_B = re.compile(rb"(Bearer\s+)[A-Za-z0-9+_\-/=.]{12,}", re.IGNORECASE)
_MASK_B = _MASK.encode("utf-8")


def _redact_bytes(data: bytes) -> bytes:
    """与 redact() 同语义，但在字节上做，不动任何其它字节。"""
    out = _TOKEN_QUERY_B.sub(lambda m: m.group(1) + _MASK_B, data)
    out = _TOKEN_JSON_B.sub(lambda m: m.group(1) + _MASK_B + m.group(2), out)
    return _BEARER_B.sub(lambda m: m.group(1) + _MASK_B, out)


def scrub_existing() -> int:
    """把已存在的历史日志整文件重过一遍 redact, 抹掉脱敏上线之前落盘的凭据。

    脱敏是后加的: 老日志里可能还躺着明文 `?token=…`/`Bearer …`。启动时扫一遍
    run/info/error/crash 及其 .1/.2/.3 备份, 就地重写(临时文件 + os.replace, 原子)。
    返回改动的文件数。任何单个文件的失败都吞掉, 不影响其余, 更不影响启动。

    必须按字节读写(见上面 _redact_bytes 的注释): 文本模式 round-trip 会把历史日志里
    的非法 UTF-8 字节换成 U+FFFD, 那是不可恢复的内容损失。
    """
    changed = 0
    try:
        names = os.listdir(LOG_DIR)
    except OSError:
        return 0
    for base in _LOG_FILES:
        for name in names:
            if name != base and not name.startswith(base + "."):
                continue
            path = os.path.join(LOG_DIR, name)
            try:
                with open(path, "rb") as f:
                    data = f.read()
                clean = _redact_bytes(data)
                if clean == data:
                    continue
                tmp = path + ".scrub.tmp"
                with open(tmp, "wb") as f:
                    f.write(clean)
                os.replace(tmp, path)
                changed += 1
            except Exception:
                try:
                    os.remove(path + ".scrub.tmp")
                except OSError:
                    pass
                continue
    return changed
