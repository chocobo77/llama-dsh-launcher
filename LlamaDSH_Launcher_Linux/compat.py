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
import time
import shutil
import threading
import datetime
import subprocess
import tempfile

import yaml

import procman

DSH_PORT = 3080
PATCH_ALIASES = {"code-runtime": "ptc-runtime", "code-runtime-worker-thread": "ptc-runtime"}
# DSH 自带的 bundle,不清理
BUILTIN_BUNDLES = {"@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app", "@deepseek-ai/dsh-headless",
                   "@deepseek-ai/dsh-acp-app", "@deepseek-ai/dsh-sdk-app", "@deepseek-ai/dsh-sdk-minimal",
                   "@deepseek-ai/experimental-voice-input-bundle", "@deepseek-ai/experimental-agent-team-profile"}


def dsh_home():
    # abspath/expanduser: DSH_HOME 可能被写成相对路径或带 ~, 不展开时后续所有
    # 拼接都跟着 cwd 走(同一份 profile 会从两个位置被读写)
    raw = os.environ.get("DSH_HOME") or os.path.join(os.path.expanduser("~"), ".dsh")
    return os.path.abspath(os.path.expanduser(raw))


def profile_dir(profile="web"):
    p = str(profile or "")
    # 请求体直传的 profile 名: 拒绝路径穿越(/ \ ..), 否则 write_json/package.json
    # 会被写到 ~/.dsh/profiles 之外(payload: profile="../../x")。
    if (not p or p in (".", "..") or p != os.path.basename(p)
            or any(c in p for c in '\\/:*?"<>|\r\n') or p.startswith(".")):
        raise ValueError("非法 profile 名: %r" % (profile,))
    return os.path.join(dsh_home(), "profiles", p)


def get_dsh_version(dsh_dir):
    dsh_dir = (dsh_dir or "").strip().strip('"')
    if not dsh_dir:
        # 空 dsh_dir 时 os.path.join("", "package.json") 退化成相对路径 → 读到
        # **当前工作目录**里某个无关 package.json 的版本号(启动器 cwd 就在自己目录,
        # 但被别的调用方换过 cwd 时会拿到假版本)
        return ""
    for rel in (os.path.join("apps", "cli", "package.json"), "package.json"):
        p = os.path.join(dsh_dir, rel)
        try:
            with open(p, encoding="utf-8-sig") as f:
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
        # utf-8-sig: Windows/node 写的 package.json 可能带 BOM, 用 utf-8 读会
        # 在首字符塞一个 ‌ → json 解析失败, 于是整个插件被当成"没有 package.json"
        with open(path, encoding="utf-8-sig") as f:
            return json.load(f)
    except Exception:
        # 注意: 传 None **不会**拿到 None —— 兜底是 `default if default is not None else {}`,
        # 而签名默认就是 None。要区分"文件是空的"与"文件读不动"必须用 read_json_or_none。
        return default if default is not None else {}


def read_json_or_none(path):
    """读 JSON, 失败(不存在/无权限/截断/坏编码)返回 None。

    与 read_json 的区别就是那个 None: 凡是据此判断"这个文件到底能不能核对"的地方
    (插件启停状态、市场停用记录)都必须用本函数, 用 read_json 会把读不动当成空内容。
    """
    try:
        with open(path, encoding="utf-8-sig") as f:
            return json.load(f)
    except Exception:
        return None


def write_json_atomic(path, doc, backup=False):
    if backup:
        bak = _backup(path)
        if bak is None and os.path.exists(path):
            raise IOError("备份失败,中止写入(保护历史数据)")
    write_file_atomic(path, json.dumps(doc, indent=2, ensure_ascii=False))


def _parse_patch_entries(path):
    """粗解析 cordis.patch.yml:返回 ([{index, id, raw_block}], 失败原因)。

    原因非空表示**读不动**(编码错/YAML 语法错/顶层不是映射)。以前这种情况返回空列表,
    审计于是打印"cordis.patch.yml: 0 条补丁"并在结尾给出"0 错误 / 0 可修复项"——
    一个坏掉的文件被报成"一切正常", patch_orphan/rename 检查也整段跳过(假绿)。
    """
    entries = []
    try:
        with open(path, encoding="utf-8") as f:
            doc = yaml.safe_load(f)
    except Exception as ex:
        return entries, "%s: %s" % (type(ex).__name__, ex)
    if not isinstance(doc, dict):
        return entries, "顶层不是映射(实际是 %s)" % type(doc).__name__
    return _walk_patch_entries(doc), ""


