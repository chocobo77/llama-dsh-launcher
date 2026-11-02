# -*- coding: utf-8 -*-
"""llama.cpp 变体/版本管理 + 更新链(CS Toolchain/UpdateService 变体部分的 Linux 适配)。

- 变体 = 独立目录(互不覆盖),存于方案 ProfileConfig.LlamaVariants;激活名 ActiveLlamaVariant。
- 版本身份 = `llama-server --version` 的 build 号(运行时探测,无本地清单)。
- 资产挑选(CS 52d1f36 Linux 化): 平台 token "-bin-ubuntu-" + .tar.gz;
  NVIDIA=CUDA(版本最高)+cudart 配对包, Intel=SYCL(fp16→fp32), AMD/默认=Vulkan;
  兜底链末位为 CPU 构建(-bin-ubuntu-x64)。
- 下载加固(d227f3c): .part 临时文件 + 原子改名 + size ±1% 对账 + 磁盘预检(3×, ≥512MB)。
- 落盘自检(0a7cf6d→d227f3c): 装完后实测 build,缺失/不一致按失败处理。
"""
import io
import json
import os
import re
import shutil
import stat
import subprocess
import tarfile
import tempfile
import time

import urllib.request

import applog
import monitor
import procman

GITHUB_API = "https://api.github.com"
MIRRORS = ("https://gh-proxy.com/", "https://ghfast.top/", "https://ghproxy.net/")
UA = {"User-Agent": "LlamaDSH-Launcher", "Accept": "application/vnd.github+json"}
PLATFORM_TOKEN = "-bin-ubuntu-"
ARCHIVE_SUFFIX = ".tar.gz"
DEFAULT_REPO = "ggml-org/llama.cpp"
UPDATE_TMP_ROOT = os.path.join(os.environ.get("XDG_CACHE_HOME", os.path.expanduser("~/.cache")),
                               "llamadsh", "updates")

# 滚动扫描用严格形态: 纯 bNNNNN 或 内嵌build+hex短哈希(PrismML 形态) — d227f3c
RE_TAG_STRICT = re.compile(r"^(?:b(\d+)$|^[A-Za-z0-9_-]*b(\d{3,})-[0-9a-fA-F]{6,}$)")
RE_TAG_LOOSE = re.compile(r"^b(\d+)$", re.I)
RE_TAG_NUM = re.compile(r"^(\d+)$")
RE_TAG_EMBED = re.compile(r"b(\d{3,})", re.I)
RE_CUDA = re.compile(r"cuda-([\d.]+)-x64", re.I)


# ---------------------------------------------------------------- HTTP ----

def http_get(url, timeout=10, binary=False):
    req = urllib.request.Request(url, headers=UA)
    with urllib.request.urlopen(req, timeout=timeout) as r:
        data = r.read()
    return data if binary else data.decode("utf-8", "replace")


def gh_api(path):
    """GitHub API:直连 → 镜像前缀,每候选 2 次,共约 60s 上限(80d56de)。"""
    url = GITHUB_API + path
    candidates = [url] + [m + url for m in MIRRORS]
    last = None
    for u in candidates:
        for _ in range(2):
            try:
                return json.loads(http_get(u, timeout=10))
            except Exception as ex:
                last = ex
                time.sleep(0.4)
    raise last if last else IOError("GitHub API 不可达")


# ------------------------------------------------------- 仓库链接解析 ----

def parse_repo_input(text):
    """仓库链接 → owner/repo。支持 主页/.git/tree/releases 深链/裸 owner/repo
    (f3374cf: 先剥前缀、去 # 锚点、按 / 切段取前两段、repo 去尾 .git)。"""
    t = (text or "").strip()
    if not t:
        return None
    for pre in ("https://github.com/", "http://github.com/", "https://www.github.com/",
                "http://www.github.com/", "github.com/", "www.github.com/"):
        if t.lower().startswith(pre.lower()):
            t = t[len(pre):]
            break
    hash_idx = t.find("#")
    if hash_idx >= 0:
        t = t[:hash_idx]
    seg = [s for s in t.split("/") if s]
    if len(seg) < 2:
        return None
    owner, repo = seg[0], seg[1]
    if repo.lower().endswith(".git"):
        repo = repo[:-4]
    if not owner or not repo:
        return None
    return owner + "/" + repo


