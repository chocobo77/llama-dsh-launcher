# -*- coding: utf-8 -*-
"""ParamSchemaService 的 Python 复刻。

三层层叠:内置 llama_params.yml(随程序) → 程序旁 llama_params.yml(优先) →
~/.llamadsh/params_override.yml(差异覆盖)。
YAML 键为 camelCase(flag/label/role/type/default/group/enabled/...)。
"""
import os
import shutil

import yaml

import applog

# 程序目录(main.py 所在目录)
APP_DIR = os.path.dirname(os.path.abspath(__file__))
SCHEMA_FILE = "llama_params.yml"
# 内置纯净副本(随启动器发布,等价于原版的嵌入式资源);程序旁副本可被用户编辑
BUILTIN_PATH = os.path.join(APP_DIR, "webui", "llama_params_default.yml")
EXE_PATH = os.path.join(APP_DIR, SCHEMA_FILE)

OVERRIDE_DIR = os.path.join(os.path.expanduser("~"), ".llamadsh")
OVERRIDE_PATH = os.path.join(OVERRIDE_DIR, "params_override.yml")


def _read_builtin():
    # 注意: 读内置纯净副本(BUILTIN_PATH), 而非程序旁副本——
    # 否则内置层永远等于程序旁层, schema 自动升级永远不会触发。
    with open(BUILTIN_PATH, "r", encoding="utf-8") as f:
        return f.read()


def _parse_doc(yml):
    """解析基础层文档;失败返回 (None, error)。"""
    try:
        doc = yaml.safe_load(yml)
        if not isinstance(doc, dict) or not doc.get("params"):
            return None, "未解析到任何参数(params 为空)"
        return doc, ""
    except Exception as ex:
        return None, str(ex)


def _parse_override(yml):
    try:
        ov = yaml.safe_load(yml)
        return (ov if isinstance(ov, dict) else {}), ""
    except Exception as ex:
        return None, str(ex)


def _apply_override(doc, ov, result):
    groups = doc.setdefault("groups", []) or []
    # 组合并
    for g in ov.get("groups") or []:
        if not isinstance(g, dict) or not g.get("id"):
            continue
        base = next((x for x in groups if x.get("id") == g["id"]), None)
        if base is None:
            ng = {"id": g["id"], "title": g.get("title") or g["id"], "order": g.get("order") or 0}
            groups.append(ng)
        else:
            if g.get("title"):
                base["title"] = g["title"]
            if g.get("order"):
                base["order"] = g["order"]
    # 删除参数
    for flag in ov.get("remove") or []:
        params = doc["params"]
        victim = next((p for p in params if p.get("flag") == flag or flag in (p.get("aliases") or [])), None)
        if victim is not None:
            params.remove(victim)
        else:
            result["warnings"].append("override remove: 未找到参数 --" + flag)
    # 参数补丁
    for op in ov.get("params") or []:
        if not isinstance(op, dict) or not op.get("flag"):
            result["warnings"].append("override 参数缺少 flag,已跳过")
            continue
        params = doc["params"]
        target = next((p for p in params if p.get("flag") == op["flag"] or op["flag"] in (p.get("aliases") or [])), None)
        if target is None:
            target = {
                "flag": op["flag"], "label": op.get("label") or op["flag"],
                "type": op.get("type") or "String", "default": op.get("default") or "",
                "group": op.get("group") or "custom", "enabled": op.get("enabled") or False,
                "advanced": op.get("advanced") or False, "tooltip": op.get("tooltip") or "",
                "hidden": op.get("hidden") or False, "deprecated": op.get("deprecated") or False,
            }
            for k in ("role", "options", "requires", "conflicts", "implies", "suggests", "validate", "aliases"):
                if op.get(k) is not None:
                    target[k] = op[k]
            params.append(target)
            continue
        for k in ("label", "role", "type", "default", "group", "options", "requires",
                  "conflicts", "implies", "suggests", "validate", "aliases"):
            if op.get(k) is not None:
                target[k] = op[k]
        for k in ("enabled", "advanced", "hidden", "deprecated"):
            if op.get(k) is not None:
                target[k] = op[k]


