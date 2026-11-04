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
import threading
import time

import urllib.parse
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

# 资产名来自远端 JSON, 会被拼进 UPDATE_TMP_ROOT 下的路径:
# 绝对路径或 ../ 直接逃出缓存目录, 必须先过形态白名单(对齐 Windows IsSafeAssetName)。
RE_ASSET_NAME = re.compile(r"^[A-Za-z0-9._\-]+$")


def is_safe_asset_name(name):
    return bool(name) and name == os.path.basename(name) and ".." not in name \
        and bool(RE_ASSET_NAME.match(name))


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
    names = []
    for a in assets:
        n = a.get("name") or ""
        if not is_safe_asset_name(n):
            if n:
                applog.error("toolchain.pick_assets: 资产名形态非法, 已跳过: %s" % n)
            continue
        names.append(n)
    sizes = {a["name"]: a.get("size") or 0 for a in assets if a.get("name") in names}
    digests = {a["name"]: a.get("digest") or "" for a in assets if a.get("name") in names}
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
            "main_digest": digests.get(chosen, "") if chosen else "",
            "cudart": cudart_name,
            "cudart_size": sizes.get(cudart_name, 0) if cudart_name else 0,
            "cudart_digest": digests.get(cudart_name, "") if cudart_name else ""}


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
        "digest": pick.get("main_digest", ""),
        "cudart_name": pick["cudart"],
        "cudart_digest": pick.get("cudart_digest", ""),
        "cudart_url": next((a["browser_download_url"] for a in rel.get("assets", [])
                            if a["name"] == pick["cudart"]), "") if pick["cudart"] else "",
        "cudart_size": pick["cudart_size"],
        "stable_version": stable_version,
    }


def _gh_api_direct(path, timeout=15):
    """只走直连的 GitHub API GET(不经镜像): 把 release 摘要锚定到官方源。失败返回 None。"""
    try:
        req = urllib.request.Request(GITHUB_API + path, headers=UA)
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return json.loads(r.read().decode("utf-8", "replace"))
    except Exception:
        return None


def anchor_release_digests(release):
    """release JSON 可能经镜像取得(镜像与载荷同源, 可自证一致)。
    下载前尽力直连 api.github.com 复核资产摘要; 直连不通沿用现有摘要,
    摘要缺失时 download_asset 会 fail-closed(拒绝仅凭体积放行)。"""
    try:
        m = re.match(r"https://github\.com/([^/]+)/([^/]+)/releases/download/([^/]+)/([^/]+)",
                     release.get("asset_url") or "")
        if not m:
            return
        owner, repo, tag = m.group(1), m.group(2), m.group(3)
        doc = _gh_api_direct("/repos/%s/%s/releases/tags/%s"
                             % (owner, repo, urllib.parse.quote(tag, safe="")))
        if doc is None:
            applog.info("toolchain: 直连 api.github.com 不可达, 沿用当前通道摘要(可能来自镜像)")
            return
        for a in doc.get("assets") or []:
            name = a.get("name") or ""
            dig = a.get("digest") or ""
            if not dig:
                continue
            if name == release.get("asset_name"):
                old = release.get("digest") or ""
                if old and old != dig:
                    applog.error("toolchain: %s 的摘要与直连 GitHub 不一致(镜像可疑), 以直连为准" % name)
                release["digest"] = dig
            elif release.get("cudart_name") and name == release["cudart_name"]:
                release["cudart_digest"] = dig
    except Exception as ex:
        applog.error("toolchain.anchor_release_digests 异常: %s" % ex)


# ------------------------------------------------------------- 下载/安装 --

_active_downloads = 0
_dl_lock = threading.Lock()


def cleanup_update_tmp():
    """启动时清理缓存更新目录的崩溃残留(.part/解压目录)。
    有下载在途时跳过, 防止互删正在写入的文件。"""
    with _dl_lock:
        if _active_downloads:
            return
    try:
        shutil.rmtree(UPDATE_TMP_ROOT, ignore_errors=True)
    except Exception:
        pass


def _sha256_file(path):
    import hashlib
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def download_asset(url, dest, expected_size=0, job_log=None, timeout_secs=1200, expected_digest=""):
    """直连 → 三镜像;.part 临时文件 + 原子改名;长度 <1MB 拒收;±1% 对账。

    摘要缺失 fail-closed: 产物随后会被直接执行, "只看体积"不构成完整性保证。
    应急覆盖: 环境变量 LLAMADSH_ALLOW_UNVERIFIED_UPDATE=1。
    """
    if not expected_digest and os.environ.get("LLAMADSH_ALLOW_UNVERIFIED_UPDATE") != "1":
        raise IOError("该 release 资产无 sha256 摘要, 拒绝仅凭体积校验"
                      "(镜像链任一环都可能返回等长篡改内容); "
                      "应急可用环境变量 LLAMADSH_ALLOW_UNVERIFIED_UPDATE=1 覆盖(不建议)")
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
            if expected_digest:
                # GitHub asset digest(sha256:...)强校验: 镜像链任一环被篡改时体积对账挡不住等长内容
                want = expected_digest[7:] if expected_digest.lower().startswith("sha256:") else expected_digest
                got_hash = _sha256_file(part)
                if got_hash != want.lower():
                    raise IOError("SHA-256 不匹配(得 %s… 期望 %s…, 镜像内容被替换?)" % (got_hash[:16], want[:16]))
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
    # filter="data": 拒绝绝对路径成员/.. 穿越/指向解压目录外的软硬链, 剥离特权位
    # (3.12 不传 filter = fully_trusted 语义 + DeprecationWarning)
    with tarfile.open(archive, "r:gz") as tf:
        tf.extractall(dest_dir, filter="data")
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


