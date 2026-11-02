# -*- coding: utf-8 -*-
"""DSH 兼容性检查/修复 + 插件智能检测/修复(精简版,等价原版 DshCompatService 的核心面)。

覆盖:环境检查、残留锁、--dump-config 探针、cordis.patch.yml 静态检查
(补丁改名映射 code-runtime→ptc-runtime、孤儿补丁)、manifest bundles、
插件 peer 兼容、node_modules 失效符号链接。全部修复"先备份后写入"。
"""
import os
import re
import glob
import json
import shutil
import datetime
import subprocess

import yaml

import procman

DSH_PORT = 3080
PATCH_ALIASES = {"code-runtime": "ptc-runtime", "code-runtime-worker-thread": "ptc-runtime"}
# DSH 自带的 bundle,不清理
BUILTIN_BUNDLES = {"@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app", "@deepseek-ai/dsh-headless",
                   "@deepseek-ai/dsh-acp-app", "@deepseek-ai/dsh-sdk-app", "@deepseek-ai/dsh-sdk-minimal",
                   "@deepseek-ai/experimental-voice-input-bundle", "@deepseek-ai/experimental-agent-team-profile"}


def dsh_home():
    return os.environ.get("DSH_HOME") or os.path.join(os.path.expanduser("~"), ".dsh")


def profile_dir(profile="web"):
    return os.path.join(dsh_home(), "profiles", profile)


def get_dsh_version(dsh_dir):
    for rel in (os.path.join("apps", "cli", "package.json"), "package.json"):
        p = os.path.join(dsh_dir, rel)
        try:
            with open(p, encoding="utf-8") as f:
                v = (json.load(f) or {}).get("version")
            if v:
                return v
        except Exception:
            continue
    return ""


def _backup(path):
    """备份带毫秒(同秒两次修复不再互相覆盖);失败返回 None(调用方对关键写回必须中止)。"""
    try:
        bak = "%s.bak-compat-%s" % (path, datetime.datetime.now().strftime("%Y%m%d-%H%M%S-%f")[:-3])
        shutil.copy2(path, bak)
        return bak
    except OSError:
        return None


def write_file_atomic(path, content):
    """tmp + os.replace 原子写: 进程被杀/断电不留截断文件(be42658)。"""
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(content)
    os.replace(tmp, path)


def read_json(path, default=None):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return default if default is not None else {}


def write_json_atomic(path, doc, backup=False):
    if backup:
        bak = _backup(path)
        if bak is None and os.path.exists(path):
            raise IOError("备份失败,中止写入(保护历史数据)")
    write_file_atomic(path, json.dumps(doc, indent=2, ensure_ascii=False))


def _parse_patch_entries(path):
    """粗解析 cordis.patch.yml:返回 [{index, id, raw_block}]。"""
    entries = []
    try:
        with open(path, encoding="utf-8") as f:
            doc = yaml.safe_load(f)
    except Exception:
        return entries
    if not isinstance(doc, dict):
        return entries
    def walk(node, in_patch=False):
        if isinstance(node, dict):
            this_id = node.get("id") or node.get("path")
            if in_patch and isinstance(this_id, str):
                entries.append(this_id)
            for k, v in node.items():
                walk(v, in_patch or k in ("patch", "patches"))
        elif isinstance(node, list):
            for v in node:
                walk(v, in_patch)
    walk(doc)
    return entries


def _dump_config_entries(dsh_dir, profile="web", timeout=180):
    """node bin.js --profile web --dump-config;返回 (entry_ids, stderr_tail)。"""
    binjs = os.path.join(dsh_dir, "apps", "cli", "lib", "bin.js")
    if not os.path.isfile(binjs):
        return None, "缺少 apps/cli/lib/bin.js"
    try:
        r = subprocess.run(["node", binjs, "--profile", profile, "--dump-config"],
                           capture_output=True, text=True, timeout=timeout, cwd=dsh_dir)
    except subprocess.TimeoutExpired:
        return None, "--dump-config 超时(%ds)" % timeout
    except FileNotFoundError:
        return None, "node 未安装"
    if r.returncode != 0:
        tail = "\n".join((r.stderr or "").strip().splitlines()[-20:])
        return None, "退出码 %d:\n%s" % (r.returncode, tail)
    ids = set()
    for m in re.finditer(r'"id"\s*:\s*"([^"]+)"', r.stdout or ""):
        ids.add(m.group(1))
    try:
        doc = json.loads(r.stdout)
        def walk(n):
            if isinstance(n, dict):
                i = n.get("id")
                if isinstance(i, str):
                    ids.add(i)
                for v in n.values():
                    walk(v)
            elif isinstance(n, list):
                for v in n:
                    walk(v)
        walk(doc)
    except Exception:
        pass
    return ids, ""


def _manifest_bundles(profile="web"):
    p = os.path.join(profile_dir(profile), "package.json")
    try:
        with open(p, encoding="utf-8") as f:
            doc = json.load(f)
        return (doc.get("dsh", {}).get("profile", {}).get("bundles")) or []
    except Exception:
        return []


def _broken_symlinks(profile="web"):
    out = []
    base = os.path.join(profile_dir(profile), "node_modules")
    patterns = [os.path.join(base, "*"), os.path.join(base, "@*", "*")]
    for pat in patterns:
        for p in glob.glob(pat):
            if os.path.islink(p) and not os.path.exists(p):
                out.append(p)
    return out


