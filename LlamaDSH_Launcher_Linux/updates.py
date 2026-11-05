# -*- coding: utf-8 -*-
"""自动检索(FileSearcher 精简版)与更新服务(UpdateService)。

- 检索:PATH → 常见目录 → 受限 BFS(启发式打分,超时停止)。
- llama.cpp 更新:GitHub Releases,优先 vulkan-x64 的 ubuntu 包,三镜像回退。
- DSH 更新:git fetch + ahead/behind,--ff-only 拉取;本地未提交改动自动
  stash→合并→pop 恢复;直连失败时镜像 fetch 回退(42e897c 同语义)。
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


def find_dsh_project(known_only=False):
    """返回包含 pnpm-workspace.yaml 的 DSH 项目目录或 None。

    known_only=True 只查已知位置(几次 stat): 启动路径上用它, 全盘 BFS 最长
    SEARCH_DEADLINE=45s, 放在点"启动"之后的 HTTP 处理线程里就是一次长时间卡死。
    """
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
    if known_only:
        return None
    deadline = time.time() + SEARCH_DEADLINE
    roots = [os.path.join(home, "code"), os.path.join(home, "Projects"), os.path.join(home, "dev"), "/opt", home]
    return _bfs(roots, lambda n: n == "pnpm-workspace.yaml", deadline, max_depth=4)


# ============================================================ llama.cpp 更新

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


def llama_update_probe():
    """上游版本探针, 返回 {"build","tag","error"}。

    以前这里自己另写了一套(_GH_API 只直连 + 30s 超时): 本机实测每次都是 32s 后返回
    (None, "") —— 因为直连 api.github.com 通, 但 nightly-tag.txt 的下载资产超时, 而异常
    会跳过同一 try 里本来可用的"latest tag 解析"兜底。工具链那条(fetch_latest_llama_release)
    早就有镜像 + 严格 tag 回退, 两条并存的**分叉本身**就是"检查更新时好时坏"的根因。
    """
    import toolchain as _tc
    try:
        rel = _tc.fetch_latest_llama_release(_tc.DEFAULT_REPO, None, None)
    except Exception as ex:                                  # noqa: BLE001
        return {"build": None, "tag": "", "error": "%s: %s" % (type(ex).__name__, ex)}
    build = int(rel.get("build") or 0)
    if build <= 0:
        return {"build": None, "tag": rel.get("tag", ""),
                "error": "上游 release 列表里没解析出 build 号(tag=%s)" % rel.get("tag", "")}
    return {"build": build, "tag": rel.get("tag", ""), "error": ""}


def llama_latest_version():
    """兼容旧调用: 只回 (build, tag)。失败原因请用 llama_update_probe()。"""
    p = llama_update_probe()
    return p["build"], p["tag"]


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

def _git(cwd, *args, timeout=15):
    r = subprocess.run(["git", "-C", cwd] + list(args), capture_output=True, text=True,
                       timeout=timeout)
    return r.returncode, (r.stdout or "").strip(), (r.stderr or "").strip()


def _build_head_short():
    try:
        import state
        return (state.load_build_state().get("head") or "")[:8]
    except Exception:
        return ""


def dsh_git_state(dsh_dir):
    """能不能用 git 比对更新: 返回 (cause, hint)。

    cause ∈ ""(可以) / not_dir / not_repo / no_remote / detached。这四类**都不是网络问题**:
    本机实测 ~/.local/share/deepseek-harness 是一个"local source snapshot"提交, 仓库里
    一个 remote 都没配, 旧代码却把它报成"fetch 失败(直连与镜像均不可达)" —— 用户于是反复
    点"检查更新", 看起来就是功能不稳定。
    """
    if not dsh_dir or not os.path.isdir(dsh_dir):
        return "not_dir", "没有指定(或不存在的)DSH 目录: 先在设置里填 DSH 源码目录"
    rc, _, _ = _git(dsh_dir, "rev-parse", "--is-inside-work-tree")
    head = _build_head_short()
    if rc != 0:
        return "not_repo", ("该 DSH 目录不是 git 检出%s, 无法用 git 比对更新。"
                            "DSH 的更新走重装或「🔨 重建 DSH」" %
                            ("(构建基线 commit %s)" % head if head else ""))
    rc, remotes, _ = _git(dsh_dir, "remote")
    if rc != 0 or not remotes:
        return "no_remote", ("该 DSH 目录没有配置 git 远端(不是网络问题):它只是一个本地源码快照"
                             "%s。要能检查更新, 先给它指上游, 例如:\n"
                             "  git -C \"%s\" remote add origin <DSH 源码仓库地址>\n"
                             "之后再点检查更新即可。" %
                             ("(构建基线 commit %s)" % head if head else "", dsh_dir))
    rc, br, _ = _git(dsh_dir, "rev-parse", "--abbrev-ref", "HEAD")
    if br == "HEAD":
        return "detached", "仓库处于 detached HEAD 状态,无法比对更新"
    return "", ""


_DSH_CAUSE_MSG = {"not_dir": "DSH 目录未指定",
                  "not_repo": "DSH 目录不是 git 仓库",
                  "no_remote": "DSH 目录没有配置 git 远端(origin)",
                  "detached": "DSH 仓库处于 detached HEAD"}


def dsh_check_update(dsh_dir):
    """返回 dict:branch/ahead/behind/head;失败时 error + cause + hint。"""
    cause, hint = dsh_git_state(dsh_dir)
    if cause:
        return {"error": _DSH_CAUSE_MSG.get(cause, "DSH 仓库状态无法比对更新"),
                "cause": cause, "hint": hint}
    try:
        fr = subprocess.run(["git", "fetch", "origin", "--prune"], cwd=dsh_dir,
                            capture_output=True, text=True, timeout=120)
    except Exception as ex:
        return {"error": str(ex), "cause": "network"}
    remote_ref = "origin/%s"
    if fr.returncode != 0:
        # 直连失败: 镜像 fetch 回退(比对对象改为 FETCH_HEAD)
        try:
            origin = subprocess.run(["git", "remote", "get-url", "origin"], cwd=dsh_dir,
                                    capture_output=True, text=True, timeout=15).stdout.strip()
        except Exception:
            origin = ""
        fetched = False
        tried = []
        for candidate in _mirror_urls(origin):
            tried.append(candidate)
            mf = subprocess.run(["git", "-c", "http.version=HTTP/1.1", "fetch", "--no-tags",
                                 candidate, "+refs/heads/*:refs/mirrors/*"],
                                cwd=dsh_dir, capture_output=True, text=True, timeout=120)
            if mf.returncode == 0:
                fetched = True
                remote_ref = "refs/mirrors/%s"
                break
        if not fetched:
            return {"error": "fetch 失败(直连与镜像均不可达): " + (fr.stderr or "").strip()[-400:],
                    "cause": "network",
                    "hint": "origin=%s; 试过镜像: %s" % (origin or "?", ", ".join(tried) or "(无镜像候选)")}
    try:
        branch = subprocess.run(["git", "rev-parse", "--abbrev-ref", "HEAD"], cwd=dsh_dir,
                                capture_output=True, text=True, timeout=15).stdout.strip()
        if branch == "HEAD":
            return {"error": "仓库处于 detached HEAD 状态,无法比对更新", "cause": "detached"}
        head = subprocess.run(["git", "rev-parse", "HEAD"], cwd=dsh_dir,
                              capture_output=True, text=True, timeout=15).stdout.strip()
        cnt = subprocess.run(["git", "rev-list", "--left-right", "--count", "HEAD...%s" % (remote_ref % branch)],
                             cwd=dsh_dir, capture_output=True, text=True, timeout=30).stdout.strip()
        ahead, behind = (cnt.split() + ["0", "0"])[:2]
        return {"branch": branch, "head": head[:8], "ahead": int(ahead), "behind": int(behind),
                "build_baseline": _build_head_short(),
                "compared_via": "mirror" if remote_ref != "origin/%s" else "origin"}
    except Exception as ex:
        return {"error": str(ex), "cause": "compare"}


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


def _run_git(repo_dir, args, timeout):
    try:
        r = subprocess.run(["git"] + args, cwd=repo_dir,
                           capture_output=True, text=True, timeout=timeout)
        return r.returncode, ((r.stdout or "") + (r.stderr or "")).strip()
    except subprocess.TimeoutExpired:
        if _cleanup_git_lock(repo_dir):
            return -1, "git 执行超时(%d 秒); 已自动清理残留的 .git/index.lock" % timeout
        return -1, "git 执行超时(%d 秒); .git/index.lock 仍在(可能有 git 进程运行中)" % timeout
    except FileNotFoundError:
        return -1, "未找到 git,请安装 git 并加入 PATH"
    except Exception as ex:
        if "index.lock" in str(ex) and _cleanup_git_lock(repo_dir):
            return -1, "git 报锁冲突; 已自动清理残留的 .git/index.lock"
        return -1, str(ex)


def _mirror_urls(origin):
    """从 origin URL 推导 GitHub 镜像候选(UpdateService.MirrorUrls 同语义):
    支持 LLAMADSH_GIT_MIRROR 环境变量(分号分隔,{url} 占位可选)+ 三个固定前缀。"""
    u = (origin or "").strip()
    if not u:
        return []
    if u.startswith("git@github.com:"):
        u = "https://github.com/" + u[len("git@github.com:"):]
    elif u.startswith("ssh://git@github.com/"):
        u = "https://github.com/" + u[len("ssh://git@github.com/"):]
    if "github.com" not in u.lower():
        return []
    u = u.split("?", 1)[0].rstrip("/")
    if u.lower().endswith(".git"):
        u = u[:-4]
    out, seen = [], set()

    def add(url):
        if url.lower() not in seen:
            seen.add(url.lower())
            out.append(url)

    custom = os.environ.get("LLAMADSH_GIT_MIRROR") or ""
    for item in custom.split(";"):
        item = item.strip()
        if not item:
            continue
        if "{url}" in item.lower():
            # 大小写不敏感替换占位符
            idx = item.lower().index("{url}")
            add(item[:idx] + u + item[idx + 5:])
        else:
            add(item.rstrip("/") + "/" + u)
    for prefix in ("https://gh-proxy.com/", "https://ghfast.top/", "https://ghproxy.net/"):
        if u.startswith(prefix):
            continue
        add(prefix + u)
    return out


def _local_changes_blocked(git_output):
    """git 输出表明合并被本地未提交改动挡住。"""
    t = (git_output or "").lower()
    return ("would be overwritten" in t or "please commit your changes" in t
            or "local changes" in t)


def _stash_pop_safe(repo_dir, log):
    """恢复 stash;pop 冲突时 git 本就不丢弃条目,提示手动处理。"""
    rc, out = _run_git(repo_dir, ["stash", "pop"], 60)
    if rc == 0:
        log("[更新] 本地未提交改动已恢复(stash pop)")
    else:
        log("[更新] ⚠ 更新完成,但本地改动恢复时有冲突——改动仍保留在 stash 中。"
            "请在 DSH 目录手动执行 git stash pop 处理:\n" + out[-500:])


def dsh_pull(dsh_dir, log=None):
    """git pull --ff-only;本地未提交改动挡合并时 stash→合并→pop 恢复;
    直连失败时镜像 fetch + merge --ff-only 回退(不产生 merge 提交)。
    所有失败路径先 pop 还原工作区再报错,不留半套状态(42e897c 同语义)。"""
    log = log or (lambda t: None)
    # 先把"根本不是 git 检出/没有远端"这类**结构性问题**分出来: 否则 git 的 stderr 会被
    # 汇进"直连与镜像拉取均失败", 把一个"没配 origin"的本地快照说成网络问题(与检查更新同病灶)。
    cause, hint = dsh_git_state(dsh_dir)
    if cause:
        return False, _DSH_CAUSE_MSG.get(cause, "DSH 仓库状态无法更新") + (("\n" + hint) if hint else "")
    rc, out = _run_git(dsh_dir, ["pull", "--ff-only"], 240)
    stashed = False
    if rc == 0:
        return True, out[-500:]
    if rc > 0 and _local_changes_blocked(out):
        # 本地补丁挡住快进: stash 带标记,更新完自动恢复,绝不丢弃。
        # -u 连未跟踪文件一起收进 stash(untracked 挡 merge 是常见形态);
        # "No local changes to save" 视为无可 stash,不置 stashed 标记
        rc2, out2 = _run_git(dsh_dir, ["stash", "push", "-u", "-m", "LlamaDSH auto-stash before update"], 60)
        if rc2 != 0:
            return False, "本地有未提交改动且自动 stash 失败:\n" + out2[-400:]
        if "no local changes to save" not in out2.lower():
            stashed = True
            log("[更新] 检测到本地未提交改动,已自动 stash(更新完成后自动恢复)")
        rc, out = _run_git(dsh_dir, ["pull", "--ff-only"], 240)
        if rc == 0:
            _stash_pop_safe(dsh_dir, log)
            return True, out[-500:]
    # 直连拉取失败: 镜像 fetch 当前分支 + 本地快进合并
    brc, branch = _run_git(dsh_dir, ["rev-parse", "--abbrev-ref", "HEAD"], 15)
    if brc == 0 and branch and branch != "HEAD":
        orc, origin = _run_git(dsh_dir, ["remote", "get-url", "origin"], 15)
        last_mirror_err = ""
        for candidate in _mirror_urls(origin if orc == 0 else ""):
            mrc, mout = _run_git(dsh_dir,
                                 ["-c", "http.version=HTTP/1.1", "fetch", "--no-tags", candidate, branch], 120)
            if mrc == 0:
                mrc2, head = _run_git(dsh_dir, ["rev-parse", "FETCH_HEAD"], 15)
                if mrc2 == 0 and re.fullmatch(r"[0-9a-f]{40}", head):
                    mmrc, mmerge = _run_git(dsh_dir, ["merge", "--ff-only", head], 120)
                    if mmrc == 0:
                        if stashed:
                            _stash_pop_safe(dsh_dir, log)
                        log("[更新] 已通过镜像完成更新: %s" % candidate)
                        return True, "镜像更新成功(%s): %s" % (candidate, head[:8])
                    if stashed:
                        _stash_pop_safe(dsh_dir, log)  # 合并仍失败: 先还原工作区
                    return False, "镜像已获取上游,但本地无法快进合并(可能有本地提交/历史分叉):\n" + mmerge[-800:]
                break
            last_mirror_err = mout
        if stashed:
            _stash_pop_safe(dsh_dir, log)  # 镜像全失败: 先还原工作区再报错
        return False, "直连与镜像拉取均失败:\n" + (out + "\n" + last_mirror_err)[-800:]
    if stashed:
        _stash_pop_safe(dsh_dir, log)
    return False, out[-500:]