def _sole_top_level_dir(root):
    """解压目录里**只有一个目录、没有文件**时返回它, 否则 None。

    上游 tar 都带一层归档根目录(llama-bNNNN-…/、cudart-llama-bNNNN-…/)。主包靠
    _find_dir_containing("llama-server") 找到它, 而没有可寻文件名的包(cudart)需要这个
    通用剥壳: 不剥就会把整棵根目录搬进去, 库文件与可执行文件不同目录。
    """
    try:
        names = os.listdir(root)
    except OSError:
        return None
    if len(names) != 1:
        return None
    full = os.path.join(root, names[0])
    return full if os.path.isdir(full) else None


def merge_cudart_tree(cdir, src_dir):
    """把解压好的 cudart 包并入 llama-* 目录, 返回 (.so 数, 说明文本)。

    两个坑叠在一起:
      1) cudart 包与主包一样带一层归档根目录(cudart-llama-bNNNN-bin-…-x64/)。以前直接
         copytree(cdir) ⇒ .so 落到 src_dir/cudart-…/ 里, 而 llama-server 的 rpath 是
         $ORIGIN ⇒ 运行时照样找不到 libcublas(安装流程还会把整棵目录当"用户额外文件"
         在下次更新时迁回来, 越攒越多)。
      2) 说明文本以前无条件写"已并入"。现在按**同目录里真的有 .so** 才这么说, 否则明说
         结构不对, 让用户知道 CUDA 版仍会缺库。
    """
    root = _sole_top_level_dir(cdir) or cdir
    _copy_dir_overwrite(root, src_dir)
    so = [fn for fn in os.listdir(src_dir) if ".so" in fn]
    if so:
        return len(so), " + cudart 运行库已并入(%d 个 .so 与 llama-server 同目录)" % len(so)
    return 0, (" ⚠ cudart 已解压并入, 但 llama-server 同目录里没有 .so(解压结构变化?)"
               " —— CUDA 版很可能仍报缺库")


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
    """覆盖前运行态预检。dir_hint 给定时只判定该目录的实例
    (变体 A 运行不应阻断变体 B 更新); 不给时保持全机判定。"""
    import glob as _glob
    hint = os.path.realpath(os.path.abspath(dir_hint)) if dir_hint else None
    for pdir in _glob.glob("/proc/[0-9]*"):
        try:
            with open(os.path.join(pdir, "comm")) as f:
                if f.read().strip() not in ("llama-server", "llama-server.exe"):
                    continue
            if hint is None:
                return True
            exe = os.path.realpath(os.path.join(pdir, "exe"))
            if exe.startswith(hint + os.sep) or exe.startswith(hint + "/"):
                return True
        except OSError:
            continue
    return False