def audit(dsh_dir, log, include_dump=True):
    """返回报告 dict:items=[{level,text}], fixables=[{kind,detail,...}], version。"""
    items, fixables = [], []
    dsh_dir = (dsh_dir or "").strip().strip('"')

    def it(level, text):
        items.append({"level": level, "text": text})
        log("[兼容] %s%s" % ({"info": "·", "warn": "⚠", "error": "✗", "ok": "✓"}[level], text))

    if not dsh_dir or not os.path.isdir(dsh_dir):
        it("error", "DSH 目录不存在: %s" % dsh_dir)
        return {"items": items, "fixables": fixables, "version": ""}
    it("ok", "目录: %s" % dsh_dir)
    if not os.path.isdir(os.path.join(dsh_dir, ".git")):
        it("warn", "不是 git 仓库(无法做升级基线检测)")
    ver = get_dsh_version(dsh_dir)
    it("ok" if ver else "warn", "版本: %s" % (ver or "未知"))
    for exe, name in (("node", "node"), ("pnpm", "pnpm")):
        it("ok" if procman.which(exe) else "error", "%s: %s" % (name, procman.which(exe) or "未找到"))
    binjs = os.path.join(dsh_dir, "apps", "cli", "lib", "bin.js")
    it("ok" if os.path.isfile(binjs) else "error",
       "编译产物 apps/cli/lib/bin.js: %s" % ("存在" if os.path.isfile(binjs) else "缺失(需要全量重建)"))

    # 残留锁
    lp = os.path.join(dsh_home(), "task-board", "ledger-v2.lock")
    if os.path.exists(lp) and not procman.find_process_by_port(DSH_PORT) and not procman.node_host_running():
        fixables.append({"kind": "stale_lock", "path": lp})
        it("warn", "残留锁文件: %s(可修复:删除)" % lp)

    # manifest bundles
    mb = _manifest_bundles()
    unknown = [b for b in mb if b not in BUILTIN_BUNDLES]
    it("info", "manifest bundles: %s" % (", ".join(mb) if mb else "(空)"))
    if unknown:
        it("ok", "第三方 bundle: %s" % ", ".join(unknown))

    # cordis.patch.yml 静态检查
    patch_path = os.path.join(profile_dir(), "cordis.patch.yml")
    patch_ids = []
    if os.path.isfile(patch_path):
        patch_ids = _parse_patch_entries(patch_path)
        it("info", "cordis.patch.yml: %d 条补丁" % len(patch_ids))

    # dump-config 探针
    if include_dump and os.path.isfile(binjs):
        ids, err = _dump_config_entries(dsh_dir)
        if ids is None:
            it("error", "--dump-config 探针失败: %s" % err.splitlines()[0] if err else "--dump-config 探针失败")
            fixables.append({"kind": "dump_failed", "detail": err})
        else:
            it("ok", "--dump-config 正常(%d 个 entry)" % len(ids))
            for pid_ in patch_ids:
                if pid_ in ids:
                    continue
                mapped = PATCH_ALIASES.get(pid_)
                if mapped and mapped in ids:
                    fixables.append({"kind": "patch_rename", "old": pid_, "new": mapped})
                    it("warn", "补丁条目 '%s' 已被 '%s' 取代(可修复:改名迁移)" % (pid_, mapped))
                else:
                    fixables.append({"kind": "patch_orphan", "old": pid_})
                    it("warn", "补丁条目 '%s' 在 dump-config 中不存在(可修复:注释失效条目)" % pid_)
            # 入口注入审计(C# 4878-4945): dump 行 → 入口文件 → inject 服务名/运行时 ctx 访问
            ver = ver or get_dsh_version(dsh_dir)
            inject_fixes = audit_inject(dsh_dir, ver, it, fixables)
            if inject_fixes:
                fixables.extend(inject_fixes)

    # 失效符号链接
    broken = _broken_symlinks()
    if broken:
        fixables.append({"kind": "broken_links", "paths": broken})
        it("warn", "%d 个失效 node_modules 链接(可修复:清理)" % len(broken))

    # 锁定停用回写强制(C# 1979): 锁定包被外部写回 bundles → 报错 + 纳入修复
    pinned = read_pinned_inactive()
    manifest = _profile_manifest() or {}
    bundles_now = ((manifest.get("dsh") or {}).get("profile") or {}).get("bundles") or []
    for p in pinned:
        if p in bundles_now:
            fixables.append({"kind": "pinned_writeback", "name": p})
            it("error", "插件被外部重新激活: %s(锁定中;可修复:再次移除并保持锁定)" % p)

    # .dsh-market disabled
    state_p = os.path.join(profile_dir(), ".dsh-market", "state.json")
    if os.path.isfile(state_p):
        try:
            with open(state_p, encoding="utf-8") as f:
                dis = (json.load(f) or {}).get("disabled") or []
            if dis:
                fixables.append({"kind": "market_disabled", "path": state_p, "count": len(dis)})
                it("warn", "dsh-market 写入了 %d 条 disabled 状态(可修复:清空)" % len(dis))
        except Exception:
            pass

    if not fixables:
        it("ok", "复检结果:0 错误 / 0 可修复项")
    return {"items": items, "fixables": fixables, "version": ver}


