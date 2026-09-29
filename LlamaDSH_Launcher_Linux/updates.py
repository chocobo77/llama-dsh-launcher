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
                  "proc", "sys", "dev", "run", "snap", "boot", "cdrom", "tmp", "var/lib/docker"}
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
    found = []
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
                    return p
                if e.is_dir(follow_symlinks=False):
                    c = (depth + 2) * 1.5 + _score_dir(e.name)
                    counter += 1
                    pq.put((c, depth + 1, p))
        if found:
            break
    return found[0] if found else None


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
        return name == "llama-server" and os.access(os.path.join("", name), os.X_OK)

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
    known = [os.path.join(home, "deepseek-harness"),
             os.path.join(home, "code", "deepseek-harness"),
             os.path.join(home, "Projects", "deepseek-harness"),
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
        out = subprocess.run([exe, "--version"], capture_output=True, text=True, timeout=15).stdout
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


def llama_update(llama_dir, log, job_log):
    """下载并整目录覆盖(只动二进制,不碰模型)。返回 (ok, msg)。"""
    local = llama_local_version(llama_dir)
    latest, tag = llama_latest_version()
    if latest is None:
        return False, "无法获取上游版本(网络?)"
    if local is not None and local >= latest:
        return True, "已是最新(build %d ≥ %d)" % (local, latest)
    log("[更新] llama.cpp: build %s → %d(%s)" % (local or "?", latest, tag))
    url = llama_download_url(latest)
    if not url:
        return False, "未找到 ubuntu-x64 资产"
    ok = False
    for base in ("",) + _MIRRORS:
        dl = url if not base else base + url
        try:
            job_log("下载: %s" % dl)
            blob = _http_get(dl, timeout=1800)
            if len(blob) < 1024 * 1024:
                raise IOError("文件过小")
            zf = zipfile.ZipFile(io.BytesIO(blob))
            os.makedirs(llama_dir, exist_ok=True)
            for info in zf.infolist():
                if info.is_dir():
                    continue
                base_name = os.path.basename(info.filename)
                if not base_name:
                    continue
                target = os.path.join(llama_dir, base_name)
                with zf.open(info) as src, open(target, "wb") as dst:
                    shutil.copyfileobj(src, dst)
                os.chmod(target, 0o755)
            job_log("解压完成 → %s" % llama_dir)
            ok = True
            break
        except Exception as ex:
            job_log("下载/解压失败(%s): %s" % (base or "直连", ex))
    if not ok:
        return False, "全部镜像下载失败"
    new_local = llama_local_version(llama_dir)
    return True, "更新完成:build %s → %s" % (local or "?", new_local or "?")


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
        head = subprocess.run(["git", "rev-parse", "HEAD"], cwd=dsh_dir,
                              capture_output=True, text=True, timeout=15).stdout.strip()
        cnt = subprocess.run(["git", "rev-list", "--left-right", "--count", "HEAD...origin/%s" % branch],
                             cwd=dsh_dir, capture_output=True, text=True, timeout=30).stdout.strip()
        ahead, behind = (cnt.split() + ["0", "0"])[:2]
        return {"branch": branch, "head": head[:8], "ahead": int(ahead), "behind": int(behind)}
    except Exception as ex:
        return {"error": str(ex)}


def dsh_pull(dsh_dir):
    try:
        r = subprocess.run(["git", "pull", "--ff-only"], cwd=dsh_dir,
                           capture_output=True, text=True, timeout=180)
        return r.returncode == 0, (r.stdout or r.stderr).strip()[-500:]
    except Exception as ex:
        return False, str(ex)