# ------------------------------------------------------------ 版本解析 ----

def parse_build_from_tag(tag):
    """宽松版(nightly-tag 内容/latest tag):b12345 / 纯数字 / prism-b10831-9d9efea。"""
    t = tag or ""
    m = RE_TAG_LOOSE.match(t)
    if m:
        return int(m.group(1))
    m = RE_TAG_NUM.match(t)
    if m:
        return int(m.group(1))
    m = RE_TAG_EMBED.search(t)
    if m:
        return int(m.group(1))
    return 0


def local_build(llama_dir):
    """运行 llama-server --version 提取 build 号(8s 超时)。
    新版构建把版本横幅打到 stderr, 两路合并后再匹配。"""
    exe = find_llama_server_exe(llama_dir)
    if not exe:
        return None
    try:
        r = subprocess.run([exe, "--version"], capture_output=True, text=True, timeout=8)
        out = (r.stdout or "") + (r.stderr or "")
        m = re.search(r"build\s+(\d+)", out)
        return int(m.group(1)) if m else None
    except Exception:
        return None


def find_llama_server_exe(llama_dir):
    text = (llama_dir or "").strip().strip('"')
    if not text:
        return None
    if os.path.isdir(text):
        for name in ("llama-server", "llama-server.exe"):
            p = os.path.join(text, name)
            if os.path.isfile(p):
                return p
        for sub in ("bin", "build/bin", "build/bin/Release", "Release"):
            p = os.path.join(text, sub, "llama-server")
            if os.path.isfile(p):
                return p
        return None
    return text if os.path.isfile(text) else None


# ------------------------------------------------------------ 后端挑选 ----

def backend_preference():
    """显卡 → 后端: NVIDIA=cuda, Intel=sycl, AMD/默认=vulkan(52d1f36 Linux 化)。"""
    try:
        return monitor.gpu_vendor_for_backend()
    except Exception:
        return "vulkan"


VALID_BACKENDS = ("vulkan", "rocm", "cuda", "sycl")


def resolve_backend(profile):
    """用户在 UI 里显式选择的后端优先;空/自动回落显卡检测。"""
    choice = ((profile or {}).get("LlamaBackend") or "").strip().lower()
    if choice in VALID_BACKENDS:
        return choice
    return backend_preference()


def _pick(names, token):
    for n in names:
        if token.lower() in n.lower():
            return n
    return None