def repair(dsh_dir, report, log):
    """执行可修复项;全部先备份再写。返回修复计数。
    868253e: 传入的 report 可能是确认框前的陈旧快照,入口先重审一次,
    以重审结果贯穿全部修复决策。"""
    fixed = 0
    dsh_dir = (dsh_dir or "").strip().strip('"')
    try:
        report = audit(dsh_dir, lambda _t: None)
    except Exception:
        pass
    for f in report.get("fixables", []):
        kind = f["kind"]
        try:
            if kind == "stale_lock":
                os.remove(f["path"])
                log("[兼容] 已删除残留锁: %s" % f["path"])
                fixed += 1
            elif kind == "patch_rename" or kind == "patch_orphan":
                patch_path = os.path.join(profile_dir(), "cordis.patch.yml")
                if os.path.isfile(patch_path):
                    bak = _backup(patch_path)
                    with open(patch_path, encoding="utf-8") as fh:
                        text = fh.read()
                    old = f["old"]
                    if kind == "patch_rename":
                        new = f["new"]
                        text = re.sub(r"(\b(?:id|path)\s*:\s*[\"']?)%s([\"'?]|\s|$)" % re.escape(old),
                                      lambda m: m.group(1) + new + m.group(2), text)
                        # 改名映射的配置键(computeMs→timeoutMs 等)
                        text = text.replace("computeMs", "timeoutMs").replace("maxWallMs", "maxTimeoutMs")
                        log("[兼容] 补丁改名 %s → %s(备份 %s)" % (old, new, bak))
                    else:
                        lines = text.splitlines(keepends=True)
                        out, skip = [], False
                        for ln in lines:
                            if re.match(r"\s*-\s*(?:id|path)\s*:\s*[\"']?%s\b" % re.escape(old), ln):
                                out.append("# [compat] 已失效条目,自动注释: " + ln)
                                skip = True
                                continue
                            if skip:
                                if re.match(r"\s{4,}\S", ln):
                                    out.append("# [compat] " + ln)
                                    continue
                                skip = False
                            out.append(ln)
                        text = "".join(out)
                        log("[兼容] 已注释失效补丁 '%s'(备份 %s)" % (old, bak))
                    with open(patch_path, "w", encoding="utf-8") as fh:
                        fh.write(text)
                    fixed += 1
            elif kind == "broken_links":
                for p in f["paths"]:
                    try:
                        os.remove(p)
                        log("[兼容] 已清理失效链接: %s" % p)
                        fixed += 1
                    except OSError:
                        pass
            elif kind == "pinned_writeback":
                set_bundle_active("web", f["name"], False)  # 移除 + 保持锁定
                log("[兼容] 已再次移除锁定包并保持锁定: %s" % f["name"])
                fixed += 1
            elif kind == "dead_peer":
                pj = f["plugin_pkg"]
                doc = read_json(pj, None)
                if doc:
                    (doc.get("peerDependencies") or {}).pop(f["peer"], None)
                    bak = _backup(pj)
                    if bak:
                        write_file_atomic(pj, json.dumps(doc, indent=2, ensure_ascii=False))
                        log("[兼容] 已从 %s 依赖声明移除死包 %s(备份 %s)" % (f["plugin"], f["peer"], bak))
                        fixed += 1
            elif kind == "inject_fix":
                path = f["file"]
                if not os.path.isfile(path):
                    log("[兼容] 入口文件不存在,跳过: %s" % path)
                    continue
                bak = _backup(path)
                if not bak:
                    log("[兼容] 备份失败,中止注入修复: %s" % path)
                    continue
                text = open(path, encoding="utf-8", errors="replace").read()
                repl = f.get("replacement") or ""
                if f.get("runtime_access"):
                    text2, n = rename_ctx_service_access(text, f["service"], repl)
                    hint = "运行时 ctx.%s 改名 → ctx.%s" % (f["service"], repl) if (n and repl) else "无替代,需人工"
                else:
                    text2, n = remove_from_inject_service(text, f["service"], repl)
                    hint = ("inject %s 改名 → %s" % (f["service"], repl)) if (n and repl) else ("inject 移除 %s" % f["service"])
                if n:
                    write_file_atomic(path, text2)
                    log("[兼容] 注入修复 %d 处: %s(%s,备份 %s)" % (n, path, hint, bak))
                    fixed += 1
                else:
                    log("[兼容] 注入无可改写命中: %s(%s)" % (path, hint))
            elif kind == "market_disabled":
                bak = _backup(f["path"])
                with open(f["path"], encoding="utf-8") as fh:
                    doc = json.load(fh)
                doc["disabled"] = []
                with open(f["path"], "w", encoding="utf-8") as fh:
                    json.dump(doc, fh, indent=2, ensure_ascii=False)
                log("[兼容] 已清空 dsh-market disabled(%d 条,备份 %s)" % (f.get("count", 0), bak))
                fixed += 1
        except Exception as ex:
            log("[兼容] 修复项 %s 执行失败: %s" % (kind, ex))
    return fixed


def plugin_check(dsh_dir, log):
    """插件智能检测:遍历 ~/.dsh/profiles/web/node_modules 的插件,检查状态。"""
    items = []
    dsh_dir = (dsh_dir or "").strip().strip('"')
    base = os.path.join(profile_dir(), "node_modules")
    bundles = set(_manifest_bundles())
    disabled = set()
    state_p = os.path.join(profile_dir(), ".dsh-market", "state.json")
    if os.path.isfile(state_p):
        try:
            with open(state_p, encoding="utf-8") as f:
                disabled = set((json.load(f) or {}).get("disabled") or [])
        except Exception:
            pass
    count = 0
    for pat in (os.path.join(base, "*"), os.path.join(base, "@*", "*")):
        for pdir in glob.glob(pat):
            pj = os.path.join(pdir, "package.json")
            if not os.path.isfile(pj):
                continue
            try:
                with open(pj, encoding="utf-8") as f:
                    doc = json.load(f)
            except Exception:
                continue
            name = doc.get("name") or ""
            if not name or name.startswith("@types"):
                continue
            count += 1
            peer = (doc.get("peerDependencies") or {})
            status = []
            if name in disabled:
                status.append("已停用(dsh-market)")
            elif name in bundles:
                status.append("bundle 已激活")
            if peer:
                # 简化 peer 检查:版本段存在性
                status.append("peer: " + ", ".join("%s@%s" % kv for kv in list(peer.items())[:2]))
            link = "链接" if os.path.islink(pdir) else "目录"
            if os.path.islink(pdir) and not os.path.exists(pdir):
                status.append("✗ 失效链接")
            items.append({"name": name, "note": "; ".join(status) or "已安装(%s)" % link})
    log("[插件] 共扫描 %d 个插件" % count)
    return {"items": items, "count": count}


# ============================================================ Semver 判定 ==

def _semver_parts(v):
    v = (v or "").strip()
    v = v[1:] if v.startswith("v") else v
    m = re.match(r"^(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:-([0-9A-Za-z.-]+))?", v)
    if not m:
        return None
    maj = int(m.group(1))
    mnr = int(m.group(2) or 0)
    pat = int(m.group(3) or 0)
    pre = m.group(4)
    return (maj, mnr, pat, pre)


def _semver_cmp(a, b):
    pa, pb = _semver_parts(a), _semver_parts(b)
    if pa is None or pb is None:
        return 0
    if pa[:3] != pb[:3]:
        return -1 if pa[:3] < pb[:3] else 1
    # 无 pre-release > 有 pre-release
    if pa[3] is None and pb[3] is None:
        return 0
    if pa[3] is None:
        return 1
    if pb[3] is None:
        return -1
    return -1 if pa[3] < pb[3] else (1 if pa[3] > pb[3] else 0)


def semver_satisfies(version, range_str):
    """离线 semver satisfies(简化版 caret/tilde/比较符/||/x 通配)。"""
    range_str = (range_str or "").strip()
    if not range_str or range_str in ("*", "latest", "workspace:^", "workspace:*"):
        return True
    if range_str.startswith("workspace:"):
        range_str = range_str[len("workspace:"):]
    version = (version or "").strip()
    if not version:
        return False
    for alt in range_str.split("||"):
        alt = alt.strip()
        if not alt:
            continue
        if alt in ("*", "latest"):
            return True
        # AND 区间(空格分隔的多个比较符须全部成立)
        comparators = alt.split()
        if all(_sat_one(version, c_) for c_ in comparators):
            return True
    return False


