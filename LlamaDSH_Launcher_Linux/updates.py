# -*- coding: utf-8 -*-
"""自动检索(FileSearcher 精简版)与更新服务(UpdateService)。

- 检索:PATH → 常见目录 → 受限 BFS(启发式打分,超时停止)。
- llama.cpp 更新:GitHub Releases,优先 vulkan-x64 的 ubuntu 包,三镜像回退。
- DSH 更新:git fetch + ahead/behind,--ff-only 拉取。
"""
import os
import re
import io
import glob
import json
import time
import zipfile
import shutil
import subprocess

import urllib.request

import procman
import applog

SEARCH_DEADLINE = 45
_DIR_BLACKLIST = {"$recycle.bin", "windows", "node_modules", ".git", "__pycache__",
                  "proc", "sys", "dev", "run", "snap", "boot", "cdrom", "tmp",
                  "var", "docker", "lost+found"}
_HITS = ("llama", "llamacpp", "llama.cpp", "ggml")


def _score_dir(name):
    low = name.lower()
    for h in _HITS:
        if h in low:
            return 0
    return 15


def _bfs(roots, predicate, deadline, max_depth=5):
    """按代价(深度+得分)排序的 BFS。"""
    import queue as _q
    pq = _q.PriorityQueue()
    for r in roots:
        if os.path.isdir(r):
            pq.put((0.0, 0, r))
    counter = 1
    while not pq.empty():
        cost, depth, d = pq.get()
        if depth > max_depth or time.time() > deadline:
            continue
        try:
            entries = os.scandir(d)
        except OSError:
            continue
        with entries:
            for e in entries:
                if e.name.startswith(".") or e.name.lower() in _DIR_BLACKLIST:
                    continue
                p = e.path
                if e.is_file(follow_symlinks=False) and predicate(e.name):
                    # 可执行位统一在此校验(谓词只拿得到文件名)
                    if os.access(p, os.X_OK):
                        return p
                    continue
                if e.is_dir(follow_symlinks=False):
                    c = (depth + 2) * 1.5 + _score_dir(e.name)
                    counter += 1
                    pq.put((c, depth + 1, p))
    return None


def find_llama_server():
    """返回 llama-server 可执行路径或 None。"""
    p = procman.which("llama-server")
    if p:
        return p
    # 常见目录
    home = os.path.expanduser("~")
    roots = [home, "/opt", "/usr/local", "/usr/local/bin", os.path.join(home, ".local"),
             os.path.join(home, "tools"), os.path.join(home, "dev"), os.path.join(home, "code")]
    hits = ("llama-server",)
    deadline = time.time() + SEARCH_DEADLINE

    def pred(name):
        return name == "llama-server"

    # 先浅扫常见根的一级子目录
    for r in list(roots):
        if os.path.isdir(r):
            for e in os.scandir(r):
                if e.is_dir(follow_symlinks=False) and any(h in e.name.lower() for h in _HITS):
                    for sub in (e.path, os.path.join(e.path, "build", "bin"), os.path.join(e.path, "bin")):
                        cand = os.path.join(sub, "llama-server")
                        if os.path.isfile(cand) and os.access(cand, os.X_OK):
                            return cand
    return _bfs([home, "/opt", "/usr/local"], lambda n: n == "llama-server", deadline)


def find_dsh_project():
    """返回包含 pnpm-workspace.yaml 的 DSH 项目目录或 None。"""
    home = os.path.expanduser("~")
    known = [os.path.join(home, ".local", "share", "deepseek-harness"),
             os.path.join(home, "deepseek-harness"),
             os.path.join(home, "code", "deepseek-harness"),
             os.path.join(home, "Projects", "deepseek-harness"),
             os.path.join(home, ".local", "share", "pnpm", "global"),
             "/opt/deepseek-harness"]
    for k in known:
        if os.path.isfile(os.path.join(k, "pnpm-workspace.yaml")) or \
           os.path.isfile(os.path.join(k, "apps", "cli", "package.json")):
            return k
    deadline = time.time() + SEARCH_DEADLINE
    roots = [os.path.join(home, "code"), os.path.join(home, "Projects"), os.path.join(home, "dev"), "/opt", home]
    return _bfs(roots, lambda n: n == "pnpm-workspace.yaml", deadline, max_depth=4)


# ============================================================ llama.cpp 更新

_GH_API = "https://api.github.com/repos/ggml-org/llama.cpp/releases/latest"
_MIRRORS = ("https://ghproxy.net/", "https://ghfast.top/", "https://gh-proxy.com/")


def _http_get(url, timeout=30):
    req = urllib.request.Request(url, headers={"User-Agent": "LlamaDSH-Launcher-Linux"})
    return urllib.request.urlopen(req, timeout=timeout).read()


def llama_local_version(llama_dir):
    """运行 llama-server --version,提取 build 号。"""
    exe = os.path.join(llama_dir, "llama-server")
    if not os.path.isfile(exe):
        exe = procman.which("llama-server") or ""
    if not exe or not os.path.isfile(exe):
        return None
    try:
        r = subprocess.run([exe, "--version"], capture_output=True, text=True, timeout=15)
        out = (r.stdout or "") + (r.stderr or "")  # 新版版本横幅在 stderr
        m = re.search(r"build\s+(\d+)", out)
        return int(m.group(1)) if m else None
    except Exception:
        return None