def pick_assets(assets, backend):
    """从 release 资产名里挑 (主包, cudart 包, 期望大小)。

    候选: 名含 -bin-ubuntu- 且 .tar.gz 结尾;排除 cudart/arm64/s390x;须含 x64。
    """
    names = [a["name"] for a in assets]
    sizes = {a["name"]: a.get("size") or 0 for a in assets}
    cands = [n for n in names
             if PLATFORM_TOKEN in n and n.endswith(ARCHIVE_SUFFIX)
             and "cudart" not in n.lower()
             and "x64" in n.lower() and "arm64" not in n.lower() and "s390x" not in n.lower()]
    cudarts = [n for n in names
               if PLATFORM_TOKEN in n and n.endswith(ARCHIVE_SUFFIX)
               and "cudart" in n.lower() and "x64" in n.lower() and "arm64" not in n.lower()]
    chosen = None
    if backend == "rocm":
        # ROCm 构建(RDNA3 上对 llama.cpp 通常快于 Vulkan);资产名带版本(rocm-10.0-x64),
        # 匹配 token 用 "rocm-"。未找到回退 vulkan
        chosen = _pick(cands, "rocm-") or _pick(cands, "vulkan-x64") or _pick_cpu(cands)
    elif backend == "cuda":
        best, best_v = None, None
        for n in cands:
            m = RE_CUDA.search(n)
            if m:
                v = tuple(int(x) for x in m.group(1).split("."))
                if best_v is None or v > best_v:
                    best, best_v = n, v
        chosen = best or _pick(cands, "vulkan-x64") or _pick_cpu(cands)
        cudart = None
        if chosen:
            cv = RE_CUDA.search(chosen)
            if cv:
                want = ("cudart-llama-" + PLATFORM_TOKEN.lstrip("-").replace("-bin-ubuntu-", "")
                        if False else None)
                # 精确同名匹配: cudart-llama-b{N}-bin-ubuntu-cuda-{V}-x64.tar.gz
                tag_m = re.search(r"(llama-b\d+)", chosen)
                if tag_m:
                    want = "cudart-%s-bin-ubuntu-cuda-%s-x64%s" % (tag_m.group(1), cv.group(1), ARCHIVE_SUFFIX)
                    cudart = want if want in cudarts else None
    elif backend == "sycl":
        chosen = (_pick(cands, "sycl-fp16-x64") or _pick(cands, "sycl-fp32-x64")
                  or _pick(cands, "vulkan-x64") or _pick_cpu(cands))
    else:  # vulkan / AMD / 未知
        chosen = (_pick(cands, "vulkan-x64") or _pick(cands, "rocm-") or _pick_cpu(cands))
    if not chosen:
        chosen = cands[0] if cands else None
    cudart_name = None
    if chosen:
        cv = RE_CUDA.search(chosen)
        if cv:
            tag_m = re.search(r"(llama-b\d+)", chosen)
            if tag_m:
                want = "cudart-%s-bin-ubuntu-cuda-%s-x64%s" % (tag_m.group(1), cv.group(1), ARCHIVE_SUFFIX)
                cudart_name = want if want in cudarts else None
    return {"main": chosen, "main_size": sizes.get(chosen, 0) if chosen else 0,
            "cudart": cudart_name,
            "cudart_size": sizes.get(cudart_name, 0) if cudart_name else 0}


def _pick_cpu(cands):
    """CPU 兜底: 不含任何后端 token 的 -bin-ubuntu-x64.tar.gz。"""
    for n in cands:
        if re.search(r"-bin-ubuntu-x64\.tar\.gz$", n, re.I):
            return n
    return None


# ------------------------------------------------------- release 查询 ----

def fetch_latest_llama_release(repo=DEFAULT_REPO, backend=None, job_log=None):
    """返回 dict: tag/build/asset_name/asset_url/cudart_name/cudart_url/size/cudart_size/stable_version
    失败抛异常(调用方提示网络)。"""
    backend = backend or backend_preference()
    stable_version = ""
    try:
        latest = gh_api("/repos/%s/releases/latest" % repo)
        stable_version = latest.get("tag_name", "")
        assets = latest.get("assets", [])
        nightly = next((a for a in assets if a.get("name") == "nightly-tag.txt"), None)
        if nightly:
            build_tag = ""
            for m in ("",) + MIRRORS:
                try:
                    build_tag = http_get(m + nightly["browser_download_url"], timeout=15).strip()
                    break
                except Exception:
                    continue
            build = parse_build_from_tag(build_tag)
            if build > 0:
                rel = gh_api("/repos/%s/releases/tags/b%d" % (repo, build))
                pick = pick_assets(rel.get("assets", []), backend)
                if pick["main"]:
                    return _release_info(repo, rel, pick, stable_version)
        else:
            # 无 nightly-tag 的分支(PrismML 路径): latest 自身 tag 内嵌 build
            build = parse_build_from_tag(stable_version)
            if build > 0:
                pick = pick_assets(assets, backend)
                if pick["main"]:
                    return _release_info(repo, latest, pick, stable_version)
    except Exception:
        pass
    # 回退: 最近 20 条 releases 严格 tag 扫描
    rels = gh_api("/repos/%s/releases?per_page=20" % repo)
    for rel in rels:
        tag = rel.get("tag_name", "")
        m = RE_TAG_STRICT.match(tag)
        if not m:
            continue
        build = int(m.group(1) or m.group(2))
        pick = pick_assets(rel.get("assets", []), backend)
        if pick["main"]:
            if job_log:
                job_log("回退路径命中滚动构建: %s" % tag)
            return _release_info(repo, rel, pick, stable_version or tag)
    raise IOError("未在 %s 找到可用的 %s 资产(网络或仓库无预编译包)" % (repo, backend))


