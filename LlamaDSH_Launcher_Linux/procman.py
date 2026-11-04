# -*- coding: utf-8 -*-
"""进程管理:杀进程树、按端口找进程、PID 复用防护(ProcessTracker)、孤儿 DSH 搜索。

等价原版 ProcessHelper / ProcessTracker,以 /proc + ss 实现。
"""
import os
import re
import signal
import subprocess
import time
import glob


def read_cmdline(pid):
    try:
        with open("/proc/%d/cmdline" % pid, "rb") as f:
            return f.read().decode("utf-8", "replace").replace("\x00", " ").strip()
    except Exception:
        return ""


def process_name(pid):
    try:
        with open("/proc/%d/comm" % pid) as f:
            return f.read().strip()
    except Exception:
        return ""


def process_starttime(pid):
    """字段 22(1-based)= starttime,用于 PID 复用防护。"""
    try:
        with open("/proc/%d/stat" % pid) as f:
            data = f.read()
        rp = data.rfind(")")
        fields = data[rp + 2:].split()
        return int(fields[19])
    except Exception:
        return -1


def alive(pid):
    if not pid:
        return False
    try:
        os.kill(int(pid), 0)
        return True
    except PermissionError:
        return True
    except OSError:
        return False


def child_pids(pid, max_depth=16):
    """WMI ParentProcessId 递归的 /proc 等价;返回含自身 pid 的列表(叶子在前)。

    **枚举不了时返回 None, 不是 []**: `/proc/<pid>/task/<pid>/children` 只对同 uid 可读,
    所以 root 属主的 DSH/llama 树会读到空。以前把"读不到"当成"确实没有子进程", 于是只杀
    根 PID 就宣称"已停止", 实际 node/pnpm 还占着 3080(与"停止失败被说成已停止"同族缺陷)。
    调用方要把"没数清"如实说出去。
    """
    root = int(pid)
    out, frontier = [], [root]
    seen = {root}
    depth = 0
    enumerated = False
    while frontier and depth < max_depth:
        nxt = []
        for p in frontier:
            try:
                with open("/proc/%d/task/%d/children" % (p, p)) as f:
                    kids = [int(x) for x in f.read().split()]
                if p == root:
                    enumerated = True
            except Exception:
                kids = []
            for k in kids:
                if k not in seen:
                    seen.add(k)
                    out.append(k)
                    nxt.append(k)
        frontier = nxt
        depth += 1
    if not enumerated:
        return None
    out.append(root)
    out.reverse()  # 叶子先死
    return out


def kill_process_tree(pid, timeout=3.0):
    """先杀叶子后杀父;SIGTERM → SIGKILL。

    pid 为 None(端口查不到属主 / 进程早已自行退出)时必须直接返回 False: 以前
    except 分支里的 `pids = [int(pid)]` 会对 None 再抛一次同一个 TypeError, 异常
    逃出 stop() → 上层把"其实没事"记成"停止失败", 其后的状态清理也被跳过。
    """
    if pid is None:
        return False
    tree_certain = True
    try:
        pids = child_pids(pid)
    except Exception:
        pids = None
    if pids is None:          # 数不清整树(root 属主 / hidepid): 只能保证打到根 PID
        pids = [int(pid)]
        tree_certain = False
    for p in pids:
        try:
            os.kill(p, signal.SIGTERM)
        except OSError:
            pass
    deadline = time.time() + timeout
    while time.time() < deadline:
        if not any(alive(p) for p in pids):
            break
        time.sleep(0.1)
    for p in pids:
        try:
            os.kill(p, signal.SIGKILL)
        except OSError:
            pass
    # True 只表示"**确认**整树都不在了": 树没能枚举时即使根已死也返回 False, 让上层落在
    # "未能确认已停止"(面板保持 Stop 可点、Start 不亮)而不是谎报"已停止"。
    return (not any(alive(p) for p in pids)) and tree_certain


_probe_cache = {"at": 0.0, "ok": False}


def port_probe_ok():
    """端口探针(ss / lsof)是否可用。不可用时 find_process_by_port 一律返回 None,
    那是**查不到**, 不是"端口空闲" —— 拿它做决策的地方要把方向取保守的一侧。
    (实测本机两者都在; 这里防的是精简系统/PATH 被改窄时把"运行中"看成"没在跑"。)
    """
    now = time.time()
    if now - _probe_cache["at"] < 10.0:
        return _probe_cache["ok"]
    ok = bool(which("ss") or which("lsof"))
    _probe_cache["at"], _probe_cache["ok"] = now, ok
    return ok