def _walk_patch_entries(doc):
    entries = []
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
    # 检查项本身没跑成(文件读不动)与"检查过且没问题"是两回事, 分开计数免得假绿
    unreadable = []
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
    if os.path.exists(lp):
        # 判定要三态: "确认没在跑"才是残留锁, "查不到"不是。把探针失效当成空闲,
        # 就会给出"可修复:删除"的建议, 一键删掉活实例正在用的账本锁。
        _act = procman.dsh_activity(DSH_PORT)
        if _act is False:
            fixables.append({"kind": "stale_lock", "path": lp})
            it("warn", "残留锁文件: %s(可修复:删除)" % lp)
        elif _act is None:
            it("warn", "锁文件存在但无法判定是否残留(端口/进程探针不可用), 不提供自动删除; "
                       "请先确认 DSH 已停止, 再手工删除: %s" % lp)

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
        patch_ids, why = _parse_patch_entries(patch_path)
        if why:
            it("error", "cordis.patch.yml 读不动(%s) —— 本次无法核对补丁条目" % why)
            unreadable.append("cordis.patch.yml")
        else:
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
        except Exception as ex:
            it("error", "dsh-market/state.json 读不动(%s: %s) —— 无法核对 disabled 状态"
               % (type(ex).__name__, ex))
            unreadable.append(".dsh-market/state.json")

    if not fixables:
        # "没有可修复项"不等于"一切正常": 有些项是**没能跑成**(文件读不动), 还有些是
        # 查出来但无法自动修的错误。旧写法一律报"0 错误 / 0 可修复项", 用户据此认为环境
        # 干净就去做下一步(装插件), 这是典型的假绿。
        _errs = sum(1 for x in items if x["level"] == "error")
        if unreadable:
            it("warn", "复检结果:0 可修复项, 但有 %d 项未能核对(%s), 不要据此认为一切正常"
               % (len(unreadable), ", ".join(unreadable)))
        elif _errs:
            it("warn", "复检结果:0 可修复项, 但有 %d 项错误(见上面 ✗), 需要手工处理" % _errs)
        else:
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
    except Exception as ex:
        # 重审失败必须说出来: 否则整轮修的是弹窗时的**陈旧快照**(新出现的问题没修、
        # 已经消失的项被再改写), 而日志上只剩"已修复 N 项", 看不出这次修的不是现状。
        log("[兼容] ⚠ 修复前重审失败(%s: %s), 本次按弹窗时的旧清单修复; "
            "结束后请重新审计一次" % (type(ex).__name__, ex))
    for f in report.get("fixables", []):
        kind = f["kind"]
        try:
            if kind == "stale_lock":
                # 删锁前**再确认一次**: 弹窗到点修复之间 DSH 可能已经被启动, 探针也可能
                # 在这期间失效。删掉活实例的 ledger-v2.lock 不是"清理", 是给第二个实例
                # 让位 —— 两本账同时写就是数据损坏, 而且现场看起来"一切正常"。
                _act = procman.dsh_activity(DSH_PORT)
                if _act is not False:
                    log("[兼容] ⚠ 未删除 %s: %s" % (
                        f["path"],
                        "端口/进程探针不可用, 无法确认 DSH 已停止" if _act is None
                        else "DSH 仍在运行, 这把锁是活的"))
                    continue
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
    state_why = ""
    if os.path.isfile(state_p):
        try:
            with open(state_p, encoding="utf-8") as f:
                disabled = set((json.load(f) or {}).get("disabled") or [])
        except Exception as ex:
            # 读不动不等于"没有被停用的插件": 静默当成空集合, 被市场停用的插件会全部
            # 显示成正常状态, 用户据此以为插件都在跑。
            state_why = "%s: %s" % (type(ex).__name__, str(ex)[:80])
            items.append({"name": "dsh-market",
                          "note": "⚠ state.json 读不动(%s), 本次无法核对哪些插件被市场停用"
                                  % state_why})
    count = 0
    unreadable = []
    for pat in (os.path.join(base, "*"), os.path.join(base, "@*", "*")):
        for pdir in glob.glob(pat):
            pj = os.path.join(pdir, "package.json")
            if not os.path.isfile(pj):
                continue
            try:
                with open(pj, encoding="utf-8") as f:
                    doc = json.load(f)
            except Exception as ex:
                # 以前这里 continue: 一个被截断/带 BOM/权限不对的 package.json 会让该插件
                # **从列表里消失**, 结尾还照报"共扫描 N 个插件" —— 坏插件被扫成不存在。
                unreadable.append((os.path.relpath(pdir, base),
                                   "%s: %s" % (type(ex).__name__, str(ex)[:80])))
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
    for rel, why in unreadable:
        items.append({"name": rel,
                      "note": "⚠ package.json 读不动(%s), 无法判定该插件状态" % why})
    if unreadable:
        log("[插件] ⚠ %d 个插件目录的 package.json 读不动(不计入上面的计数): %s"
            % (len(unreadable), ", ".join("%s(%s)" % r for r in unreadable[:5])))
    log("[插件] 共扫描 %d 个插件%s" % (count, "、另有 %d 个未能核对" % len(unreadable)
                                       if unreadable else ""))
    return {"items": items, "count": count, "unreadable": [r[0] for r in unreadable],
            "market_state_unreadable": bool(state_why)}


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
    """激活/停用插件(改 manifest); 与装/卸/升级共用同一把 profile 锁串行。

    审计里的 pinned 回写、pnpm 安装回滚都会读改写同一份 package.json —— 并发的两条
    写会互相覆盖(表现为"激活没生效"或"依赖又回来了")。
    """
    if not begin_profile_op(profile, ("激活" if active else "停用") + "插件 %s" % name):
        return False, _BUSY_MSG
    try:
        return _set_bundle_active_locked(profile, name, active)
    finally:
        end_profile_op(profile)


def _set_bundle_active_locked(profile, name, active):
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