def _release_info(repo, rel, pick, stable_version):
    return {
        "repo": repo,
        "tag": rel.get("tag_name", ""),
        "build": parse_build_from_tag(rel.get("tag_name", "")),
        "asset_name": pick["main"],
        "asset_url": next((a["browser_download_url"] for a in rel.get("assets", [])
                           if a["name"] == pick["main"]), ""),
        "size": pick["main_size"],
        "cudart_name": pick["cudart"],
        "cudart_url": next((a["browser_download_url"] for a in rel.get("assets", [])
                            if a["name"] == pick["cudart"]), "") if pick["cudart"] else "",
        "cudart_size": pick["cudart_size"],
        "stable_version": stable_version,
    }


# ------------------------------------------------------------- 下载/安装 --

def download_asset(url, dest, expected_size=0, job_log=None, timeout_secs=1200):
    """直连 → 三镜像;.part 临时文件 + 原子改名;长度 <1MB 拒收;±1% 对账。"""
    urls = [url] + [m + url for m in MIRRORS]
    part = dest + ".part"
    last = None
    for u in urls:
        try:
            if job_log:
                job_log("下载: %s" % u)
            req = urllib.request.Request(u, headers=UA)
            with urllib.request.urlopen(req, timeout=timeout_secs) as r, open(part, "wb") as f:
                while True:
                    chunk = r.read(1024 * 256)
                    if not chunk:
                        break
                    f.write(chunk)
            got = os.path.getsize(part)
            if got < 1000000:
                raise IOError("文件过小(%d B)" % got)
            if expected_size > 0 and abs(got - expected_size) > expected_size * 0.01:
                raise IOError("大小对账失败(得 %d, 期望 %d, 疑似镜像错误页/截断)" % (got, expected_size))
            os.replace(part, dest)
            return dest
        except Exception as ex:
            last = ex
            if job_log:
                job_log("下载失败(%s): %s" % (u.split("/")[2] if "/" in u else u, ex))
            try:
                os.remove(part)
            except OSError:
                pass
    raise last if last else IOError("下载失败")


def _disk_precheck(target_dir, need_bytes):
    d = target_dir if os.path.isdir(target_dir) else os.path.dirname(target_dir) or "."
    # 目标/父目录可能尚未创建: 向上找最近存在的祖先再查可用空间
    probe = d
    while not os.path.exists(probe):
        parent = os.path.dirname(probe)
        if parent == probe:
            break
        probe = parent
    free = shutil.disk_usage(probe).free
    need = max(need_bytes, 512 * 1048576)
    return free >= need, free, need


def _extract_tar(archive, dest_dir):
    with tarfile.open(archive, "r:gz") as tf:
        tf.extractall(dest_dir)
    # zipfile/tar 不保证可执行位: 统一给 llama-* 补 755
    for root, _dirs, files in os.walk(dest_dir):
        for fn in files:
            if fn.startswith("llama-") or fn in ("gguf-py", "server"):
                p = os.path.join(root, fn)
                try:
                    os.chmod(p, 0o755)
                except OSError:
                    pass


def _find_dir_containing(root, filename):
    if os.path.isfile(os.path.join(root, filename)):
        return root
    for dirpath, _dirs, files in os.walk(root):
        if filename in files:
            return dirpath
    return None


def _copy_dir_overwrite(src, dst):
    os.makedirs(dst, exist_ok=True)
    for item in os.listdir(src):
        s, d = os.path.join(src, item), os.path.join(dst, item)
        if os.path.isdir(s):
            _copy_dir_overwrite(s, d)
        else:
            try:
                if os.path.exists(d):
                    os.chmod(d, 0o644)
            except OSError:
                pass
            shutil.copy2(s, d)
            try:
                os.chmod(d, 0o755)
            except OSError:
                pass


