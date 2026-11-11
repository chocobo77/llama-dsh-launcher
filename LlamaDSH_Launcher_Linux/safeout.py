# -*- coding: utf-8 -*-
"""让"往控制台打中文/符号"永远不会成为致命错误。

实测(2026-11-11): stdout 不是 UTF-8 时(GBK 控制台，或 Linux 上 LANG=C 且没有 C.UTF-8
导致 PEP 538 回退成 ASCII)，一个 `print("⚠ …")` 会抛 UnicodeEncodeError。整个文件本来就
到处 print 中文，但**新加的那些警告**是"提醒后继续运行"的路径 —— 崩溃反而把降级路径
变成了启动失败(例如查不到用户家目录时本应保持当前身份继续跑)。

这里只把 errors 策略改掉(遇到打不出的字符按原样转义)，不改编码、不改任何文案。
"""
import sys


def fix_streams() -> None:
    """把 stdout/stderr 的编码错误策略设为 backslashreplace，幂等且永不抛。"""
    for name in ("stdout", "stderr"):
        stream = getattr(sys, name, None)
        if stream is None:
            continue
        try:
            reconfigure = getattr(stream, "reconfigure", None)
            if reconfigure is not None:
                reconfigure(errors="backslashreplace")
        except Exception:
            # 流被替换成别的东西、或底层不允许 reconfigure: 保持原状即可，
            # 这里失败绝不能反过来影响启动。
            pass