def _sat_one(version, alt):
    m = re.match(r"^(>=|<=|>|<|=|\^|~)?\s*(.+)$", alt)
    if not m:
        return False
    op, target = m.group(1), m.group(2).strip()
    # x 通配/不完整版本: 1.x / 1.2.x / 0.1
    xmatch = re.match(r"^(\d+)(?:\.(\d+|x|\*))?(?:\.(\d+|x|\*))?", target)
    if xmatch and ("x" in target or "*" in target or (target.count(".") < 2 and not op)):
        maj = int(xmatch.group(1))
        mnr = xmatch.group(2)
        pv = _semver_parts(version)
        if pv is None:
            return False
        if mnr in (None, "x", "*"):
            return pv[0] == maj
        if mnr.isdigit():
            return pv[0] == maj and pv[1] == int(mnr)
        return False
    pt = _semver_parts(target)
    if pt is None:
        return False
    c = _semver_cmp(version, target)
    if op in (None, "="):
        if c == 0:
            return True
        # 裸前缀(如 "0.1.7" 常按 ^0.1.7 语义)
        if target.count(".") < 2 and c > 0:
            pv = _semver_parts(version)
            nxt = (pt[0], pt[1] + 1, 0)
            return bool(pv and pt[:3] <= pv[:3] < nxt)
        return False
    if op == "^":
        pv = _semver_parts(version)
        if pv is None:
            return False
        low = pt[:3]
        if pt[0] > 0:
            high = (pt[0] + 1, 0, 0)
        elif pt[1] > 0:
            high = (0, pt[1] + 1, 0)
        else:
            high = (0, 0, pt[2] + 1)
        return low <= pv[:3] < high
    if op == "~":
        pv = _semver_parts(version)
        if pv is None:
            return False
        return pt[:3] <= pv[:3] < (pt[0], pt[1] + 1, 0)
    if op == ">=":
        return c >= 0
    if op == "<=":
        return c <= 0
    if op == ">":
        return c > 0
    if op == "<":
        return c < 0
    return False


# ======================================================= 插件审计与管理 ==

PINNED_PATH = os.path.join(dsh_home(), "pinned_plugins.json")
COMPAT_JSON = "compatibility.json"



# ==================================================== 插件管理(对齐 C#) ====
# 锁定停用持久化: {profile: [插件名…]}(C# PinnedPluginsPath 同格式)。
# DSH 网页端/市场会把 bundle 写回 dsh.profile.bundles, 审计发现锁定包被写回时
# 自动移除并提示(C# 1979-2000)。

import urllib.request as _urlreq  # noqa: E402

UA_HEADER = "LlamaDSH-Launcher"
NPM_REGISTRIES = ("https://registry.npmjs.org", "https://registry.npmmirror.com")


def pinned_plugins_path():
    return os.path.join(dsh_home(), "pinned_plugins.json")


def read_pinned_inactive(profile="web"):
    doc = read_json(pinned_plugins_path(), None)
    if not isinstance(doc, dict):
        return []
    arr = doc.get(profile)
    return [x for x in arr if isinstance(x, str) and x] if isinstance(arr, list) else []


def pin_inactive(profile, name):
    doc = read_json(pinned_plugins_path(), {})
    if not isinstance(doc, dict):
        doc = {}
    lst = doc.get(profile) or []
    if name not in lst:
        lst.append(name)
    doc[profile] = lst
    write_json_atomic(pinned_plugins_path(), doc, backup=True)


def unpin_inactive(profile, name):
    doc = read_json(pinned_plugins_path(), {})
    if not isinstance(doc, dict) or name not in (doc.get(profile) or []):
        return
    doc[profile] = [x for x in doc[profile] if x != name]
    write_json_atomic(pinned_plugins_path(), doc, backup=True)


def _profile_manifest(profile="web"):
    return read_json(os.path.join(profile_dir(profile), "package.json"), None)


def _profile_dep_spec(profile, name):
    """插件在 profile package.json 中的依赖声明(file:/link: 表示本地安装)。

    (C# DshPluginInfo.Spec 同语义; 空显示 "(bundle)")"""
    m = _profile_manifest(profile) or {}
    deps = m.get("dependencies") or {}
    return deps.get(name, "")


def set_bundle_active(profile, name, active):
    """激活 = 加回 dsh.profile.bundles + UnpinInactive;停用 = 移出 + PinInactive。
    只改 package.json 不跑 pnpm(b63884c/aec1e9b);写前备份 + 原子写。"""
    p = os.path.join(profile_dir(profile), "package.json")
    doc = _profile_manifest(profile)
    if doc is None:
        return False, "manifest 不存在: %s" % p
    bundles = ((doc.get("dsh") or {}).get("profile") or {}).get("bundles")
    if bundles is None:
        doc.setdefault("dsh", {}).setdefault("profile", {})["bundles"] = bundles = []
    changed = False
    if active and name not in bundles:
        bundles.append(name)
        changed = True
        unpin_inactive(profile, name)
    elif not active and name in bundles:
        bundles.remove(name)
        changed = True
        pin_inactive(profile, name)
    if changed:
        write_json_atomic(p, doc, backup=True)
    return True, ("已激活(DSH 重启后生效)" if active
                  else "已停用并锁定(DSH 重启后生效;被外部写回时启动器审计会自动移除)")


def filter_bundle_activation(candidates, plugins):
    """bundle 自动激活守卫(3af19b9): peer 错位/豁免仍不兼容/缺依赖/错误/被锁定的包
    不自动激活——激活会导致 client 模块加载失败(module table 漂移)。"""
    kept, demoted = [], []
    pinned = set(read_pinned_inactive())
    for b in candidates:
        info = next((x for x in plugins if x.get("name") == b), None)
        bad, why = False, ""
        if info:
            if info.get("compatible") is False:
                bad, why = True, "peer 兼容性未确认"
            if info.get("missing_specs"):
                bad, why = True, "存在缺失依赖 (%s)" % ", ".join(info["missing_specs"][:3])
            if info.get("errors"):
                bad, why = True, "存在错误"
        if b in pinned:
            bad, why = True, "已被用户锁定停用"
        (demoted if bad else kept).append({"bundle": b, "reason": why} if bad else b)
    return kept, demoted


# ---------------------------------------------------- 死依赖(C# 2249-2350) --

_shipped_cache = {}


def _manifest_name(path):
    doc = read_json(path, None)
    if isinstance(doc, dict):
        n = doc.get("name")
        if isinstance(n, str) and n:
            return n
    return None


