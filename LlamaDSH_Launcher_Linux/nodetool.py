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


def _pnpm_path():
    """pnpm 的实际可执行文件: PATH 之外必须认 LOCAL_BIN(~/.local/bin)。

    node 一侧的候选里就有 LOCAL_BIN(_node_candidates), pnpm 一侧以前只走
    procman.which(PATH)。而两条安装路线(corepack 的 shim、npm install -g --prefix ~/.local)
    都把 pnpm 放进 ~/.local/bin, 项目里没有任何地方把它加进 PATH。GNOME 自启、systemd-user、
    sudo 的 PATH 常常不含它 ⇒ 明明装好了却被判"未安装": 面板显示 pnpm 未安装, 一键安装每次都
    重下一遍 node 并在结尾报"pnpm 安装失败", 用户反复点也不会收敛。
    """
    p = procman.which("pnpm")
    if p:
        return p
    for cand in (os.path.join(LOCAL_BIN, "pnpm"), "/usr/local/bin/pnpm", "/usr/bin/pnpm"):
        if os.path.isfile(cand) and os.access(cand, os.X_OK):
            return cand
    return None


def detect(force=False):
    """返回 dict: node24 {path,version} / any_node {path,version} / pnpm {path,version} /
    meets(bool) / version_unknown(bool)。"""
    now = time.time()
    if not force and _cache["v"] and now - _cache["t"] < 15:
        return _cache["v"]
    nodes, broken = [], []
    for c in _node_candidates():
        if os.path.isfile(c) and os.access(c, os.X_OK):
            v = _exe_version(c)
            if v and re.match(r"^v\d+", v):
                nodes.append((c, v))
            elif c not in broken:
                # 文件在、能执行, 但 `node -v` 没输出(损坏/被换成的空壳/依赖库缺失):
                # 81cdb3b 要把它算"已安装但版本未知"。以前这里直接丢弃, 于是
                # version_unknown 恒为 False(下游那段跳过重装的守卫是死码), 面板上
                # 与"根本没装 node"同形, 一键安装每次都重下一遍 node。
                broken.append(c)

    def major(v):
        try:
            return int(v.lstrip("v").split(".")[0])
        except (ValueError, IndexError):
            return 0
    n24 = next(((p, v) for p, v in nodes if major(v) >= 24), None)
    if n24 is None and nodes:
        n24 = max(nodes, key=lambda x: major(x[1]))
    unknown = bool(n24 is None and broken)
    pnpm = _pnpm_path()
    pnpm_v = _exe_version(pnpm) if pnpm else None
    st = {
        "node24": ({"path": n24[0], "version": n24[1]} if n24 else
                   ({"path": broken[0], "version": None} if unknown else {"path": None, "version": None})),
        "any_node": {"path": nodes[0][0], "version": nodes[0][1]} if nodes else
                    ({"path": broken[0], "version": None} if unknown else {"path": None, "version": None}),
        "pnpm": {"path": pnpm, "version": pnpm_v} if pnpm else {"path": None, "version": None},
        "version_unknown": unknown,
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
            # 版本号随后要拼进下载 URL 与本地文件名: 含 ../、引号、空格即为
            # 路径穿越/参数注入面, 先过形态白名单(对齐 Windows NodeSetup)。
            cands = [d["version"] for d in data
                     if d.get("version", "").startswith("v24.")
                     and re.fullmatch(r"v\d+\.\d+\.\d+", d["version"])]
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
                try:
                    # SHASUMS 固定从 nodejs.org 官方源取: 与 tarball 同镜像取时,
                    # 镜像被控 = tarball+校验表一起换, 校验形同虚设
                    shas = _http_get("https://nodejs.org/dist/%s/SHASUMS256.txt" % latest_v,
                                     timeout=30).decode()
                    want = None
                    for ln in shas.splitlines():
                        toks = ln.strip().split()
                        # 精确匹配 "<hash>  <fname>": endswith 可被前缀伪造
                        if len(toks) == 2 and toks[1] == fname:
                            want = toks[0]
                            break
                    if want:
                        got = _sha256(dest)
                        if got != want:
                            raise IOError("SHA-256 不匹配(得 %s… 期望 %s…)" % (got[:12], want[:12]))
                        job_log("SHA-256 校验通过")
                    else:
                        raise IOError("SHASUMS 中未找到 %s" % fname)
                except Exception as sha_ex:
                    # 坏文件已 replace 进 dest, 不删会永久残留未校验 tarball
                    job_log("校验失败(%s),删除坏文件并换源重试" % sha_ex)
                    try:
                        os.remove(dest)
                    except OSError:
                        pass
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
            if not os.path.isfile(src):
                continue
            try:
                # 原子换链: 先 remove 再 symlink 的窗口里失败会摧毁用户原有 node 链接
                new_link = dst + ".new"
                if os.path.islink(new_link) or os.path.exists(new_link):
                    os.remove(new_link)
                os.symlink(src, new_link)
                os.replace(new_link, dst)
            except OSError as link_ex:
                job_log("软链 %s 失败: %s" % (dst, link_ex))
        job_log("Node %s 安装到 %s" % (latest_v, target))
        cur_v = latest_v
    else:
        job_log("Node 已是最新(%s)" % cur_v)
    # pnpm: corepack 优先
    pnpm = _pnpm_path()
    if not pnpm:
        node = detect(force=True)["node24"]["path"]
        if node:
            job_log("安装 pnpm(corepack enable)...")
            try:
                r = subprocess.run([node, os.path.join(os.path.dirname(node), "corepack"), "enable"],
                                   capture_output=True, text=True, timeout=120)
            except subprocess.TimeoutExpired:
                r = None
                job_log("corepack enable 超时(120s)")
            pnpm = _pnpm_path()
            if not pnpm:
                # 超时分支里 r 是 None: 直接取 r.stderr 会抛 AttributeError, 整个任务以
                # "'NoneType' object has no attribute 'stderr'" 收场, 看不出 node 其实已装好
                _why = ("corepack enable 超时(120s)" if r is None
                        else ((r.stderr or "") + (r.stdout or "")).strip()[:120])
                job_log("corepack 失败(%s),回退 npm --prefix ~/.local install -g pnpm" % _why)
                npm = os.path.join(os.path.dirname(node), "npm")
                # 一次性 --prefix: `npm config set prefix` 会持久改写 ~/.npmrc,
                # 影响该用户所有 node 的全局安装路径
                try:
                    subprocess.run([npm, "install", "-g", "--prefix",
                                    os.path.expanduser("~/.local"), "pnpm"],
                                   capture_output=True, text=True, timeout=600)
                except subprocess.TimeoutExpired:
                    job_log("npm install -g pnpm 超时(600s)")
                pnpm = _pnpm_path()
    st2 = detect(force=True)
    if not st2["pnpm"]["path"]:
        return False, "pnpm 安装失败(可手动: corepack enable 或 npm install -g pnpm)"
    # "文件在"不等于"能用": corepack 的 shim 首次运行要联网取 pnpm, 离线/被代理拦住时
    # `pnpm --version` 没有任何输出。以前只看 path 就报"pnpm None 就绪", 失败被推到后面的
    # DSH 安装/重建里, 那时没人会想到根因是 pnpm 还没跑起来。
    pv = st2["pnpm"]["version"] or _exe_version(st2["pnpm"]["path"], timeout=60)
    if not pv:
        return False, ("pnpm 已就位(%s)但 `pnpm --version` 无输出 —— corepack 首次运行需要联网取包, "
                       "请手动执行一次 pnpm --version 看具体报错" % st2["pnpm"]["path"])
    job_log("pnpm %s 就绪(%s)" % (pv, st2["pnpm"]["path"]))
    return True, "Node %s + pnpm %s 就绪" % (st2["node24"]["version"], pv)
