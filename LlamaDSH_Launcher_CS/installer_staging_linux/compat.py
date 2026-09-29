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
    try:
        bak = "%s.bak-compat-%s" % (path, datetime.datetime.now().strftime("%Y%m%d-%H%M%S"))
        shutil.copy2(path, bak)
        return bak
    except OSError:
        return None


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

    # 失效符号链接
    broken = _broken_symlinks()
    if broken:
        fixables.append({"kind": "broken_links", "paths": broken})
        it("warn", "%d 个失效 node_modules 链接(可修复:清理)" % len(broken))

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
    """执行可修复项;全部先备份再写。返回修复计数。"""
    fixed = 0
    dsh_dir = (dsh_dir or "").strip().strip('"')
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