def shipped_dsh_package_names(dsh_dir):
    """DSH 源码树中所有 workspace 包名(packages/apps/vendor/native/benchmarks/website)。"""
    key = os.path.abspath(dsh_dir)
    if key in _shipped_cache:
        return _shipped_cache[key]
    names = set()
    root_name = _manifest_name(os.path.join(dsh_dir, "package.json"))
    if root_name:
        names.add(root_name)
    for root in ("packages", "apps", "vendor", "native", "benchmarks", "website"):
        base = os.path.join(dsh_dir, root)
        if not os.path.isdir(base):
            continue
        stack = [base]
        while stack:
            d = stack.pop()
            n = _manifest_name(os.path.join(d, "package.json"))
            if n:
                names.add(n)
            try:
                for sub in os.scandir(d):
                    if sub.is_dir() and sub.name not in (".pnpm", "node_modules"):
                        stack.append(sub.path)
            except OSError:
                continue
    _shipped_cache[key] = names
    return names


def is_dsh_package_dead(package_name, dsh_dir, profile_dir_):
    """上游已删除的 DSH 包: 所有 node_modules farm 均未安装, 且源码树无该 workspace 包。
    只判定 @deepseek-ai/ 命名空间(C# IsDshPackageDead 同规则)。"""
    prefix = "@deepseek-ai/"
    if not package_name.startswith(prefix):
        return False
    short = package_name[len(prefix):]
    farms = [os.path.join(profile_dir_, "node_modules")]
    profile_parent = os.path.dirname(os.path.abspath(profile_dir_))
    farms.insert(0, os.path.join(profile_parent, "node_modules"))  # 共享 DSH 运行时 farm
    farms += [os.path.join(dsh_dir, *parts) for parts in
              (("node_modules",), ("apps", "cli", "node_modules"), ("apps", "web", "node_modules"),
               ("apps", "desktop", "node_modules"), ("apps", "desktop-host", "node_modules"))]
    for farm in farms:
        if os.path.isdir(os.path.join(farm, "@deepseek-ai", short)):
            return False
    return package_name not in shipped_dsh_package_names(dsh_dir)


# ------------------------------------------------------- 门禁(C# EvaluateGate) --

def evaluate_gate(latest_meta, dsh_version):
    """插件最新版对当前 DSH 运行时的门禁: engines.dsh + 全部 @deepseek-ai/dsh* peer。
    版本未知时不判定(避免误报)。返回 (blocked, gate_name, gate_range)。"""
    v = (dsh_version or "").strip().lstrip("vV")
    if not v or not v[:1].isdigit():
        return (False, "", "")
    engines = latest_meta.get("engines") or {}
    gate_range = engines.get("dsh") or ""
    if gate_range and not semver_satisfies(v, gate_range):
        return (True, "engines.dsh", gate_range)
    peers = latest_meta.get("peerDependencies") or {}
    for k, rng in peers.items():
        if k == "@deepseek-ai/dsh" or k.startswith("@deepseek-ai/dsh-"):
            if not semver_satisfies(v, rng):
                return (True, "peer " + k, rng)
    return (False, "", "")


# ---------------------------------------------------- 结构化审计(audit) --

def audit_plugins(profile="web", dsh_dir=""):
    """逐插件结构化审计(对齐 C# DshPluginInfo 的 UI 字段)。

    state_label: 已启用/已停用/已锁定停用/被外部重新激活;spec = profile 依赖声明;
    optional peer 不参与判定;dead peers 单列(修复 = 从依赖声明移除)。"""
    dsh_dir = (dsh_dir or "").strip().strip('"')
    pdir = profile_dir(profile)
    base = os.path.join(pdir, "node_modules")
    manifest = _profile_manifest(profile) or {}
    in_bundles = set(((manifest.get("dsh") or {}).get("profile") or {}).get("bundles") or [])
    deps = manifest.get("dependencies") or {}
    exempt_doc = read_json(os.path.join(pdir, COMPAT_JSON), {})
    pinned = set(read_pinned_inactive(profile))
    market_doc = read_json(os.path.join(pdir, ".dsh-market", "state.json"), {})
    market_disabled = set(market_doc.get("disabled") or [])
    ver = get_dsh_version(dsh_dir)
    out = []
    # 只列直接依赖(用户/市场安装的插件);pnpm 传递依赖(argparse/undici 等)
    # 不是插件, 列出来标"已停用"会误导
    direct_deps = set(deps.keys())
    for pat in (os.path.join(base, "*"), os.path.join(base, "@*", "*")):
        for pdir_ in sorted(glob.glob(pat)):
            pj = os.path.join(pdir_, "package.json")
            if not os.path.isfile(pj):
                continue
            try:
                with open(pj, encoding="utf-8") as f:
                    doc = json.load(f)
            except Exception:
                continue
            name = doc.get("name") or ""
            if not name or name.startswith("@types"):
                continue
            if name not in direct_deps:
                continue
            version = doc.get("version") or "?"
            peer = doc.get("peerDependencies") or {}
            peer_meta = doc.get("peerDependenciesMeta") or {}
            peer_issues = {}
            dead_peers = []
            for dep, rng in peer.items():
                if peer_meta.get(dep, {}).get("optional"):
                    continue  # optional peer 不参与探针(C# 同语义)
                if dep.startswith("@deepseek-ai/"):
                    if is_dsh_package_dead(dep, dsh_dir, pdir):
                        dead_peers.append(dep)
                        continue
                installed = _installed_version(base, dep)
                if installed and not semver_satisfies(installed, rng):
                    peer_issues[dep] = rng
            exempted = version in (exempt_doc.get(name) or [])
            missing_specs = []
            for dep in (doc.get("dependencies") or {}):
                if (doc.get("dependencies") or {})[dep].startswith("file:"):
                    continue
                if _installed_version(base, dep) is None:
                    missing_specs.append(dep)
            errors = []
            if os.path.islink(pdir_) and not os.path.exists(pdir_):
                errors.append("失效链接")
            if name in market_disabled:
                errors.append("dsh-market 停用记录残留")
            if name in in_bundles and name in pinned:
                state_label = "被外部重新激活"
            elif name in pinned:
                state_label = "已锁定停用"
            else:
                state_label = "已启用" if name in in_bundles else "已停用"
            out.append({
                "name": name, "version": version, "dir": pdir_,
                "state_label": state_label,
                "spec": deps.get(name, "") or "(bundle)",
                "in_bundles": name in in_bundles, "installed": True,
                "compatible": (not peer_issues) or exempted,
                "exempted": exempted,
                "peer_issues": [{"dep": k, "range": v,
                                 "installed": _installed_version(base, k) or "?"}
                                for k, v in peer_issues.items()],
                "dead_peers": dead_peers,
                "missing_specs": missing_specs, "errors": errors,
                "locked": name in pinned,
                "disabled_by_market": name in market_disabled,
            })
    out.sort(key=lambda x: (0 if x["in_bundles"] else 1, x["name"]))
    return out