def find_process_by_port(*ports, name_filter=None):
    """ss -ltnp 端口属主探测。默认不过滤进程名(C# d227f3c 同语义):
    Node 24 主线程 comm 已改为 "MainThread", 按 "node" 过滤会永远查不到,
    导致该接管的没接管、重启撞 EADDRINUSE 误报启动失败。"""
    try:
        out = subprocess.run(["ss", "-ltnp"], capture_output=True, text=True, timeout=10).stdout
    except Exception:
        out = ""
    for line in out.splitlines():
        m = re.search(r":(\d+)\s+.*?pid=(\d+)", line)
        if not m:
            continue
        port, pid = int(m.group(1)), int(m.group(2))
        if port in ports:
            if not name_filter:
                return pid
            pname = process_name(pid)
            if pname in name_filter or pname + ".exe" in name_filter:
                return pid
    if not name_filter:
        # 不限名时退回 lsof
        for port in ports:
            try:
                out = subprocess.run(["lsof", "-t", "-i", ":%d" % port, "-sTCP:LISTEN"],
                                     capture_output=True, text=True, timeout=10).stdout
                for ln in out.split():
                    try:
                        return int(ln)
                    except ValueError:
                        pass
            except Exception:
                pass
    return None


class ProcessTracker:
    """记录 PID + StartTime;Poll() 返回 None=仍活,-1=消失或 PID 复用。"""

    def __init__(self, pid):
        self.pid = int(pid)
        self.start_time = process_starttime(self.pid)

    def poll(self):
        if not alive(self.pid):
            return -1
        if process_starttime(self.pid) != self.start_time:
            return -1
        return None


_DSH_NAME_HINTS = ("deepseek-harness", "@deepseek-ai/dsh", "apps/cli")
_dsh_orphan_cache = {"t": 0.0, "v": []}
_cmdline_cache = {"t": 0.0, "v": {}}


def _all_cmdlines():
    now = time.time()
    if now - _cmdline_cache["t"] < 10:
        return _cmdline_cache["v"]
    res = {}
    for pdir in glob.glob("/proc/[0-9]*"):
        try:
            pid = int(os.path.basename(pdir))
        except ValueError:
            continue
        if process_name(pid) in ("node", "electron"):
            res[pid] = read_cmdline(pid)
    _cmdline_cache["t"] = now
    _cmdline_cache["v"] = res
    return res


def find_orphaned_dsh_processes(dsh_dir, cache_secs=15):
    """按优先级打分排序的孤儿 node 进程(原版同款打分)。"""
    now = time.time()
    if now - _dsh_orphan_cache["t"] < cache_secs:
        return _dsh_orphan_cache["v"]
    dsh_dir = (dsh_dir or "").strip().strip('"')
    cands = []
    for pid, cl in _all_cmdlines().items():
        if "pnpm.mjs" in cl:
            continue
        low = cl.lower()
        score = 0
        # 强特征(C# 4bf9416 同语义): 裸 "tsx" 不命中——任何 tsx dev server
        # 都会被误认成孤儿 DSH 而误杀
        if re.search(r"cli/src/bin|apps/cli|@deepseek-ai", low):
            score = max(score, 5)
        if "deepseek-harness" in low:
            score = max(score, 4)
        if "dsh" in low and re.search(r"web|dist|cli|server|start", low):
            score = max(score, 3)
        if dsh_dir and (dsh_dir.lower() in low):
            score = max(score, 2)
        if score > 0:
            cands.append((score, pid))
    cands.sort(key=lambda x: -x[0])
    result = [pid for _, pid in cands]
    _dsh_orphan_cache["t"] = now
    _dsh_orphan_cache["v"] = result
    return result


def node_host_running():
    """DshMaintenance.NodeHostRunning:node 进程 cmdline 含 DSH 标志。"""
    for pid, cl in _all_cmdlines().items():
        if any(h.lower() in cl.lower() for h in _DSH_NAME_HINTS):
            return True
    return False


def which(exe):
    for d in os.environ.get("PATH", "").split(os.pathsep):
        p = os.path.join(d, exe)
        if os.path.isfile(p) and os.access(p, os.X_OK):
            return p
    return None