def llama_running(dir_hint=None):
    """覆盖前运行态预检(Linux 无文件锁,以进程/端口为准)。"""
    import glob as _glob
    pids = [int(os.path.basename(p)) for p in _glob.glob("/proc/[0-9]*")]
    for pid in pids:
        try:
            with open("/proc/%d/comm" % pid) as f:
                if f.read().strip() in ("llama-server", "llama-server.exe"):
                    return True
        except OSError:
            continue
    return False


def apply_llama_update(dir_, release, job_log=None):
    """下载 → 解压 → 覆盖 → build 硬校验。返回 (ok, msg)。"""
    dir_ = os.path.abspath(dir_)
    os.makedirs(dir_, exist_ok=True)
    if llama_running():
        return False, "llama-server 正在运行,请先停止再更新(防半套混合二进制)"
    os.makedirs(UPDATE_TMP_ROOT, exist_ok=True)  # 先建缓存目录再做磁盘预检
    need = max(release.get("size", 0) * 3, 512 * 1048576)
    ok, free, need = _disk_precheck(UPDATE_TMP_ROOT, need)
    if not ok:
        return False, "磁盘空间不足(需要约 %.1f GB,可用 %.1f GB)" % (need / GIB, free / GIB)
    main_path = os.path.join(UPDATE_TMP_ROOT, release["asset_name"])
    try:
        download_asset(release["asset_url"], main_path, release.get("size", 0), job_log)
        ex_dir = os.path.join(UPDATE_TMP_ROOT, "extract_%d" % release.get("build", 0))
        shutil.rmtree(ex_dir, ignore_errors=True)
        os.makedirs(ex_dir)
        _extract_tar(main_path, ex_dir)
        src_dir = _find_dir_containing(ex_dir, "llama-server")
        if not src_dir:
            return False, "压缩包内未找到 llama-server(结构变化?)"
        cudart_note = ""
        if release.get("cudart_url"):
            cudart_path = os.path.join(UPDATE_TMP_ROOT, release["cudart_name"])
            try:
                download_asset(release["cudart_url"], cudart_path, release.get("cudart_size", 0), job_log)
                cdir = os.path.join(UPDATE_TMP_ROOT, "extract_cudart_%d" % release.get("build", 0))
                shutil.rmtree(cdir, ignore_errors=True)
                os.makedirs(cdir)
                _extract_tar(cudart_path, cdir)
                _copy_dir_overwrite(cdir, src_dir)  # cudart 包含 *.so*,并入同一目录
                cudart_note = " + cudart 运行库已并入"
            except Exception as ex:
                cudart_note = "(cudart 下载失败: %s — 启动 CUDA 版可能报缺库, 可改选 Vulkan 资产)" % ex
        # 覆盖安装(只动二进制不碰模型;先清目标旧二进制防混杂)
        for fn in os.listdir(src_dir):
            s = os.path.join(src_dir, fn)
            if os.path.isfile(s):
                shutil.copy2(s, os.path.join(dir_, fn))
                try:
                    os.chmod(os.path.join(dir_, fn), 0o755)
                except OSError:
                    pass
            elif os.path.isdir(s):
                _copy_dir_overwrite(s, os.path.join(dir_, fn))
        # 落盘自检(d227f3c 硬校验): 本地 build 必须存在且与 release 一致
        lb = local_build(dir_)
        if lb is None:
            return False, "安装后 llama-server 未报告 build 号,安装可能不完整。建议删除该目录后重新安装。"
        rb = release.get("build", 0)
        if rb > 0 and lb != rb:
            return False, ("本地 build %d 与上游 %d 不一致(目录里可能是分支/CI 构建)。"
                           "若加载模型报 ggml type 错误,请删除该目录后重新安装。" % (lb, rb))
        msg = "llama.cpp 已更新到 %s(build %d)[%s]%s, 本地验证 build %d" % (
            release["tag"], rb, release["asset_name"], cudart_note, lb)
        if "prismml" in (release.get("repo") or "").lower():
            msg += "\n⚠ PrismML 低比特内核仅覆盖 CUDA/Metal/CPU——AMD/Intel 显卡将走 CPU 回退(较慢)。"
        if "libcudart" in cudart_note:
            pass
        return True, msg
    finally:
        shutil.rmtree(UPDATE_TMP_ROOT, ignore_errors=True)