def enforce_pinned(profile="web", log=None):
    """锁定回写强制(C# 1979): 审计时发现锁定包被写回 bundles → 报错并纳入修复。
    这里直接执行移除(备份+原子写), 返回被移除的名字列表。"""
    manifest_p = os.path.join(profile_dir(profile), "package.json")
    doc = _profile_manifest(profile)
    if doc is None:
        return []
    bundles = ((doc.get("dsh") or {}).get("profile") or {}).get("bundles") or []
    pinned = read_pinned_inactive(profile)
    write_back = [p for p in pinned if p in bundles]
    if write_back:
        bak = _backup(manifest_p)
        for p in write_back:
            bundles.remove(p)
        write_json_atomic(manifest_p, doc, backup=False)  # 已备份
        if log:
            log("[插件] 锁定包被外部写回, 已自动移除: %s(备份 %s)" % (", ".join(write_back), bak))
    return write_back


def _installed_version(base, dep):
    pj = os.path.join(base, dep, "package.json")
    try:
        with open(pj, encoding="utf-8") as f:
            return (json.load(f) or {}).get("version")
    except Exception:
        return None


# ------------------------------------------------- 升级预检(C# PreviewPluginUpdate) --

def _registry_query(name):
    for reg in NPM_REGISTRIES:
        try:
            req = _urlreq.Request("%s/%s" % (reg, name), headers={"User-Agent": UA_HEADER})
            with _urlreq.urlopen(req, timeout=15) as r:
                meta = json.loads(r.read().decode("utf-8", "replace"))
            latest = (meta.get("dist-tags") or {}).get("latest")
            if latest:
                return reg, latest, (meta.get("versions") or {}).get(latest) or {}
        except Exception:
            continue
    return None, None, None


def preview_plugin_update(profile, name, dsh_dir=""):
    """升级预检: registry 最新版 + gate 判定 + 最新版死 peer。
    registry 最新版 ≤ 已装 → up_to_date(不虚报成功)。"""
    dsh_dir = (dsh_dir or "").strip().strip('"')
    pdir = profile_dir(profile)
    base = os.path.join(pdir, "node_modules")
    installed = _installed_version(base, name)
    if not installed:
        return {"error": "插件未安装: %s" % name}
    reg, latest, latest_meta = _registry_query(name)
    if reg is None:
        return {"error": "registry 查询失败(npmjs 与 npmmirror 均不可达)"}
    if _semver_cmp(latest, installed) <= 0:
        return {"up_to_date": True, "installed": installed, "latest": latest}
    dsh_version = get_dsh_version(dsh_dir) if dsh_dir else ""
    blocked, gate_name, gate_range = evaluate_gate(latest_meta, dsh_version)
    dead_peers = []
    for dep in (latest_meta.get("peerDependencies") or {}):
        if dep.startswith("@deepseek-ai/") and is_dsh_package_dead(dep, dsh_dir, pdir):
            dead_peers.append(dep)
    concern = ""
    if blocked:
        concern = "升级可能被 DSH 门禁拒绝(%s %s)" % (gate_name, gate_range)
    elif dead_peers:
        concern = "最新版仍声明已删除的依赖: %s" % ", ".join(dead_peers)
    return {"installed": installed, "latest": latest, "registry": reg,
            "gate_blocked": blocked, "gate_name": gate_name, "gate_range": gate_range,
            "dead_peers": dead_peers, "concern": concern}


def _pnpm_env():
    env = dict(os.environ)
    pnpm_home = os.path.expanduser("~/.local/share/pnpm")
    env["PNPM_HOME"] = pnpm_home
    env["PATH"] = pnpm_home + os.pathsep + env.get("PATH", "")
    return env


def _pnpm_add(cwd, name, spec):
    # 注意: pnpm 的 CLI 不认 --fetch-timeout(那是 npm 参数), 传了会直接
    # "unexpected argument" 拒绝执行——超时放宽走 .npmrc/PNPM_HOME 配置, 不走这里
    if not procman_which_pnpm():
        return False, "pnpm 不在 PATH(可先在 Node 环境面板一键安装)"
    r = subprocess.run(["pnpm", "add", name + spec],
                       cwd=cwd, capture_output=True, text=True, timeout=900, env=_pnpm_env())
    return r.returncode == 0, (r.stdout or "") + (r.stderr or "")


def procman_which_pnpm():
    import procman as _p
    return _p.which("pnpm")


def _pnpm_remove(cwd, name):
    r = subprocess.run(["pnpm", "remove", name], cwd=cwd, capture_output=True,
                       text=True, timeout=600, env=_pnpm_env())
    return r.returncode == 0, (r.stdout or "") + (r.stderr or "")


def upgrade_plugin(profile, name, dsh_dir="", job_log=None):
    """升级插件: 预检(gate/死 peer)→ pnpm add @latest(registry 失败换 npmmirror)
    → 读回实际版本 → 死 peer 依赖声明清理。"""
    prev = preview_plugin_update(profile, name, dsh_dir)
    if prev.get("error"):
        return False, prev["error"]
    if prev.get("up_to_date"):
        return True, "已是最新(%s)" % prev.get("installed")
    base = profile_dir(profile)
    if job_log:
        job_log("升级 %s: %s → %s" % (name, prev.get("installed"), prev.get("latest")))
    ok, out = _pnpm_add(base, name, "@latest")
    if not ok:
        return False, "pnpm add 失败: %s" % out[-300:]
    got = _installed_version(os.path.join(base, "node_modules"), name)
    if got and prev.get("latest") and _semver_cmp(got, prev["latest"]) < 0:
        if job_log:
            job_log("⚠ 实际安装版本 %s 低于 registry latest %s" % (got, prev["latest"]))
    # 升级后清理死 peer 依赖声明(基于重审)
    pj = os.path.join(base, "node_modules", name, "package.json")
    doc = read_json(pj, None)
    if doc and dsh_dir:
        peers = doc.get("peerDependencies") or {}
        dead = [d for d in peers if d.startswith("@deepseek-ai/")
                and is_dsh_package_dead(d, dsh_dir, base)]
        if dead:
            bak = _backup(pj)
            if bak:
                for d in dead:
                    (doc.get("peerDependencies") or {}).pop(d, None)
                write_file_atomic(pj, json.dumps(doc, indent=2, ensure_ascii=False))
                if job_log:
                    job_log("已清理死 peer 依赖声明: %s(备份 %s)" % (", ".join(dead), bak))
    return True, "已升级 %s → %s" % (prev.get("installed"), got)