def _finalize(doc, result):
    cpu = os.cpu_count() or 1
    for p in doc["params"]:
        dv = p.get("default")
        if isinstance(dv, str) and "$cpu" in dv:
            p["default"] = dv.replace("$cpu", str(cpu))
    by_flag = {}
    for p in doc["params"]:
        if p["flag"] in by_flag:
            result["errors"].append("参数 flag 重复: --%s(保留第一个)" % p["flag"])
            continue
        by_flag[p["flag"]] = p
    groups = {g["id"]: g for g in doc["groups"]}
    for p in doc["params"]:
        if not p.get("group"):
            p["group"] = "custom"
        if p["group"] not in groups:
            ng = {"id": p["group"], "title": p["group"], "order": 900}
            groups[p["group"]] = ng
            doc["groups"].append(ng)
            result["warnings"].append("参数 --%s 引用了未定义的组 '%s',已自动创建" % (p["flag"], p["group"]))
    for p in doc["params"]:
        req = p.get("requires")
        if req:
            keep = [r for r in req if r.get("flag") in by_flag]
            for r in req:
                if r.get("flag") not in by_flag:
                    result["errors"].append("--%s 的 requires 引用了不存在的参数 --%s,该规则已忽略" % (p["flag"], r.get("flag")))
            p["requires"] = keep
        conf = p.get("conflicts")
        if conf:
            keep = [c for c in conf if c in by_flag]
            for c in conf:
                if c not in by_flag:
                    result["errors"].append("--%s 的 conflicts 引用了不存在的参数 --%s,该规则已忽略" % (p["flag"], c))
            p["conflicts"] = keep
        imp = p.get("implies")
        if imp:
            keep = [c for c in imp if c in by_flag]
            for c in imp:
                if c not in by_flag:
                    result["errors"].append("--%s 的 implies 引用了不存在的参数 --%s,该规则已忽略" % (p["flag"], c))
            p["implies"] = keep
        val = p.get("validate")
        if isinstance(val, dict) and val.get("leq") and val["leq"] not in by_flag:
            result["errors"].append("--%s 的 validate.leq 引用了不存在的参数 --%s,该规则已忽略" % (p["flag"], val["leq"]))
            val["leq"] = None
    doc["groups"].sort(key=lambda g: g.get("order", 0))


def load():
    """返回 LoadResult dict: defs/groups/errors/warnings/source/fallback。"""
    result = {"defs": [], "groups": [], "errors": [], "warnings": [], "source": "", "fallback": False}
    try:
        embedded = _read_builtin()
    except Exception as ex:
        result["errors"].append("内置默认配置读取失败(严重): " + str(ex))
        result["source"] = "内置(空)"
        result["groups"] = [{"id": "basics", "title": "基础设置", "order": 0}]
        return result
    emb_doc, emb_err = _parse_doc(embedded)
    if emb_doc is None:
        result["errors"].append("内置默认配置解析失败(严重): " + emb_err)
        result["source"] = "内置(空)"
        result["groups"] = [{"id": "basics", "title": "基础设置", "order": 0}]
        return result

    doc = None
    try:
        if not os.path.exists(EXE_PATH):
            # 缺失则从内置副本提取
            with open(EXE_PATH, "w", encoding="utf-8") as f:
                f.write(embedded)
            doc = emb_doc
            result["source"] = "程序旁"
        else:
            exe_yml = open(EXE_PATH, encoding="utf-8").read()
            doc, exe_err = _parse_doc(exe_yml)
            if doc is None:
                result["fallback"] = True
                result["errors"].append("程序旁 llama_params.yml 解析失败: %s\n→ 本次已回退内置默认配置,请修复或删除该文件。" % exe_err)
            elif emb_doc.get("meta", {}).get("schema", 0) > doc.get("meta", {}).get("schema", 0):
                try:
                    shutil.copyfile(EXE_PATH, EXE_PATH + ".bak")
                    with open(EXE_PATH, "w", encoding="utf-8") as f:
                        f.write(embedded)
                    result["warnings"].append("参数配置已随启动器升级: v%s → v%s(旧文件备份为 llama_params.yml.bak,可从中找回自定义改动)" % (
                        doc.get("meta", {}).get("schema", "?"), emb_doc.get("meta", {}).get("schema", "?")))
                except Exception as up_ex:
                    result["warnings"].append("参数配置自动升级失败(%s),继续使用本地 v%s" % (up_ex, doc.get("meta", {}).get("schema", "?")))
                doc = emb_doc
            if doc is not None:
                result["source"] = "程序旁"
    except Exception as ex:
        result["warnings"].append("读取程序旁配置失败(%s),使用内置默认。" % ex)

    if doc is None:
        doc = emb_doc
        result["source"] = "内置"

    # 第 2 层:用户 override
    try:
        if os.path.exists(OVERRIDE_PATH):
            ov, ov_err = _parse_override(open(OVERRIDE_PATH, encoding="utf-8").read())
            if ov is None:
                result["errors"].append("override 配置解析失败,已忽略该层: %s\n%s" % (OVERRIDE_PATH, ov_err))
            else:
                _apply_override(doc, ov, result)
    except Exception as ex:
        result["errors"].append("读取 override 失败,已忽略该层: " + str(ex))

    _finalize(doc, result)
    result["groups"] = doc["groups"]
    result["defs"] = doc["params"]
    try:
        applog.info("[参数模块] source=%s defs=%d groups=%d fallback=%s errors=%s warnings=%s" % (
            result["source"], len(result["defs"]), len(result["groups"]), result["fallback"],
            result["errors"], result["warnings"]))
    except Exception:
        pass
    return result


def migrate_profile_flags(profile, defs):
    """旧 profile 的 flag 键按 aliases 迁移(目标键不存在时)。profile 为 dict(PascalCase)。"""
    by_flag = {}
    for d in defs:
        by_flag.setdefault(d["flag"], d)
    for section in ("ValueParams", "BoolParams"):
        sec = profile.get(section) or {}
        for key in list(sec.keys()):
            if key in by_flag:
                continue
            target = next((d for d in defs if key in (d.get("aliases") or [])), None)
            if target is not None and target["flag"] not in sec:
                sec[target["flag"]] = sec[key]
                del sec[key]