GIB = 1073741824.0


# ------------------------------------------------------------ 变体管理 ----

def ensure_default_variant(profile):
    """旧配置迁移: LlamaVariants 为空时把现有 LlamaDir 注册为标准变体。"""
    vs = profile.get("LlamaVariants")
    if not vs:
        profile["LlamaVariants"] = [{"Name": "标准 llama.cpp", "Repo": DEFAULT_REPO,
                                     "Dir": profile.get("LlamaDir") or ""}]
        if not profile.get("ActiveLlamaVariant"):
            profile["ActiveLlamaVariant"] = "标准 llama.cpp"
    return profile


def suggest_variant_dir(base_dir, owner):
    """与现有 llama.cpp 并排: <parent>/llama.cpp-<owner>。"""
    base = (base_dir or "").strip().strip('"') or os.path.expanduser("~/llama.cpp")
    parent = os.path.dirname(base.rstrip("/")) or os.path.expanduser("~")
    return os.path.join(parent, "llama.cpp-" + (owner or "variant"))


def switch_variant(profile, name):
    """切换激活变体 = 切换 LlamaDir(运行中由 API 层守卫)。返回 (ok, msg, new_dir)。"""
    ensure_default_variant(profile)
    vs = profile.get("LlamaVariants") or []
    v = next((x for x in vs if x.get("Name") == name), None)
    if v is None:
        return False, "变体不存在: %s" % name, None
    profile["ActiveLlamaVariant"] = name
    d = v.get("Dir") or ""
    profile["LlamaDir"] = d
    if not d:
        return True, "已切换到 '%s',但该变体尚未安装(目录为空)" % name, d
    return True, "已切换到 '%s'(%s)" % (name, d), d


def install_variant(profile, repo_input, name, dir_, job_log=None, backend=None):
    """变体安装全流程: 解析链接 → 查最新 → 下载安装 → 注册变体。返回 (ok, msg)。"""
    repo = parse_repo_input(repo_input)
    if not repo:
        return False, "无法识别仓库链接(支持 主页/.git/tree/releases 深链/裸 owner/repo)"
    if not name:
        name = repo.split("/")[1] or repo.split("/")[0]
    vs = profile.get("LlamaVariants") or []
    dup = next((x for x in vs if x.get("Name") == name and (x.get("Dir") or "") != (dir_ or "")), None)
    if dup:
        return False, "已存在同名但目录不同的变体: %s" % name
    if job_log:
        job_log("查询 %s 最新版本...(网络不通时最多约 1 分钟, 请留意按钮状态)" % repo)
    backend = backend or resolve_backend(profile)
    if job_log:
        job_log("按显卡挑选后端: %s" % backend)
    release = fetch_latest_llama_release(repo, backend, job_log)
    if job_log:
        job_log("最新: %s(build %d) 资产 %s" % (release["tag"], release["build"], release["asset_name"]))
    dir_ = dir_ or suggest_variant_dir(profile.get("LlamaDir"), repo.split("/")[0])
    if os.path.isfile(find_llama_server_exe(dir_) or ""):
        if job_log:
            job_log("⚠ 目标目录已含 llama-server,将覆盖安装;建议先清空防新旧混杂")
    ok, msg = apply_llama_update(dir_, release, job_log)
    if not ok:
        return False, msg
    if job_log:
        job_log(msg)
    entry = {"Name": name, "Repo": repo, "Dir": dir_}
    profile["LlamaVariants"] = [x for x in vs if x.get("Name") != name] + [entry]
    profile["ActiveLlamaVariant"] = name
    profile["LlamaDir"] = dir_
    return True, msg