def uninstall_plugin(profile, name):
    ok, out = _pnpm_remove(profile_dir(profile), name)
    if not ok:
        return False, "pnpm remove 失败: %s" % out[-300:]
    return True, "已卸载 %s" % name


def install_local_plugin(profile, source, job_log=None):
    """本地安装: 目录 → pnpm pack 出稳定 tgz;或直接给 .tgz/.tar.gz。"""
    source = (source or "").strip()
    tgz = source
    if not procman_which_pnpm():
        return False, "pnpm 不在 PATH(可先在 Node 环境面板一键安装)"
    if os.path.isdir(source):
        if job_log:
            job_log("pnpm pack %s ..." % source)
        r = subprocess.run(["pnpm", "pack"], cwd=source, capture_output=True, text=True,
                           timeout=300, env=_pnpm_env())
        if r.returncode != 0:
            return False, "pnpm pack 失败: %s" % ((r.stderr or r.stdout or "")[-300:])
        # 输出末尾若干行里找 .tgz 名(不同 pnpm 版本还会打 "Tarball Details" 等节)
        fname = ""
        for ln in reversed((r.stdout or "").strip().splitlines()):
            ln = ln.strip()
            if ln.endswith(".tgz") and "/" not in ln and "\\" not in ln:
                fname = ln
                break
        pkg = os.path.join(source, fname)
        if not fname or not os.path.isfile(pkg):
            return False, "pnpm pack 未产出 tgz(输出: %s)" % (r.stdout or "")[-200:]
        tgz = pkg
    if not os.path.isfile(tgz) or not tgz.endswith((".tgz", ".tar.gz")):
        return False, "需要插件目录或 .tgz 包: %s" % tgz
    ok, out = _pnpm_add(profile_dir(profile), tgz, "")
    if not ok:
        return False, "安装失败: %s" % out[-300:]
    return True, "已从 %s 安装" % os.path.basename(tgz)


def exempt_plugin(profile, name, version):
    """版本豁免写入 compatibility.json(备份失败即中止;29be3c6)。"""
    p = os.path.join(profile_dir(profile), COMPAT_JSON)
    doc = read_json(p, {})
    doc.setdefault(name, [])
    if version not in doc[name]:
        doc[name].append(version)
    write_json_atomic(p, doc, backup=True)
    return True, "已豁免 %s@%s" % (name, version)


# ============================================ 入口注入检测/改写(C# 3996-4950) ==

def _find_inject_array_spans(text):
    """定位 inject[:=] [ ... ] 数组体(括号配平, 上限 20000 字符)。"""
    spans = []
    if not text:
        return spans
    for m in re.finditer(r"(?<![\w$.])inject\s*[:=]\s*\[", text):
        open_ = m.end() - 1
        depth = 0
        for k in range(open_, min(len(text), open_ + 20000)):
            if text[k] == "[":
                depth += 1
            elif text[k] == "]":
                depth -= 1
                if depth == 0:
                    spans.append((open_ + 1, k - open_ - 1))
                    break
    return spans


def extract_inject_names(js_text):
    """inject 数组里的字符串字面量服务名。"""
    names = []
    text = js_text or ""
    for start, length in _find_inject_array_spans(text):
        if start < 0 or length <= 0:
            continue
        body = text[start:start + length]
        for m in re.finditer(r"['\"](?P<n>[A-Za-z_$][A-Za-z0-9_$.-]*)['\"]", body):
            n = m.group("n")
            if n and n not in names:
                names.append(n)
    return names


def is_in_code_at_contexts(text, positions):
    """轻量 JS 词法状态机(C# IsInCodeAtContext): 一次扫描判定多个位置是否为
    代码上下文(非字符串/模板/注释)。不识别正则字面量(已知局限)。"""
    positions = sorted(set(positions))
    result = {p: True for p in positions}
    pi = 0
    sq = dq = bt = line_c = block_c = False
    i, n = 0, len(text)
    while i < n:
        while pi < len(positions) and positions[pi] <= i:
            result[positions[pi]] = not (line_c or block_c or sq or dq or bt)
            pi += 1
        c = text[i]
        nxt = text[i + 1] if i + 1 < n else ""
        if line_c:
            if c == "\n":
                line_c = False
            i += 1
            continue
        if block_c:
            if c == "*" and nxt == "/":
                block_c = False
                i += 2
                continue
            i += 1
            continue
        if sq or dq or bt:
            q = sq and "'" or dq and '"' or "`"
            if c == "\\":
                i += 2
                continue
            if c == q:
                sq = dq = bt = False
            i += 1
            continue
        if c == "/" and nxt == "/":
            line_c = True
            i += 2
            continue
        if c == "/" and nxt == "*":
            block_c = True
            i += 2
            continue
        if c == "'":
            sq = True
        elif c == '"':
            dq = True
        elif c == "`":
            bt = True
        i += 1
    while pi < len(positions):
        result[positions[pi]] = not (line_c or block_c or sq or dq or bt)
        pi += 1
    return result


def _ctx_service_access_regex(service):
    esc = re.escape(service)
    return re.compile(
        "\\bctx\\??\\.(" + esc + ")\\b"
        + "|\\bctx\\??\\[\\s*(['\"])(" + esc + ")\\2\\s*\\]"
        + "|\\bctx\\??\\.get\\(\\s*(['\"])(" + esc + ")\\4\\s*\\)")


def has_ctx_service_access(text, service):
    if not text or not service:
        return False
    ms = list(_ctx_service_access_regex(service).finditer(text))
    states = is_in_code_at_contexts(text, [m.start() for m in ms])
    return any(states[m.start()] for m in ms)