def apply_llama_update(dir_, release, job_log=None):
    """下载 → 解压 → 覆盖 → build 硬校验。返回 (ok, msg)。"""
    global _active_downloads
    dir_ = os.path.abspath(dir_)
    # 纵深防御: 上游 pick_assets 已白名单, 拼路径前再兜底一次(远端元数据不可信)
    if not is_safe_asset_name(release.get("asset_name") or "") \
            or (release.get("cudart_name") and not is_safe_asset_name(release["cudart_name"])):
        return False, "release 元数据含非法资产名, 已拒绝下载(路径穿越防护)"
    os.makedirs(dir_, exist_ok=True)
    if llama_running(dir_):
        return False, "该目录的 llama-server 正在运行,请先停止再更新(防半套混合二进制)"
    os.makedirs(UPDATE_TMP_ROOT, exist_ok=True)  # 先建缓存目录再做磁盘预检
    need = max(release.get("size", 0) * 3, 512 * 1048576)
    need += release.get("cudart_size", 0)  # cudart 包大小原漏计
    ok, free, need = _disk_precheck(UPDATE_TMP_ROOT, need)
    if not ok:
        return False, "磁盘空间不足(需要约 %.1f GB,可用 %.1f GB)" % (need / GIB, free / GIB)
    # 目标盘与缓存盘可能不同分区: 解压结果最终要落到 dir_, 该盘空间也必须预检
    ok2, free2, _ = _disk_precheck(dir_, need)
    if not ok2:
        return False, "目标磁盘空间不足(需要约 %.1f GB,可用 %.1f GB)" % (need / GIB, free2 / GIB)
    # 在途下载计数: 并发实例(两个变体同时更新)完成时的 rmtree 不得删掉彼此
    # 正在写的 .part/资产文件; 归零才清整个缓存目录
    with _dl_lock:
        _active_downloads += 1
    anchor_release_digests(release)
    main_path = os.path.join(UPDATE_TMP_ROOT, release["asset_name"])
    try:
        download_asset(release["asset_url"], main_path, release.get("size", 0), job_log,
                       expected_digest=release.get("digest", ""))
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
                download_asset(release["cudart_url"], cudart_path, release.get("cudart_size", 0), job_log,
                               expected_digest=release.get("cudart_digest", ""))
                cdir = os.path.join(UPDATE_TMP_ROOT, "extract_cudart_%d" % release.get("build", 0))
                shutil.rmtree(cdir, ignore_errors=True)
                os.makedirs(cdir)
                _extract_tar(cudart_path, cdir)
                _so_n, cudart_note = merge_cudart_tree(cdir, src_dir)
                if _so_n == 0 and job_log:
                    job_log("[更新] ⚠ cudart 并入后 %s 里没有找到 .so" % src_dir)
            except Exception as ex:
                cudart_note = "(cudart 下载失败: %s — 启动 CUDA 版可能报缺库, 可改选 Vulkan 资产)" % ex
        # 覆盖安装原子化: 先整体装到 dir_ 同分区的暂存目录, 校验通过后再交换;
        # 失败/断电不留混合二进制(Windows 版"exe 占用预检"的 Linux 等价防护)
        stage = dir_ + ".stage-%d" % os.getpid()
        shutil.rmtree(stage, ignore_errors=True)
        os.makedirs(stage)
        for fn in os.listdir(src_dir):
            s = os.path.join(src_dir, fn)
            if os.path.isfile(s):
                shutil.copy2(s, os.path.join(stage, fn))
                try:
                    os.chmod(os.path.join(stage, fn), 0o755)
                except OSError:
                    pass
            elif os.path.isdir(s):
                _copy_dir_overwrite(s, os.path.join(stage, fn))
        # 落盘自检(d227f3c 硬校验): 暂存目录的 build 必须存在且与 release 一致,
        # 通过后才交换进正式目录(失败只丢暂存, 正式目录完好)
        lb = local_build(stage)
        if lb is None:
            shutil.rmtree(stage, ignore_errors=True)
            return False, "安装后 llama-server 未报告 build 号,安装可能不完整。建议删除该目录后重新安装。"
        rb = release.get("build", 0)
        if rb > 0 and lb != rb:
            shutil.rmtree(stage, ignore_errors=True)
            return False, ("本地 build %d 与上游 %d 不一致(目录里可能是分支/CI 构建)。"
                           "若加载模型报 ggml type 错误,请删除该目录后重新安装。" % (lb, rb))
        # 原子交换: 旧目录改名 .old → 暂存转正 → 用户额外文件(模型等)迁回 → 删旧。
        # 语义对齐 Windows 版"只动二进制不碰模型": swap 不丢 dir_ 里的非构建文件
        old_dir = dir_ + ".old-%d" % os.getpid()
        if os.path.exists(dir_):
            os.rename(dir_, old_dir)
        try:
            os.rename(stage, dir_)
        except OSError:
            if os.path.exists(old_dir) and not os.path.exists(dir_):
                os.rename(old_dir, dir_)  # 回滚
            raise
        # 迁回用户额外文件(新构建里没有的条目 = 用户放的模型/配置等)
        extras_warn = ""
        try:
            for entry in os.listdir(old_dir):
                dst = os.path.join(dir_, entry)
                if not os.path.exists(dst):
                    shutil.move(os.path.join(old_dir, entry), dst)
        except OSError as mv_ex:
            # 迁回失败不回滚安装(二进制已就绪), 但 .old 保留供用户手动找回
            extras_warn = "\n⚠ 部分旧目录文件未能迁回(%s), 原目录保留在: %s" % (mv_ex, old_dir)
        shutil.rmtree(old_dir, ignore_errors=True)
        msg = "llama.cpp 已更新到 %s(build %d)[%s]%s, 本地验证 build %d" % (
            release["tag"], rb, release["asset_name"], cudart_note, lb)
        if extras_warn:
            msg += extras_warn
        if "prismml" in (release.get("repo") or "").lower():
            msg += "\n⚠ PrismML 低比特内核仅覆盖 CUDA/Metal/CPU——AMD/Intel 显卡将走 CPU 回退(较慢)。"
        return True, msg
    except Exception as ex:
        # 契约是 (ok, msg): 下载/解压异常直接 raise 会穿透 run_job 之外的调用方
        return False, "安装失败: %s" % ex
    finally:
        with _dl_lock:
            _active_downloads -= 1
            if _active_downloads <= 0:
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
