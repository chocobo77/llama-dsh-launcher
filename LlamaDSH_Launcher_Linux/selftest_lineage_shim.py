#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""在 Windows 上跑 selftest.py 的 §31/§32 两段(血统重启、家目录、脱敏、输出容错、profile)。

为什么要有这个文件：§31/§32 是**纯逻辑**断言(假 execvp、假 user_home、临时日志目录)，本身不需要
Linux，但 selftest.py 整体在 Windows 上跑不起来(其它节要 /proc、GTK、POSIX 信号)。以前只有
作者本机临时搭的 shim 能跑这两段，仓库里没有可复现的入口，别人(和以后的我)只能"相信它跑过"。

做法：从 selftest.py 里按小节标题切出源码文本，补上最小的 POSIX 件(fcntl / pwd / geteuid 等)，
在同一个命名空间里 exec。这不是完整自检，也不替代 Linux 上跑 `python3 selftest.py`。

跑法(Windows，在仓库根或本目录下均可)：
    python LlamaDSH_Launcher_Linux\\selftest_lineage_shim.py
退出码 0 = 两段的断言全过。
"""
import io
import os
import shutil
import sys
import tempfile
import types

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

# --- Windows 上缺的 POSIX 件: 只补够这两段用的最小面 ------------------------------------
if not hasattr(os, "geteuid"):
    os.geteuid = lambda: 0
if not hasattr(os, "getuid"):
    os.getuid = lambda: 1000
if not hasattr(os, "getgid"):
    os.getgid = lambda: 1000

_fake_fcntl = types.ModuleType("fcntl")
_fake_fcntl.flock = lambda *a, **k: None
_fake_fcntl.LOCK_EX = 2
_fake_fcntl.LOCK_NB = 4
sys.modules.setdefault("fcntl", _fake_fcntl)

if "pwd" not in sys.modules:
    # §31 里那条"查 passwd"的断言要一个真实的 passwd 条目。这里给一个自洽的假条目：
    # 用户名与家目录都由本机临时目录构成，断言查的是"user_home 返回值 == passwd 里的值"，
    # 不依赖具体的用户名。
    _fake_pwd = types.ModuleType("pwd")
    _shim_home = os.path.realpath(tempfile.mkdtemp(prefix="shimpwd_"))

    class _struct_passwd(object):
        pw_name = "shimuser"
        pw_dir = _shim_home

    _fake_pwd.getpwuid = lambda uid: _struct_passwd()
    _fake_pwd.getpwnam = lambda name: _struct_passwd() if name == "shimuser" else None
    sys.modules["pwd"] = _fake_pwd
else:
    _shim_home = None

# 这两段引用到的 selftest 侧名字
PASS, FAILS = [], []


def check(name, cond, detail=""):
    if cond:
        PASS.append(name)
        print("PASS  %s" % name)
    else:
        FAILS.append(name)
        print("FAIL  %s   => %s" % (name, detail))


_selftest_src = io.open(os.path.join(HERE, "selftest.py"), encoding="utf-8").read()


def _slice(start_marker, end_marker):
    """按小节标题切源码: 从标题所在行的行首开始, 到结束标记之前结束。

    标记用标题文字而不是那行分隔线的破折号数量 —— 后者一改就对不上(踩过)。
    """
    i = _selftest_src.find(start_marker)
    if i < 0:
        raise SystemExit("selftest.py 里找不到小节起始标记: %r" % start_marker)
    i = _selftest_src.rfind("\n", 0, i) + 1
    j = _selftest_src.find(end_marker, i)
    if j < 0:
        raise SystemExit("selftest.py 里找不到小节结束标记: %r" % end_marker)
    return _selftest_src[i:j]


_ns = {
    "__name__": "selftest_lineage_sections",
    # §31/§32 里用 os.path.dirname(os.path.abspath(__file__)) 定位被测源码，
    # 必须指向 selftest.py 本身，否则读到的源文件断言会指错地方。
    "__file__": os.path.join(HERE, "selftest.py"),
    "io": io, "os": os, "sys": sys, "shutil": shutil, "tempfile": tempfile,
    "check": check, "FAILS": FAILS,
    "_SELFTEST_HOME": tempfile.mkdtemp(prefix="shim_home_"),
    "_tmp": tempfile.mkdtemp(prefix="shim_tmp_"),
}
import applog as _applog_top  # noqa: E402

_ns["_applog_top"] = _applog_top
# 自检绝不真去 exec sudo(见 selftest.py 顶部同一条)
os.environ.setdefault("LlamaDSH_NO_LINEAGE_RESET", "1")

for _marker_pair in (("31. 血统自我重启一次封顶", "32. 启动顺序"),
                     ("32. 启动顺序", "shutil.rmtree(_SELFTEST_HOME")):
    _code = _slice(*_marker_pair)
    try:
        exec(compile(_code, os.path.join(HERE, "selftest.py#section"), "exec"), _ns)
    except Exception as _ex:  # 段落自己崩了也要给出可辨认的结论
        check("小节 %r 执行未抛" % _marker_pair[0], False, "%s: %s" % (type(_ex).__name__, _ex))

for _d in (_ns["_SELFTEST_HOME"], _ns["_tmp"], _shim_home):
    if _d:
        shutil.rmtree(_d, ignore_errors=True)

print()
print("SHIM SUMMARY PASS=%d FAIL=%d" % (len(PASS), len(FAILS)))
if FAILS:
    print("FAILED: %s" % FAILS)
    sys.exit(1)
sys.exit(0)
