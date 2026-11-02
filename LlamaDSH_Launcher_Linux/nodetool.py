# -*- coding: utf-8 -*-
"""Node.js v24 + pnpm 环境检测与一键安装(CS NodeSetup 的 Linux 适配)。

- 检测候选: PATH / ~/.nvm / ~/.local/share/fnm / ~/.volta / /snap/bin / env(NODE24_PATH 等)。
- 安装(免 root): nodejs.org 镜像体系下载 node-v24-linux-x64.tar.xz →
  ~/.local/lib/nodejs-vX,bin 软链进 ~/.local/bin;SHASUMS256.txt SHA-256 校验
  (取不到校验表直接判失败换源,不做弱校验 — 与 CS 安全策略一致)。
- pnpm: 优先 corepack enable(node24 自带),回退 npm --prefix ~/.local install -g。
- 81cdb3b 语义: exe 存在但 node -v 无输出 → 判"未知版本已安装",跳过重装。
"""
import hashlib
import json
import os
import re
import shutil
import subprocess
import tarfile
import time
import urllib.request

import procman

LOCAL_BIN = os.path.join(os.path.expanduser("~"), ".local", "bin")
LOCAL_LIB = os.path.join(os.path.expanduser("~"), ".local", "lib")
DIST_MIRRORS = ("https://registry.npmmirror.com/-/binary/node", "https://nodejs.org/dist")
UA = {"User-Agent": "LlamaDSH-Launcher"}

_cache = {"t": 0.0, "v": None}


def _http_get(url, timeout=30):
    req = urllib.request.Request(url, headers=UA)
    return urllib.request.urlopen(req, timeout=timeout).read()


def _node_candidates():
    env = os.environ
    cands = []
    for var in ("NODE24_PATH", "NODE_EXE", "NODE_HOME"):
        p = env.get(var)
        if p:
            cands.append(os.path.join(p, "bin", "node") if os.path.isdir(p) else p)
    for p in (os.path.join(LOCAL_LIB, "nodejs", "bin"), "/usr/local/bin", "/usr/bin",
              "/snap/bin", os.path.join(LOCAL_BIN)):
        cands.append(os.path.join(p, "node"))
    home = os.path.expanduser("~")
    cands += glob_dirs(os.path.join(home, ".nvm", "versions"), "node", "bin")
    cands += glob_dirs(os.path.join(home, ".local", "share", "fnm", "node-versions"), "installation", "bin")
    cands.append(os.path.join(home, ".volta", "bin", "node"))
    w = procman.which("node")
    if w:
        cands.append(w)
    return cands


def glob_dirs(root, prefix, sub):
    out = []
    try:
        if not os.path.isdir(root):
            return out
        for d in os.listdir(root):
            p = os.path.join(root, d)
            if d.startswith(prefix) and os.path.isdir(p):
                cand = os.path.join(p, sub, "node")
                if os.path.isfile(cand):
                    out.append(cand)
    except OSError:
        pass
    return out


def _exe_version(path, args=("--version",), timeout=8):
    try:
        out = subprocess.run([path] + list(args), capture_output=True, text=True, timeout=timeout)
        v = (out.stdout or "").strip()
        return v or None
    except Exception:
        return None


def detect(force=False):
    """返回 dict: node24 {path,version} / any_node {path,version} / pnpm {path,version} /
    meets(bool) / version_unknown(bool)。"""
    now = time.time()
    if not force and _cache["v"] and now - _cache["t"] < 15:
        return _cache["v"]
    nodes = []
    for c in _node_candidates():
        if os.path.isfile(c) and os.access(c, os.X_OK):
            v = _exe_version(c)
            if v and re.match(r"^v\d+", v):
                nodes.append((c, v))
    def major(v):
        try:
            return int(v.lstrip("v").split(".")[0])
        except (ValueError, IndexError):
            return 0
    n24 = next(((p, v) for p, v in nodes if major(v) >= 24), None)
    if n24 is None and nodes:
        n24 = max(nodes, key=lambda x: major(x[1]))
    pnpm = procman.which("pnpm")
    pnpm_v = _exe_version(pnpm) if pnpm else None
    st = {
        "node24": {"path": n24[0], "version": n24[1]} if n24 else {"path": None, "version": None},
        "any_node": {"path": nodes[0][0], "version": nodes[0][1]} if nodes else {"path": None, "version": None},
        "pnpm": {"path": pnpm, "version": pnpm_v} if pnpm else {"path": None, "version": None},
        "version_unknown": bool(n24 and not n24[1]),
        "meets": bool(n24 and major(n24[1]) >= 24 and pnpm),
    }
    _cache.update(t=now, v=st)
    return st


_v24_cache = {"t": 0.0, "v": (None, None)}


def latest_v24():
    """最新 v24.x:npmmirror 镜像 → nodejs.org(120s 缓存)。"""
    now = time.time()
    if now - _v24_cache["t"] < 120:
        return _v24_cache["v"]
    for base in DIST_MIRRORS:
        try:
            data = json.loads(_http_get(base + "/index.json", timeout=20))
            cands = [d["version"] for d in data if d.get("version", "").startswith("v24.")]
            if cands:
                def key(v):
                    return tuple(int(x) for x in v.lstrip("v").split("."))
                r = (base, max(cands, key=key))
                _v24_cache.update(t=now, v=r)
                return r
        except Exception:
            continue
    return None, None