def rename_ctx_service_access(text, service, replacement):
    """把运行时代码里的 ctx.<旧服务> 改写为 ctx.<新服务>;字符串/注释内不动。"""
    rx = _ctx_service_access_regex(service)
    ms = list(rx.finditer(text))
    if not ms:
        return text, 0
    states = is_in_code_at_contexts(text, [m.start() for m in ms])
    count = 0
    out = []
    last = 0
    for m in ms:
        if not states[m.start()]:
            continue
        out.append(text[last:m.start()])
        out.append(m.group(0).replace(service, replacement))
        last = m.end()
        count += 1
    out.append(text[last:])
    return "".join(out), count


def remove_from_inject_service(text, service, replacement):
    """只改 inject 数组内的字符串字面量(改名/留空删名), 绝不碰运行时引用。"""
    count = 0
    spans = _find_inject_array_spans(text)
    out = text
    for start, length in reversed(spans):
        body = out[start:start + length]
        def sub(m):
            nonlocal count
            n = m.group("n")
            if n == service:
                count += 1
                return ("'%s'" % replacement) if replacement else ""
            return m.group(0)
        new_body = re.sub(r"['\"](?P<n>[A-Za-z_$][A-Za-z0-9_$.-]*)['\"]", sub, body)
        if new_body != body:
            out = out[:start] + new_body + out[start + length:]
    return out, count


def try_resolve_dead_service(service, dsh_version):
    """当前 DSH 永不解析的服务名(C# TryResolveDeadService)。
    返回 (是否死服务, 原因, 替代名)。"""
    if service == "logger":
        return (True, "cordis 4 里 ctx.logger 是上下文普通属性、不是可注入服务(ctx.get('logger') 为 undefined)", "")
    if service == "webRuntime" and semver_satisfies(dsh_version or "", ">=0.2.1-alpha.1"):
        return (True, "DSH %s 起 Web profile 不再提供 webRuntime,改由 web-startup 行提供 webStartup" % (dsh_version or "?"), "webStartup")
    if service == "invariants" and semver_satisfies(dsh_version or "", ">=0.2.0-rc.2"):
        return (True, "@deepseek-ai/dsh-invariants 与 invariants 服务自 DSH 0.2.0-rc.2 起已整包删除", "")
    return (False, "", "")


def audit_inject(dsh_dir, dsh_version, it, fixables):
    """入口注入审计(C# 4856-4950): dump 行定位入口 → inject 服务名/运行时 ctx 访问
    → 死服务解析(logger/webRuntime→webStartup/invariants)。返回 fixables。"""
    rows, err = dump_config_rows(dsh_dir)
    out = []
    if rows is None:
        it("warn", "注入审计跳过: %s" % (err or "dump 不可用"))
        return out
    seen_files = set()
    seen_keys = set()
    resolved = 0

    def add_fix(entry, service, replacement, runtime_access, row_id, module):
        key = entry.lower() + "|" + service
        if key in seen_keys:
            return
        seen_keys.add(key)
        fix = {"kind": "inject_fix", "file": entry, "service": service,
               "replacement": replacement, "runtime_access": runtime_access}
        out.append(fix)
        if runtime_access:
            fixable = bool(replacement)
            hint = ("把入口文件里对 ctx.%s 的访问改名为 ctx.%s(改前自动备份)" % (service, replacement)
                    if fixable else "入口运行时代码仍访问已删除服务 ctx.%s,无替代,需人工处理" % service)
            it("error", "入口访问已删除的服务: %s → ctx.%s(%s)" % (row_id, service, hint))
        else:
            hint = ("把入口 inject 里的 %s 改名为 %s(改前自动备份)" % (service, replacement)
                    if replacement else "从入口 inject 里删除 %s(改前自动备份)" % service)
            it("error", "插件行 inject 永不解析: %s → %s(%s)" % (row_id, service, hint))

    pdir = profile_dir()
    for row in rows:
        entry = row.get("entry") or ""
        if not entry:
            continue
        path = entry if os.path.isabs(entry) else None
        if path is None:
            for cand in (os.path.join(dsh_dir, entry), os.path.join(pdir, entry),
                         os.path.join(dsh_dir, "apps", "cli", entry)):
                if os.path.isfile(cand):
                    path = cand
                    break
        if not path or not os.path.isfile(path) or path in seen_files:
            continue
        seen_files.add(path)
        resolved += 1
        try:
            text = open(path, encoding="utf-8", errors="replace").read()
        except OSError:
            continue
        row_id = row.get("id") or "?"
        module = row.get("module") or "?"
        for service in extract_inject_names(text):
            dead, reason, replacement = try_resolve_dead_service(service, dsh_version)
            if dead:
                add_fix(path, service, replacement, False, row_id, module)
        for candidate in ("webRuntime", "invariants"):
            dead, reason, replacement = try_resolve_dead_service(candidate, dsh_version)
            if dead and has_ctx_service_access(text, candidate):
                add_fix(path, candidate, replacement, True, row_id, module)
    it("info", "inject 检查: dump 行 %d 个,定位入口 %d 个,问题 %d 项" % (len(rows), resolved, len(out)))
    return out


def dump_config_rows(dsh_dir, profile="web", timeout=180):
    """--dump-config 的行(id/module/entry), 供注入审计定位入口文件。"""
    binjs = os.path.join(dsh_dir, "apps", "cli", "lib", "bin.js")
    if not os.path.isfile(binjs):
        return None, "缺少 apps/cli/lib/bin.js"
    try:
        r = subprocess.run(["node", binjs, "--profile", profile, "--dump-config"],
                           capture_output=True, text=True, timeout=timeout, cwd=dsh_dir)
    except subprocess.TimeoutExpired:
        return None, "--dump-config 超时(%ds)" % timeout
    except FileNotFoundError:
        return None, "node 未安装"
    if r.returncode != 0:
        return None, "退出码 %d: %s" % (r.returncode, "\n".join((r.stderr or "").strip().splitlines()[-5:]))
    rows = []

    def walk(n):
        if isinstance(n, dict):
            if isinstance(n.get("id"), str) and (n.get("entry") or n.get("module")):
                rows.append({"id": n.get("id"), "module": n.get("module") or "",
                             "entry": n.get("entry") or ""})
            for v in n.values():
                walk(v)
        elif isinstance(n, list):
            for v in n:
                walk(v)
    try:
        walk(json.loads(r.stdout or "{}"))
    except Exception:
        pass
    return rows, ""