def filter_bundle_activation(candidates, plugins, profile="web"):
    """bundle 自动激活守卫(3af19b9): peer 错位/豁免仍不兼容/缺依赖/错误/被锁定的包
    不自动激活——激活会导致 client 模块加载失败(module table 漂移)。"""
    kept, demoted = [], []
    pinned = set(read_pinned_inactive(profile))
    for b in candidates:
        info = next((x for x in plugins if x.get("name") == b), None)
        bad, why = False, ""
        if info:
            if info.get("compatible") is False:
                bad, why = True, "peer 兼容性未确认"
            elif info.get("exempted") and info.get("peer_issues"):
                # C# FilterBundleActivation(2693): **豁免过版本检查 ≠ peer 真的对上了**。
                # audit_plugins 里 compatible = (not peer_issues) or exempted, 所以只看
                # compatible 会让豁免过的错位包通过守卫被自动激活, 后果就是 C# 注释写的
                # client module table 漂移、DSH 启动时模块加载失败。
                bad, why = True, "peer 与当前 DSH 版本不匹配(版本检查已豁免)"
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
    dsh_dir = (dsh_dir or "").strip().strip('"')
    if not dsh_dir:
        return set()          # 不知道 DSH 在哪: 空集比"把启动器自己当成源码树"诚实
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
    # None 与 {} 必须分开: {} 是"没有被停用的插件", None 是"读不动"。读不动时每个插件的
    # 启用/停用判定都没有依据, 静默当成 {} 会把被市场停用的插件一律显示成"已启用"。
    _mp = os.path.join(pdir, ".dsh-market", "state.json")
    market_doc = read_json_or_none(_mp) if os.path.isfile(_mp) else {}
    market_disabled = set((market_doc or {}).get("disabled") or [])
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
            except Exception as ex:
                # 依赖里声明了它, 但 package.json 读不动(截断/带 BOM/权限/磁盘满): 以前
                # 直接 continue ⇒ 插件列表里**整个插件消失**, 面板显示"没有这个插件",
                # 用户只会看到 DSH 启动时它加载失败。必须作为 error 条目列出来。
                rel = os.path.relpath(pdir_, base)
                if rel in direct_deps:
                    out.append({
                        "name": rel, "version": "?", "dir": pdir_,
                        "state_label": "无法核对", "spec": deps.get(rel, "") or "(bundle)",
                        "in_bundles": rel in in_bundles, "installed": True,
                        "compatible": False, "exempted": False, "peer_issues": [],
                        "dead_peers": [], "missing_specs": [],
                        "errors": ["package.json 读不动(%s: %s)"
                                   % (type(ex).__name__, str(ex)[:100])],
                        "locked": rel in pinned,
                        "disabled_by_market": rel in market_disabled,
                    })
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
    if market_doc is None:
        out.append({
            "name": ".dsh-market/state.json", "version": "?", "dir": _mp,
            "state_label": "无法核对", "spec": "", "in_bundles": False,
            "installed": False, "compatible": False, "exempted": False,
            "peer_issues": [], "dead_peers": [], "missing_specs": [],
            "errors": ["市场停用记录读不动 → 各插件的启用/停用状态本次未能核对"],
            "locked": False, "disabled_by_market": False,
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
    # ~/.local/bin 也要进子进程 PATH: corepack/npm 安装的 pnpm 就在那儿, 而启动器自己的
    # PATH 常常不含它(GNOME 自启、systemd-user、sudo)。只加 PNPM_HOME 会覆盖一半路径。
    env["PATH"] = (pnpm_home + os.pathsep + os.path.expanduser("~/.local/bin")
                   + os.pathsep + env.get("PATH", ""))
    return env


# 取包超时放宽(C# InstallLocalPlugin/UpgradePlugin 传 --fetch-timeout=600000)。
# pnpm 的 CLI **不认** `--fetch-timeout`(实测 12.16.1: "unexpected argument"), 但认
# rc-option 形式 `--config.fetch-timeout=...`; 写在参数**末尾**(不影响调用方按下标取
# spec)。镜像/网络差时这条决定"卡 60 秒失败"还是"等到取到为止"。
_NET_FLAGS = ["--config.fetch-timeout=600000"]


_PKG_RE = re.compile(r"^(?:@[A-Za-z0-9._~-]+/)?[A-Za-z0-9_~][A-Za-z0-9._~-]*$")


def _safe_pkg(name):
    """包名形态白名单: 未校验的 name 可注入 --force/registry 等 pnpm 参数。"""
    return bool(_PKG_RE.match(str(name or "")))


def _pnpm_add(cwd, name, spec):
    if not _safe_pkg(name):
        return False, "非法包名: %r" % (name,)
    if not procman_which_pnpm():
        return False, "pnpm 不在 PATH(可先在 Node 环境面板一键安装)"
    r = subprocess.run(["pnpm", "add", name + spec] + _NET_FLAGS, cwd=cwd,
                       capture_output=True,
                       text=True, encoding="utf-8", errors="replace",
                       timeout=900, env=_pnpm_env())
    return r.returncode == 0, (r.stdout or "") + (r.stderr or "")


def procman_which_pnpm():
    import procman as _p
    return _p.which("pnpm")


def _pnpm_remove(cwd, name):
    if not _safe_pkg(name):
        return False, "非法包名: %r" % (name,)
    r = subprocess.run(["pnpm", "remove", name] + _NET_FLAGS, cwd=cwd,
                       capture_output=True,
                       text=True, encoding="utf-8", errors="replace",
                       timeout=600, env=_pnpm_env())
    return r.returncode == 0, (r.stdout or "") + (r.stderr or "")


def upgrade_plugin(profile, name, dsh_dir="", job_log=None):
    """升级插件; 与装/卸/激活共用 profile 锁(升级 = pnpm remove+add 的变体)。"""
    if not begin_profile_op(profile, "升级插件 %s" % name):
        return False, _BUSY_MSG
    try:
        return _upgrade_plugin_locked(profile, name, dsh_dir, job_log)
    finally:
        end_profile_op(profile)


def _upgrade_plugin_locked(profile, name, dsh_dir="", job_log=None):
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
    """卸载 = 先停用并锁定(移出 dsh.profile.bundles + pin), 再 pnpm remove。

    C# UninstallPlugin(735-751) 的第一步就是 SetBundleActive(false)。Linux 以前只跑
    pnpm remove, 于是 bundles 里仍指向已删掉的包 —— DSH 启动/入口补丁全报 entry not
    found, 下一次审计还会把它当"已启用的插件"来审。
    """
    if not begin_profile_op(profile, "卸载插件 %s" % name):
        return False, _BUSY_MSG
    try:
        ok_off, msg_off = set_bundle_active(profile, name, False)
        ok, out = _pnpm_remove(profile_dir(profile), name)
        if not ok:
            return False, ("pnpm remove 失败: %s; bundle 已先停用(%s), 依赖声明未变"
                           % (out[-300:], msg_off if ok_off else "停用也失败"))
        return True, "已卸载 %s(%s)" % (name, msg_off)
    finally:
        end_profile_op(profile)


# ---------------------------------------------------- 本地插件安装(C# InstallLocalPlugin) --
# 打包忽略项必须分两族(C# TryPackStagedPackage 对**所有**条目都套 PackIgnoredNames):
# ① _PACK_ALWAYS: 结构性目录, 任何情况都不能进 tgz —— node_modules 既炸体积又与 pnpm
#    的依赖树冲突(npm 自己也永不打它), .git/.pnpm 同理。作者就算把它写进 files 也不带。
# ② _PACK_SOFT: 开发期目录, 默认甩掉; 但插件把**运行期资产**放在名为 scripts/ 的目录
#    并显式写进 files 时必须原样带上(否则打出的是缺资产的包)。
# C# 用 OrdinalIgnoreCase, 这里也按小写比对, 否则 Tests/Docs 会被打进包。
_PACK_ALWAYS = {"node_modules", ".git", ".pnpm", ".pnpm-store", ".svn", "__pycache__"}
_PACK_SOFT = {"tests", "test", "docs", "scripts", ".github", ".vscode", ".idea",
              "coverage", "pnpm-lock.yaml", "pnpm-workspace.yaml", ".npmrc", ".gitignore"}
_PACK_IGNORE = _PACK_ALWAYS | _PACK_SOFT


_PACK_LINK_SKIP = []      # 本次打包被甩掉的"指向包外"符号链接(供 _pack_staged 留痕)


def _links_skipped():
    """取走并清空本次打包甩掉的越界符号链接清单(与 _OP_IMBALANCE 同一手法)。"""
    out = list(_PACK_LINK_SKIP)
    del _PACK_LINK_SKIP[:]
    return out


def _escapes(src_dir, path):
    """path(解析符号链接后)是否落在 src_dir 之外。判不准时按"越界"处理。"""
    try:
        root = os.path.realpath(src_dir)
        tgt = os.path.realpath(path)
    except OSError:
        return True
    return tgt != root and not tgt.startswith(root + os.sep)


def _ignore_fn(lower_set, src_dir=None):
    """shutil.copytree 的 ignore 回调: 按小写名比对(与 C# 的大小写无关语义一致)。

    src_dir 给定时**额外甩掉指向包目录之外的符号链接**: copytree 默认跟随符号链接,
    把目标内容原样复制进暂存目录 —— `dist/assets -> ../../secrets` 这类链接会把包外
    的东西(别人的密钥、另一个 profile 的配置、另一棵 node_modules)打进 tgz。包随后被
    装进别的 profile 或发给别人, 就成了外带 + 体积爆炸。npm pack 对包外链接同样是拒绝。
    """
    def _pick(_dir, names):
        skip = [n for n in names if n.lower() in lower_set]
        if src_dir:
            for n in names:
                if n in skip:
                    continue
                p = os.path.join(_dir, n)
                if os.path.islink(p) and _escapes(src_dir, p):
                    skip.append(n)
                    _PACK_LINK_SKIP.append(os.path.relpath(p, src_dir))
        return skip
    return _pick


def _profile_deps(base):
    """profile 的 dependencies 字典(安装前后做 diff 用)。"""
    return (read_json(os.path.join(base, "package.json"), None) or {}).get("dependencies") or {}


def _package_identity(src_dir):
    doc = read_json(os.path.join(src_dir, "package.json"), None) or {}
    return str(doc.get("name") or "").strip(), str(doc.get("version") or "").strip()


def _tarball_identity(tgz):
    """从 tgz 里读 name/version —— 只认包根那一层的 package.json。

    两种打包布局都要认: npm 的 `package/package.json`, 以及裸根的 `package.json`。
    取**最浅**的那一层, 且深度 >1 一律不算: 否则裸根包里的 examples/package.json
    会被当成本体(它和 npm 布局的包根前缀在路径深度上长得一样)。
    """
    import tarfile
    try:
        with tarfile.open(tgz, "r:gz") as tf:
            cands = []
            for m in tf.getmembers():
                n = m.name.replace("\\", "/")
                if n.startswith("/") or ".." in n.split("/"):
                    # 绝对路径 / .. 穿越成员一律不认: 否则 `../package.json` 这种"包根上一层"
                    # 会被当成包身份(实测旧版返回它的 name/version), 一个自制 tgz 就能把
                    # 安装记录的 name/version 伪造成任意值, 卸错目标、盖掉别的条目。
                    # (解压侧 toolchain._extract_tar 用 filter="data" 也会拒这类成员。)
                    continue
                while n.startswith("./"):
                    n = n[2:]
                if not n.endswith("package.json"):
                    continue
                rel = n[: -len("package.json")].rstrip("/")
                depth = 0 if not rel else rel.count("/") + 1
                if depth <= 1:
                    cands.append((depth, m))
            if not cands:
                return "", ""
            cands.sort(key=lambda x: x[0])
            fh = tf.extractfile(cands[0][1])
            if fh is None:
                return "", ""
            doc = json.loads(fh.read().decode("utf-8-sig", "replace"))
            return str(doc.get("name") or "").strip(), str(doc.get("version") or "").strip()
    except Exception:
        pass
    return "", ""


def _missing_build_entry(src_dir):
    """package.json 的 main 指向的产物不存在 → 返回该相对路径(先提示用户去 build)。"""
    doc = read_json(os.path.join(src_dir, "package.json"), None) or {}
    main = str(doc.get("main") or "").strip()
    if not main:
        return None
    full = os.path.join(src_dir, main.replace("/", os.sep))
    return None if os.path.isfile(full) else main


def _newest_tgz(directory, not_before=None):
    """目录里最新的 .tgz。

    not_before: 只认这个时间戳之后产生的文件 —— 否则上一次失败留下的旧包会被当成
    本次 pnpm pack 的产物装进 profile(内容还是上个版本的)。
    """
    best, best_mt = None, None
    for path in glob.glob(os.path.join(directory, "*.tgz")):
        try:
            mt = os.path.getmtime(path)
        except OSError:
            continue                      # 列完就没了(并发清理), 跳过而不是让整次安装炸掉
        if not_before is not None and mt < not_before:
            continue
        if best is None or mt > best_mt:
            best, best_mt = path, mt
    return best


def _rel_path(value):
    """package.json 里 files/bundle.patch 这类相对路径: 剥 ./ 前缀并拒绝越界。

    不能用 lstrip("./"): 它按**字符集**剥, 会把 ".hidden/x" 削成 "hidden/x"。
    """
    v = str(value or "").strip().replace("\\", "/")
    while v.startswith("./"):
        v = v[2:]
    v = v.strip("/")
    if not v or v.startswith("/") or ".." in v.split("/") or v.startswith("!"):
        return ""
    return v


def _pack_entries(src_dir, declared):
    """package.json 的 files 数组 → 要复制的相对路径(npm 语义)。

    带通配(* ? [)的条目要 glob 展开, 否则 "dist/*.js" 会被当成一个不存在的字面路径,
    结果是暂存包里什么产物都没有; "!" 开头是排除项(npm), 不参与复制。
    """
    lowered = {str(x).lower() for x in _PACK_IGNORE}
    always = {str(x).lower() for x in _PACK_ALWAYS}
    if not isinstance(declared, list):
        return [leaf for leaf in sorted(os.listdir(src_dir)) if leaf.lower() not in lowered]
    out = []
    for raw in declared:
        entry = _rel_path(raw)
        if not entry:
            continue
        if any(ch in entry for ch in "*?["):
            for hit in glob.glob(os.path.join(src_dir, entry)):
                rel = os.path.relpath(hit, src_dir)
                # 通配命中的是**作者声明**的条目: 只挡结构性目录(node_modules/.git/…),
                # 不能拿整份 _PACK_IGNORE 去过滤, 否则声明了 "scripts/*.js" 反而什么
                # 都收不到(而不声明 scripts 时它只是被甩掉) —— 两条路必须一致。
                if rel not in (".", "") and os.path.basename(rel).lower() not in always:
                    out.append(rel)
        else:
            out.append(entry)
    return list(dict.fromkeys(out))


def _pkg_filename(name, version):
    """packages/ 下的目标文件名。

    name/version 都来自**包内** package.json(不可信输入): 不消毒的话 "@x/../../etc"
    能把文件写到 <dshHome>/packages 之外。
    """
    leaf = re.sub(r"[^A-Za-z0-9._@~+-]", "-", str(name or "plugin")).replace("/", "-")
    ver = re.sub(r"[^A-Za-z0-9._+-]", "-", str(version or "")) or "0.0.0"
    # 点号串也压掉: 分隔符换成 "-" 后 ".." 仍会留在文件名里, 看着就像穿越路径,
    # 而且某些工具会按 "." 分段处理它
    leaf = re.sub(r"\.{2,}", ".", leaf).lstrip("-.") or "plugin"
    ver = re.sub(r"\.{2,}", ".", ver).lstrip("-.") or "0.0.0"
    return "%s-%s.tgz" % (leaf, ver)


def _pnpm_error_tail(out, limit=600):
    """pnpm 失败信息里最有用的尾巴: ERR_PNPM_* 行优先(末尾常被进度条/警告占满)。"""
    text = str(out or "").strip()
    if not text:
        return "无输出"
    hits = [ln.strip() for ln in text.splitlines()
            if "ERR_PNPM" in ln or "ERR!" in ln or ln.lower().startswith("npm err")]
    return ("\n".join(hits) if any(hits) else text)[-limit:]


def _copy_entry(src, dst, use_ignore=True, root=None):
    """复制一个条目, 返回是否真复制了。

    root 是打包的源目录(插件根): 条目本身或其内部指向 root 之外的符号链接不进包。

    use_ignore=False 用于 package.json 里**显式声明**的条目: `_PACK_SOFT`(scripts/
    docs/tests/…)是甩开发期目录的, 但插件完全可能把运行期资产放在名为 scripts/ 的
    目录里 —— 作者自己写进 files 的就必须原样带上, 否则打出的是缺资产的包。
    注意**不是"什么都不甩"**: `_PACK_ALWAYS`(node_modules/.git/.pnpm/…)对声明条目
    同样生效, 否则源码树里就地跑过 pnpm 的插件会把整棵 node_modules 打进 tgz
    (体积爆炸 + 装进 profile 后与 pnpm 的依赖树双份冲突)。
    """
    if not os.path.exists(src):
        return False
    if root and os.path.islink(src) and _escapes(root, src):
        # files/main/patch 指到包外的链接: 不复制, 但要让用户知道包里少了东西
        _PACK_LINK_SKIP.append(os.path.relpath(src, root))
        return False
    if os.path.isdir(src):
        shutil.copytree(src, dst, dirs_exist_ok=True,
                        ignore=_ignore_fn(_PACK_IGNORE if use_ignore else _PACK_ALWAYS, root))
    else:
        os.makedirs(os.path.dirname(dst) or ".", exist_ok=True)
        shutil.copy2(src, dst)
    return True


def _pack_staged(src_dir, out_dir, job_log=None):
    """`pnpm pack` 因缺 devDependencies(prepack 里的 tsc 不存在)失败时的退路:
    只复制运行所需文件到暂存目录, 去掉 prepack/prepare/postinstall 再 pack,
    保留 dist 等已构建产物, 得到可安装的稳定 tgz(C# TryPackStagedPackage 同语义)。
    """
    stage = tempfile.mkdtemp(prefix="llamadsh_packstage_")
    _links_skipped()          # 清掉上一轮可能留下的记录, 免得串到本次日志里
    try:
        missing = _missing_build_entry(src_dir)
        if missing:
            if job_log:
                job_log("[插件安装] 插件目录缺少构建产物 %s, 无法在没有 devDependencies 的机器上打包"
                        "; 请先在源目录执行 pnpm install && pnpm build" % missing)
            return None
        src_pj = os.path.join(src_dir, "package.json")
        doc = read_json(src_pj, None)
        if not doc:
            return None
        declared = isinstance(doc.get("files"), list)
        absent = []
        for entry in _pack_entries(src_dir, doc.get("files")):
            if not _copy_entry(os.path.join(src_dir, entry), os.path.join(stage, entry),
                               use_ignore=not declared, root=src_dir):
                absent.append(entry)
        if absent and job_log:
            job_log("[插件安装] 注意: files 声明的条目在源目录里不存在: %s"
                    % ", ".join(absent[:6]))
        # package.json 与 bundle patch 必须在, 即使 files 没列
        _copy_entry(src_pj, os.path.join(stage, "package.json"))
        # main 入口被 files 排除时, pnpm 会照常打出一个缺入口的包(装上去就 ENOENT),
        # 所以这里补进来 —— 缺入口的包比多几个文件危险得多
        main_rel = _rel_path(doc.get("main"))
        if main_rel and not os.path.exists(os.path.join(stage, main_rel)):
            if _copy_entry(os.path.join(src_dir, main_rel), os.path.join(stage, main_rel),
                           use_ignore=False, root=src_dir) and job_log:
                job_log("[插件安装] 注意: main 入口 %s 不在 files 声明里, 已一并打入包内"
                        % main_rel)
        bundle = (doc.get("dsh") or {}).get("bundle")
        patch = _rel_path(bundle.get("patch")) if isinstance(bundle, dict) else ""
        if patch:
            _copy_entry(os.path.join(src_dir, patch), os.path.join(stage, patch),
                        use_ignore=False, root=src_dir)
        # 包外符号链接: 少了东西必须说出来, 否则用户只会看到"装上去缺资产"
        skipped = _links_skipped()
        if skipped and job_log:
            job_log("[插件安装] 注意: %d 个符号链接指向插件目录之外, 未打入包内(否则包会把"
                    "别人的文件一起带走): %s" % (len(skipped), ", ".join(skipped[:6])))
        scripts = doc.get("scripts")
        if isinstance(scripts, dict):
            for name in ("prepack", "prepare", "postinstall"):
                scripts.pop(name, None)
            if scripts:
                doc["scripts"] = scripts
            else:
                doc.pop("scripts", None)
        with open(os.path.join(stage, "package.json"), "w", encoding="utf-8") as fh:
            json.dump(doc, fh, indent=2, ensure_ascii=False)
        started = time.time()   # out_dir 里可能有上一轮 pnpm pack 失败留下的 tgz
        r = subprocess.run(["pnpm", "pack", "--pack-destination", out_dir], cwd=stage,
                           capture_output=True, text=True,
                           encoding="utf-8", errors="replace", timeout=600, env=_pnpm_env())
        if r.returncode != 0:
            if job_log:
                job_log("[插件安装] 暂存打包仍失败: %s"
                        % _pnpm_error_tail((r.stderr or "") + (r.stdout or ""), 300))
            return None
        return _newest_tgz(out_dir, not_before=started)
    finally:
        shutil.rmtree(stage, ignore_errors=True)


_RANGE_RE = re.compile(r"^[A-Za-z0-9.*^~|>=<, ]*$")


def _spec_ok(s):
    """spec 形态: file:/link: 本地包, 或 name[@range]。返回 (ok, 原因)。

    三条硬线: 不能以 '-' 开头(pnpm 会当命令行参数)、不能带换行/NUL(参数注入)、
    非 file:/link: 时包名必须过 _PKG_RE(registry 名), 版本范围只允许 semver 元字符。
    """
    if not s or s.startswith("-") or "\n" in s or "\0" in s:
        return False, "非法安装参数: %r" % (s[:100],)
    if s.startswith(("file:", "link:")):
        return True, ""
    head, sep, tail = s.rpartition("@")
    if sep and _safe_pkg(head):
        # name@file:<本地包> / name@link:<目录> 也是合法 spec(回填旧依赖时就会用到)
        if tail.startswith(("file:", "link:")):
            return True, ""
        if not _RANGE_RE.match(tail):
            return False, "非法版本范围: %r" % (s,)
        return True, ""
    if _safe_pkg(s):
        return True, ""
    return False, "非法包名/版本范围: %r" % (s,)


def _pnpm_add_spec(cwd, spec):
    """pnpm add <完整 spec>(file:/link: 本地包, 或 name[@range])。

    注意: 本地 tarball **不能**走 _pnpm_add(name=路径) —— 那里的包名白名单
    必然拒绝带 / 的路径, 用户侧就表现为"非法包名"(装 dsh-infinite-context-*.tgz 即此)。
    这里改校 spec 本身(见 _spec_ok)。
    """
    if not procman_which_pnpm():
        return False, "pnpm 不在 PATH(可先在 Node 环境面板一键安装)"
    s = str(spec or "")
    ok, why = _spec_ok(s)
    if not ok:
        return False, why
    r = subprocess.run(["pnpm", "add", s] + _NET_FLAGS, cwd=cwd, capture_output=True,
                       text=True, encoding="utf-8", errors="replace",
                       timeout=900, env=_pnpm_env())
    return r.returncode == 0, (r.stdout or "") + (r.stderr or "")


_PROFILE_LOCKS = {}
_PROFILE_LOCKS_GUARD = threading.Lock()
# 正在进行的 profile 级写操作: [描述, 重入深度]。它们**同步跑在 HTTP 线程里**, 不在
# main 的 JOBS 里 —— 而"把 package.json 写到一半服务就退出了"恰是这一族最坏的收尾事故,
# 所以退出前必须点名(main._stop_all_and_exit 调 inflight_ops())。
_INFLIGHT = {}
# end_profile_op 放锁时发现"本来不是自己持的" —— 记录下来(end_profile_op/op_imbalance)
_OP_IMBALANCE = []


def _profile_lock(profile):
    """同一 profile 的依赖写操作必须串行。

    main.py 是 ThreadingHTTPServer(每请求一线程): 装/卸/升级/激活都会读改写同一份
    package.json 并对同一个 pnpm project 跑 pnpm, 各自的备份与回滚会互相覆盖(等于丢掉
    对方刚写好的依赖)。用 **RLock** 是因为 install 内部还会调 set_bundle_active —— 同
    一线程重入必须放行。C# 不需要锁: 那边靠模态窗口 + 窗口级 _busy + manifest 写回全在
    UI 线程, 结构上就是串行的。
    """
    with _PROFILE_LOCKS_GUARD:
        return _PROFILE_LOCKS.setdefault(str(profile or ""), threading.RLock())


def begin_profile_op(profile, what):
    """非阻塞拿锁 + 登记在途; 拿不到说明别的插件操作正在跑, 调用方直接回忙。

    宁可明确拒绝也不排队: 一条 HTTP 请求挂 900s 等锁, 用户只会以为界面卡死。
    """
    key = str(profile or "")
    if not _profile_lock(key).acquire(blocking=False):
        return False
    with _PROFILE_LOCKS_GUARD:
        rec = _INFLIGHT.setdefault(key, ["", 0])
        rec[0] = what
        rec[1] += 1
    return True


def end_profile_op(profile):
    """与 begin_profile_op 成对; 重入时只减深度, 回到 0 才真正放锁。

    放锁与记账必须分开: 以前"登记已经没了"就直接 return, 把下面的 release 一起跳过 ——
    一旦 _INFLIGHT 与真实持锁状态脱节, 该 profile 的锁就**永久**泄漏, 之后所有插件操作
    都回"已有插件操作正在进行中", 用户只能重启服务自救。现在无条件尝试放锁: 真的持有过
    就放掉, 本来不是自己持的(begin 失败后 finally 也调 end)则 RLock 自己抛 RuntimeError,
    既不破坏别人的互斥, 也把失衡登记下来供自检与排障。
    """
    key = str(profile or "")
    with _PROFILE_LOCKS_GUARD:
        rec = _INFLIGHT.get(key)
        if rec is not None:
            rec[1] -= 1
            if rec[1] <= 0:
                _INFLIGHT.pop(key, None)
    try:
        _profile_lock(key).release()
    except RuntimeError:
        _OP_IMBALANCE.append(key)


def op_imbalance():
    """begin/end 失衡的次数与 profile(begin 失败后 finally 仍调 end 是正常来源)。"""
    with _PROFILE_LOCKS_GUARD:
        return list(_OP_IMBALANCE)


def inflight_ops():
    """当前在跑的、会改 profile 依赖的动作(退出收尾用)。"""
    with _PROFILE_LOCKS_GUARD:
        return ["%s(profile %s)" % (rec[0], key or "?") for key, rec in _INFLIGHT.items()]


_BUSY_MSG = "已有插件操作正在进行中, 请等它结束后再试"


def install_local_plugin(profile, source, job_log=None, dsh_dir=""):
    """本地安装插件: 目录或 .tgz 都可以(逐条对齐 C# InstallLocalPlugin)。

    几条都是"另一台电脑上"实测出来的坑:
    - tarball 先复制进 <dshHome>/packages 再装: profile 里的 file: 声明必须指向稳定位置,
      否则源文件在 U 盘/临时目录里被移动或拔出, DSH 启动就 ENOENT。
    - 同名旧依赖的 spec 与新 spec 不一致时先 pnpm remove, 否则 pnpm 会去解析旧 tarball。
    - pnpm add 失败要回滚 package.json 与 pnpm-lock.yaml, 不留半安装状态。
    - 目录形态先 pnpm pack 到临时目录(失败则用去掉构建脚本的暂存副本重试),
      仍失败才退化直接 file:<目录>; 打包临时目录每次独立, 不共用固定名。
    - 装成功后有 dsh.bundle 才自动激活, 且要先过激活守卫(见 filter_bundle_activation)。
    """
    source = (source or "").strip().strip('"')
    if not source:
        return False, "未选择插件目录或 .tgz 文件"
    if not procman_which_pnpm():
        return False, "pnpm 不在 PATH(可先在 Node 环境面板一键安装)"
    src = os.path.abspath(os.path.expanduser(source))
    is_dir = os.path.isdir(src)
    is_tgz = os.path.isfile(src) and src.endswith((".tgz", ".tar.gz"))
    if not is_dir and not is_tgz:
        return False, "本地插件不存在或不是目录/.tgz: %s" % src
    base = profile_dir(profile)
    if not os.path.isfile(os.path.join(base, "package.json")):
        return False, "profile 目录缺少 package.json: %s" % base
    packages_dir = os.path.join(dsh_home(), "packages")
    pack_temp = None
    spec, name, version = "", "", ""
    installed, got = "", ""            # 安装成功后的包名/版本(异常文案要用, 必须先绑定)
    add_done = False                   # pnpm add 成功后不得再回滚
    manifest = os.path.join(base, "package.json")
    lockfile = os.path.join(base, "pnpm-lock.yaml")
    manifest_bak = lock_bak = None
    lock_existed = os.path.isfile(lockfile)
    removed_spec = ""

    def _rollback():
        """把 manifest/lock 恢复原状(失败与异常两条路都要走, 不留半安装状态)。

        返回**是否真的写回成功**: 文案以前只看"备份存不存在", 于是 copy2 失败(只读挂载、
        磁盘满、权限)时也报"已回滚 package.json/lock" —— 用户以为 profile 完好, 实际停在
        半安装(声明在、依赖没装), 下次 DSH 启动才表现为 ENOENT/模块加载失败。
        """
        restored = True
        for path, bak in ((manifest, manifest_bak), (lockfile, lock_bak)):
            if bak:
                try:
                    shutil.copy2(bak, path)
                except OSError:
                    restored = False
        return restored

    def _rollback_note(ok_restored):
        if not manifest_bak and not lock_bak:
            return "⚠ 备份失败未能回滚; "
        if ok_restored:
            return "已回滚 package.json/lock; "
        return "⚠ 备份在但写回失败, 未能回滚 package.json/lock(profile 可能停在半安装状态); "
        if not lock_existed and lock_bak is None and os.path.isfile(lockfile):
            try:  # 原本没有 lock 文件: 回滚后也不该留下 pnpm 新建的那一份
                os.remove(lockfile)
            except OSError:
                pass

    def _snapshot(tgz_src, leaf):
        """把安装包快照进 <dshHome>/packages/, 返回稳定路径(非法文件名回 None)。"""
        os.makedirs(packages_dir, exist_ok=True)
        dest = os.path.join(packages_dir, leaf)
        root = os.path.realpath(packages_dir)
        try:
            if os.path.commonpath([os.path.realpath(dest), root]) != root:
                return None
        except ValueError:
            return None
        # 两边都要 realpath: ~/.dsh 本身是符号链接时, 拿 realpath(src) 和逻辑 dest
        # 永远不相等 → 对同一文件 copy2 → SameFileError 被当成"安装异常"
        if os.path.realpath(tgz_src) != os.path.realpath(dest):
            shutil.copy2(tgz_src, dest)
        return dest

    if not begin_profile_op(profile, "本地插件安装"):
        # 两个安装并发跑到这里时, 各自的备份/回滚会用"自己进入时"的快照覆盖对方刚写好
        # 的依赖(等于丢掉对方装好的包), pnpm 也会同时改同一个 project。
        return False, _BUSY_MSG
    try:
        if is_dir:
            name, version = _package_identity(src)
            if not name:
                return False, "插件目录缺少可读的 package.json(name 为空): %s" % src
            # 临时目录每次独立: 固定名(<dshHome>/pack-temp)会被并发安装互删,
            # 且上次失败留下的旧 tgz 会被 _newest_tgz 认成本次产物(装进 profile 的
            # 就是上一个版本的包)。
            pack_temp = tempfile.mkdtemp(prefix="llamadsh_packtemp_")
            started = time.time()
            if job_log:
                job_log("[插件安装] 打包 %s@%s …" % (name, version or "?"))
            r = subprocess.run(["pnpm", "pack", "--pack-destination", pack_temp],
                               cwd=src, capture_output=True, text=True,
                               encoding="utf-8", errors="replace", timeout=600,
                               env=_pnpm_env())
            tgz = _newest_tgz(pack_temp, not_before=started)
            if r.returncode != 0 or not tgz:
                if job_log:
                    job_log("[插件安装] pnpm pack 失败, 尝试无构建脚本打包: %s"
                            % _pnpm_error_tail((r.stderr or "") + (r.stdout or ""), 300))
                tgz = _pack_staged(src, pack_temp, job_log)
            if not tgz:
                missing = _missing_build_entry(src)
                if missing:
                    return False, ("插件目录缺少构建产物 %s: 请先在源目录执行 "
                                   "pnpm install && pnpm build 再重新安装" % missing)
                if job_log:
                    job_log("[插件安装] 无构建脚本打包仍失败, 回退直接安装目录")
                spec = "file:" + src
            else:
                dest = _snapshot(tgz, _pkg_filename(name, version))
                if not dest:
                    return False, "非法安装包名: %r" % (name,)
                spec = "file:" + dest
                if job_log:
                    job_log("[插件安装] 已生成稳定安装包: %s" % dest)
        else:
            name, version = _tarball_identity(src)
            dest = _snapshot(src, os.path.basename(src))
            if not dest:
                return False, "非法安装包文件名: %r" % (os.path.basename(src),)
            spec = "file:" + dest
            if job_log:
                job_log("[插件安装] 使用本地包: %s" % dest)

        manifest_bak = _backup(manifest)
        lock_bak = _backup(lockfile) if lock_existed else None
        before = _profile_deps(base)
        if name and name in before and before[name] != spec:
            if job_log:
                job_log("[插件安装] 清理同名旧依赖 %s(原 %s)…" % (name, before[name]))
            removed_spec = str(before[name] or "")
            ok_rm, out_rm = _pnpm_remove(base, name)
            if not ok_rm and job_log:
                job_log("[插件安装] 清理旧依赖失败: %s" % _pnpm_error_tail(out_rm, 300))
            before = _profile_deps(base)

        if job_log:
            job_log("[插件安装] pnpm add %s …" % spec)
        ok, out = _pnpm_add_spec(base, spec)
        if not ok:
            rolled = _rollback_note(_rollback()).rstrip("; ")
            note = "(%s)" % rolled
            if removed_spec:
                # 旧依赖已被 pnpm remove 掉而新装又失败: manifest 回滚了但 node_modules
                # 里还是缺包, 尽力把旧 spec 装回去, 装不回去就把话说清楚
                spec_ok, _ = _spec_ok(name + "@" + removed_spec)
                back_ok = spec_ok and _pnpm_add(base, name, "@" + removed_spec)[0]
                note = ("%s, 并已把原依赖 %s%s 装回" % (rolled, name, removed_spec)
                        if back_ok else
                        ("%s, 但同名旧依赖此前已被移除且未能装回: "
                         "如插件不可用请在插件列表点\"修复\"") % rolled)
            return False, "本地插件安装失败: %s %s" % (_pnpm_error_tail(out), note)
        add_done = True                   # 从这里起 pnpm 已经装成功, 后面再错也不能回滚

        after = _profile_deps(base)
        installed = name
        if not installed:
            for k, v in after.items():
                if k not in before or before[k] != v:
                    installed = k
                    break
        if not installed:
            return True, ("安装命令成功, 但未识别出新增/变化的包名(可能同一版本已安装),"
                          "请在插件列表点刷新确认")
        pj = os.path.join(base, "node_modules", installed.replace("/", os.sep), "package.json")
        doc = read_json(pj, None) or {}
        got = str(doc.get("version") or "")
        if not (doc.get("dsh") or {}).get("bundle"):
            bundle_msg = "已作为普通依赖安装, 但包内没有 dsh.bundle, 未加入 profile bundles"
        else:
            # C# 的 FilterBundleActivation: peer 错位/缺依赖/有错误/被用户锁定的包
            # 不能自动激活 —— 激活后 DSH 启动时模块加载直接失败
            plugins = []
            try:
                plugins = audit_plugins(profile, dsh_dir)
            except Exception as ex:
                if job_log:
                    job_log("[插件安装] 激活前审计失败, 跳过激活守卫: %r" % (ex,))
            kept, demoted = filter_bundle_activation([installed], plugins, profile)
            if any(k == installed for k in kept):
                act_ok, bundle_msg = set_bundle_active(profile, installed, True)
                if not act_ok:
                    bundle_msg = "已安装, 但写入 profile bundles 失败: %s" % bundle_msg
            else:
                why = (demoted[0].get("reason") if demoted else "审计未通过") or "审计未通过"
                bundle_msg = "已安装但**未自动激活**(%s): 请在插件列表确认后手动激活" % why
        if job_log:
            job_log("[插件安装] %s 完成: %s" % (installed, bundle_msg))
        return True, ("已安装 %s%s(%s); %s。请重启 DSH 生效。"
                      % (installed, "@" + got if got else "", spec, bundle_msg))
    except Exception as ex:
        # pnpm 超时/OSError/package.json 坏 JSON 等: 不回滚就把 profile 停在半安装状态,
        # 而且裸 traceback 会冒到 API 层变成"未知错误"
        tail = "超时" if isinstance(ex, subprocess.TimeoutExpired) else repr(ex)
        if add_done:
            # pnpm **已经装成功**之后的失败(典型: 激活写 manifest 时备份失败抛 IOError):
            # 这时候回滚方向是反的 —— 会把成功的安装删掉并谎报失败。只报告, 不回滚。
            return False, ("插件 %s%s 已安装成功, 但后续处理失败(%s): 依赖声明已生效,"
                           " 请在插件列表里手动激活/刷新确认"
                           % (installed or name or "(包名未识别)",
                              ("@" + got) if got else (("@" + version) if version else ""),
                              tail))
        return False, ("本地插件安装异常(%s), %s请重试并查看日志"
                       % (tail, _rollback_note(_rollback())))
    finally:
        if pack_temp:
            shutil.rmtree(pack_temp, ignore_errors=True)
        end_profile_op(profile)


def exempt_plugin(profile, name, version):
    """写版本豁免(compatibility.json); 也走 profile 锁, 免得和 pnpm 写回交错。"""
    if not begin_profile_op(profile, "写插件豁免 %s" % name):
        return False, _BUSY_MSG
    try:
        return _exempt_plugin_locked(profile, name, version)
    finally:
        end_profile_op(profile)


def _exempt_plugin_locked(profile, name, version):
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