def llama_latest_version():
    """上游最新 build 号(nightly-tag.txt 资产,失败回退近 20 个 release 的 bNNNNN)。"""
    try:
        data = json.loads(_http_get(_GH_API).decode("utf-8"))
        for a in data.get("assets", []):
            if a.get("name") == "nightly-tag.txt":
                txt = _http_get(a["browser_download_url"]).decode().strip()
                m = re.search(r"b?(\d+)", txt)
                if m:
                    return int(m.group(1)), data.get("tag_name", "")
        tags = [data.get("tag_name", "")]
        m = re.search(r"b?(\d+)", tags[0])
        if m:
            return int(m.group(1)), tags[0]
    except Exception:
        pass
    return None, ""


def llama_download_url(build):
    """在 bNNNNN release 里挑 ubuntu-x64 zip:优先 vulkan,回退普通/avx2。"""
    api = "https://api.github.com/repos/ggml-org/llama.cpp/releases/tags/b%d" % build
    try:
        data = json.loads(_http_get(api).decode("utf-8"))
    except Exception:
        return None
    names = [a for a in data.get("assets", []) if "-bin-ubuntu-x64" in a.get("name", "") and a["name"].endswith(".zip")]
    if not names:
        names = [a for a in data.get("assets", []) if a.get("name", "").endswith(".zip") and "ubuntu" in a.get("name", "")]
    for pref in ("vulkan", "avx2"):
        for a in names:
            if pref in a["name"]:
                return a["browser_download_url"]
    return names[0]["browser_download_url"] if names else None


def llama_update(llama_dir, log, job_log, repo=None, backend=None):
    """标准版更新: 委托 toolchain 完整链(后端挑选/.part/对账/磁盘预检/build 硬校验)。"""
    import toolchain as _tc
    repo = repo or _tc.DEFAULT_REPO
    local = llama_local_version(llama_dir)
    backend = backend or _tc.backend_preference()
    log("[更新] 按显卡挑选后端: %s" % backend)
    try:
        release = _tc.fetch_latest_llama_release(repo, backend, job_log)
    except Exception as ex:
        return False, "无法获取上游版本: %s" % ex
    latest = release.get("build", 0)
    if local is not None and latest > 0 and local >= latest:
        return True, "已是最新(build %d ≥ %d)" % (local, latest)
    log("[更新] llama.cpp: build %s → %s(%s)" % (local or "?", latest or release["tag"], release["tag"]))
    ok, msg = _tc.apply_llama_update(llama_dir, release, job_log)
    return ok, msg


# ================================================================== DSH 更新

def dsh_check_update(dsh_dir):
    """返回 dict:branch/ahead/behind/head。"""
    try:
        subprocess.run(["git", "fetch", "origin", "--prune"], cwd=dsh_dir,
                       capture_output=True, text=True, timeout=120)
    except Exception as ex:
        return {"error": str(ex)}
    try:
        branch = subprocess.run(["git", "rev-parse", "--abbrev-ref", "HEAD"], cwd=dsh_dir,
                                capture_output=True, text=True, timeout=15).stdout.strip()
        if branch == "HEAD":
            return {"error": "仓库处于 detached HEAD 状态,无法比对更新"}
        head = subprocess.run(["git", "rev-parse", "HEAD"], cwd=dsh_dir,
                              capture_output=True, text=True, timeout=15).stdout.strip()
        cnt = subprocess.run(["git", "rev-list", "--left-right", "--count", "HEAD...origin/%s" % branch],
                             cwd=dsh_dir, capture_output=True, text=True, timeout=30).stdout.strip()
        ahead, behind = (cnt.split() + ["0", "0"])[:2]
        return {"branch": branch, "head": head[:8], "ahead": int(ahead), "behind": int(behind)}
    except Exception as ex:
        return {"error": str(ex)}


def _cleanup_git_lock(repo_dir):
    """git 失败/超时后清理本仓库的 index.lock。

    保守条件: 仅当"本仓库路径"没有存活 git 进程在工作时才删
    (pgrep -x git 是全系统匹配, 用户在别的仓库跑 git 会误阻止 —— 改为按
    /proc/<pid>/cwd 比对仓库路径;pgrep 缺失时保守拒绝而非放行)。"""
    lock = os.path.join(repo_dir, ".git", "index.lock")
    if not os.path.exists(lock):
        return False
    repo_real = os.path.realpath(repo_dir)
    try:
        r = subprocess.run(["pgrep", "-x", "git"], capture_output=True, text=True, timeout=10)
        if r.returncode != 0:
            return False  # pgrep 缺失/异常: 保守拒绝删锁
        for pid in (r.stdout or "").split():
            try:
                cwd = os.path.realpath(os.path.join("/proc", pid, "cwd"))
                if cwd == repo_real or repo_real.startswith(cwd + os.sep):
                    return False  # 该仓库确有 git 进程在工作
            except OSError:
                continue
    except FileNotFoundError:
        return False  # 无 procps: 保守拒绝
    except Exception:
        return False
    try:
        os.remove(lock)
        return True
    except OSError:
        return False


def dsh_pull(dsh_dir):
    try:
        r = subprocess.run(["git", "pull", "--ff-only"], cwd=dsh_dir,
                           capture_output=True, text=True, timeout=240)
        return r.returncode == 0, (r.stdout or r.stderr).strip()[-500:]
    except subprocess.TimeoutExpired:
        if _cleanup_git_lock(dsh_dir):
            return False, "git 执行超时(240 秒); 已自动清理残留的 .git/index.lock, 可直接重试"
        return False, "git 执行超时(240 秒); .git/index.lock 仍在(可能有 git 进程运行中)"
    except Exception as ex:
        # 普通失败且报锁: 同样尝试自动清理(常见于上次异常退出)
        if "index.lock" in str(ex) and _cleanup_git_lock(dsh_dir):
            return False, "git 报锁冲突; 已自动清理残留的 .git/index.lock, 可直接重试"
        return False, str(ex)