def _sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def install_node_env(job_log, timeout_secs=600):
    """一键安装/更新 Node24 + pnpm。返回 (ok, msg)。"""
    st = detect(force=True)
    latest_base, latest_v = latest_v24()
    if not latest_v:
        return False, "无法获取 nodejs.org 版本列表(网络?)"
    job_log("Node 最新 v24: %s(%s)" % (latest_v, latest_base))
    cur_v = st["node24"]["version"]
    # 81cdb3b: exe 在但版本探测失败 → 判"未知版本已安装",跳过重装
    version_unknown = st["node24"]["path"] and not cur_v
    if version_unknown:
        job_log("⚠ Node %s 存在但版本探测失败(node -v 无输出)。跳过重装——"
                "若确需更新请先手动确认 node 可运行。" % st["node24"]["path"])
    need_node = (not cur_v and not version_unknown) or (cur_v and cur_v != latest_v and not version_unknown)
    if need_node:
        fname = "node-%s-linux-x64.tar.xz" % latest_v
        url = "%s/%s/%s" % (latest_base, latest_v, fname)
        dest = os.path.join(LOCAL_LIB, fname)
        os.makedirs(LOCAL_LIB, exist_ok=True)
        job_log("下载: %s" % url)
        # .part + SHA256 校验(校验表取不到 → 换源, 不做弱校验)
        ok_dl = False
        for attempt_base in DIST_MIRRORS:
            url_b = "%s/%s/%s" % (attempt_base, latest_v, fname)
            part = dest + ".part"
            try:
                req = urllib.request.Request(url_b, headers=UA)
                with urllib.request.urlopen(req, timeout=timeout_secs) as r, open(part, "wb") as f:
                    while True:
                        chunk = r.read(1 << 20)
                        if not chunk:
                            break
                        f.write(chunk)
                os.replace(part, dest)
                shas_url = "%s/%s/SHASUMS256.txt" % (attempt_base, latest_v)
                try:
                    shas = _http_get(shas_url, timeout=30).decode()
                    want = None
                    for ln in shas.splitlines():
                        if ln.strip().endswith(fname):
                            want = ln.split()[0]
                            break
                    if want:
                        got = _sha256(dest)
                        if got != want:
                            raise IOError("SHA-256 不匹配(得 %s… 期望 %s…)" % (got[:12], want[:12]))
                        job_log("SHA-256 校验通过")
                    else:
                        raise IOError("SHASUMS 中未找到 %s" % fname)
                except Exception as sha_ex:
                    job_log("校验失败(%s),换源重试" % sha_ex)
                    continue
                ok_dl = True
                break
            except Exception as ex:
                job_log("下载失败(%s): %s" % (attempt_base, ex))
                try:
                    os.remove(dest + ".part")
                except OSError:
                    pass
        if not ok_dl:
            return False, "Node 下载/校验失败(全部源)"
        # 解压到 ~/.local/lib/nodejs-vX,bin 软链进 ~/.local/bin
        ex_dir = os.path.join(LOCAL_LIB, "nodejs-extract")
        shutil.rmtree(ex_dir, ignore_errors=True)
        os.makedirs(ex_dir)
        with tarfile.open(dest, "r:xz") as tf:
            tf.extractall(ex_dir)
        inner = next((os.path.join(ex_dir, d) for d in os.listdir(ex_dir)
                      if d.startswith("node-")), ex_dir)
        target = os.path.join(LOCAL_LIB, "nodejs-" + latest_v)
        shutil.rmtree(target, ignore_errors=True)
        shutil.move(inner, target)
        shutil.rmtree(ex_dir, ignore_errors=True)
        try:
            os.remove(dest)
        except OSError:
            pass
        os.makedirs(LOCAL_BIN, exist_ok=True)
        for exe in ("node", "npm", "npx", "corepack"):
            src = os.path.join(target, "bin", exe)
            dst = os.path.join(LOCAL_BIN, exe)
            try:
                if os.path.islink(dst) or os.path.exists(dst):
                    os.remove(dst)
                if os.path.isfile(src):
                    os.symlink(src, dst)
            except OSError:
                pass
        job_log("Node %s 安装到 %s" % (latest_v, target))
        cur_v = latest_v
    else:
        job_log("Node 已是最新(%s)" % cur_v)
    # pnpm: corepack 优先
    pnpm = procman.which("pnpm")
    if not pnpm:
        node = detect(force=True)["node24"]["path"]
        if node:
            job_log("安装 pnpm(corepack enable)...")
            r = subprocess.run([node, os.path.join(os.path.dirname(node), "corepack"), "enable"],
                               capture_output=True, text=True, timeout=120)
            pnpm = procman.which("pnpm")
            if not pnpm:
                job_log("corepack 失败(%s),回退 npm --prefix ~/.local install -g pnpm" % (r.stderr or "").strip()[:120])
                npm = os.path.join(os.path.dirname(node), "npm")
                subprocess.run([npm, "config", "set", "prefix", os.path.expanduser("~/.local")],
                               capture_output=True, text=True, timeout=60)
                r = subprocess.run([npm, "install", "-g", "pnpm"], capture_output=True,
                                   text=True, timeout=600)
                pnpm = procman.which("pnpm")
    st2 = detect(force=True)
    if not st2["pnpm"]["path"]:
        return False, "pnpm 安装失败(可手动: corepack enable 或 npm install -g pnpm)"
    job_log("pnpm %s 就绪(%s)" % (st2["pnpm"]["version"], st2["pnpm"]["path"]))
    return True, "Node %s + pnpm %s 就绪" % (st2["node24"]["version"], st2["pnpm"]["version"])
