# -*- coding: utf-8 -*-
"""核心命令拼装 / 模型解析 / GGUF 解析 / 显存估算 / 参数依赖引擎。

逐行对应原版 MainWindow.xaml.cs 中的同名逻辑(Linux 版把 llama-server.exe
替换为 llama-server)。
"""
import os
import re
import glob
import struct

GIB = 1073741824.0

# ---------------------------------------------------------------- 参数状态 --

class ParamState:
    """一次命令构建的参数取值视图(等价原版的 _pbw/_pw 控件状态)。"""

    def __init__(self, profile, defs):
        self.defs = defs
        self.by_flag = {}
        for d in defs:
            self.by_flag.setdefault(d["flag"], d)
        bp = profile.get("BoolParams") or {}
        vp = profile.get("ValueParams") or {}
        self.bools = {k: bool(v) for k, v in bp.items()}
        self.values = {}
        for k, v in vp.items():
            if isinstance(v, dict):
                self.values[k] = (bool(v.get("Enabled")), str(v.get("Value") or "").strip())
            else:
                self.values[k] = (bool(v), str(v).strip())

    def is_on(self, d):
        """IsEffectivelyOn:bool=勾选;值参数=启用且值非空。"""
        if d["type"] == "Bool":
            return self.bools.get(d["flag"], False)
        en, v = self.values.get(d["flag"], (False, ""))
        return en and len(v) > 0

    def effective_value(self, d):
        """未生效时回退 offValue(如 spec-type 未勾选视为 none),否则默认值(用于 ==/!= 比较)。"""
        if d["type"] == "Bool":
            return "true" if self.bools.get(d["flag"], False) else "false"
        en, v = self.values.get(d["flag"], (False, ""))
        if en and len(v) > 0:
            return v
        if d.get("offValue"):
            return d["offValue"]
        return d.get("default") or ""

    def raw_value(self, flag):
        en, v = self.values.get(flag, (False, ""))
        return v if en else ""

    def eff_value(self, flag):
        """按 flag 取"**实际会生效的值**": 未勾选时回退 offValue(与 build_cmd 一致)。

        raw_value 只给用户填的值, 未勾选就是 ""。但 build_cmd 对带 offValue 的 Combo/Bool
        会**显式写出关闭值**(--flash-attn off、--spec-type none…)。用 raw_value 做判断的地方
        会把"关掉的开关"当成"没填": 实测取消勾选 Flash Attention 后, 命令行是
        --flash-attn off, 而显存预算仍按 cache-type-v 的量化计价 —— V 缓存实际回退 f16,
        预算少算约 1.8 倍, 提示也一起消失。
        """
        d = next((x for x in self.defs if x["flag"] == flag), None)
        return self.effective_value(d) if d else self.raw_value(flag)

    def role_def(self, role):
        for d in self.defs:
            if d.get("role") == role:
                return d
        return None

    def role_text(self, role):
        d = self.role_def(role)
        return self.raw_value(d["flag"]) if d else ""

    def role_bool(self, role):
        d = self.role_def(role)
        return bool(d) and self.bools.get(d["flag"], False)


def _try_float(s):
    try:
        return float(s)
    except (TypeError, ValueError):
        return None


def compare_values(v, op, target):
    a, b = _try_float(v), _try_float(target)
    if a is not None and b is not None:
        c = (a > b) - (a < b)
    else:
        c = (v > target) - (v < target)
    return {"==": c == 0, "!=": c != 0, ">": c > 0, "<": c < 0, ">=": c >= 0, "<=": c <= 0}.get(op, True)


def norm_tok(v):
    """K/M 后缀 → 实际数值(取整字符串)。"""
    t = (v or "").strip()
    m = re.match(r"^(\d+(?:\.\d+)?)\s*([KkMm]?)$", t)
    if not m:
        return t
    num = float(m.group(1))
    suf = m.group(2).lower()
    if suf == "k":
        num *= 1024.0
    elif suf == "m":
        num *= 1048576.0
    return str(int(round(num)))


def rule_met(st, r):
    d = st.by_flag.get(r.get("flag"))
    if d is None:
        return True
    op = r.get("op") or "enabled"
    if op == "enabled":
        return st.is_on(d)
    if op == "disabled":
        return not st.is_on(d)
    return compare_values(st.effective_value(d), op, r.get("value") or "")


def requires_met(st, d):
    """全部 requires 满足才返回 True(off 语义发射与生效段共用此守卫)。"""
    req = d.get("requires") or []
    return all(rule_met(st, r) for r in req)


def check_param(st, d, on):
    """单参数依赖/互斥/校验。返回 None=通过,否则剔除原因。"""
    req = d.get("requires") or []
    if req:
        unmet = [r for r in req if not rule_met(st, r)]
        if unmet:
            return "前置不满足(%s)" % " 且 ".join(describe_rule(r) for r in unmet)
    conf = d.get("conflicts") or []
    if conf:
        hit = next((c for c in conf if c in on), None)
        if hit:
            return "与 --%s 互斥" % hit
    val = d.get("validate")
    if isinstance(val, dict) and d["type"] != "Bool":
        v = st.effective_value(d)
        vn = norm_tok(v)
        n = _try_float(vn)
        if (val.get("min") is not None or val.get("max") is not None) and n is None:
            return "值 '%s' 不是数字" % v
        if val.get("min") is not None and n is not None and n < val["min"]:
            return "值 %s 小于下限 %s" % (vn, val["min"])
        if val.get("max") is not None and n is not None and n > val["max"]:
            return "值 %s 大于上限 %s" % (vn, val["max"])
        if val.get("leq"):
            other = st.by_flag.get(val["leq"])
            if other is not None and other["flag"] in on:
                cap = _try_float(norm_tok(st.effective_value(other)))
                if cap is not None and n is not None and n > cap:
                    return "值 %s 超过 --%s (%s)" % (vn, other["flag"], cap)
        if val.get("regex"):
            try:
                if not re.search(val["regex"], v):
                    return "值 '%s' 不匹配格式 %s" % (v, val["regex"])
            except re.error:
                pass
    return None


def describe_rule(r):
    op = r.get("op") or "enabled"
    flag = r.get("flag")
    if op == "enabled":
        return "--%s 已启用" % flag
    if op == "disabled":
        return "--%s 已停用" % flag
    return "--%s %s %s" % (flag, op, r.get("value"))


# ------------------------------------------------------------ 模型路径解析 --

def resolve_model_path(model_raw, log_fn=None):
    """返回 (path, error)。error 非 None 时 path 为 None。"""
    raw = (model_raw or "").strip().strip('"')
    if not raw:
        return None, None
    if os.path.isdir(raw):
        first_shards = _pick_shard_family(glob.glob(os.path.join(raw, "*-00001-of-*.gguf")))
        if first_shards:
            return first_shards, None
        ggu = glob.glob(os.path.join(raw, "*.gguf"))
        if len(ggu) == 1:
            return ggu[0], None
        if len(ggu) > 1:
            msg = "目录中存在多个 GGUF(%d 个),请直接选择主模型文件: %s" % (len(ggu), raw)
            if log_fn:
                log_fn("[提示] " + msg)
            return None, msg
        st = glob.glob(os.path.join(raw, "*.safetensors"))
        if st:
            msg = "目录中是 safetensors 分片(%d 个文件),llama.cpp 不能直接加载。请先转换为 GGUF:python convert_hf_to_gguf.py \"%s\" --outfile model.gguf" % (len(st), raw)
            if log_fn:
                log_fn("[提示] " + msg)
            return None, msg
        return None, "目录中未找到 GGUF 模型文件: %s" % raw
    if os.path.isfile(raw):
        if raw.lower().endswith(".safetensors"):
            msg = "llama.cpp 不能直接加载 .safetensors 文件。请先转换为 GGUF:python convert_hf_to_gguf.py \"%s\" --outfile model.gguf" % (os.path.dirname(raw) or ".")
            if log_fn:
                log_fn("[提示] " + msg)
            return None, msg
        return raw, None
    return raw, None  # 原版行为:不存在的路径原样透传


def _pick_shard_family(cands):
    """同目录多套 *-00001-of-*.gguf 时按"同前缀分片数最多"选家族(平手按文件名)。
    868253e:纯字典序会选错家族——例如 A-00001-of-00002 与 B-00001-of-00009 应选 B。"""
    if not cands:
        return None
    fams = {}  # lower(key) -> (原始前缀, paths)
    for c in cands:
        m = re.match(r"^(.*)-\d{5}-of-\d{5}\.gguf$", os.path.basename(c), re.I)
        if m:
            key = m.group(1)
        else:
            key = os.path.basename(c)
        fams.setdefault(key.lower(), (key, []))[1].append(c)
    # 家族总分片数 = 该前缀下完整 glob 的数量(含全部分片,不只第一片)
    def fam_size(orig_key, sample):
        d = os.path.dirname(sample)
        return len(glob.glob(os.path.join(d, orig_key + "-*-of-*.gguf")))
    best = None
    for low, (orig_key, paths) in fams.items():
        n = fam_size(orig_key, paths[0])
        cand = (n, min(os.path.basename(p) for p in paths))
        if best is None or cand[0] > best[0][0] or (cand[0] == best[0][0] and cand[1] < best[0][1]):
            best = (cand, paths)
    return sorted(best[1])[0]


def detect_safetensors_dir(model_raw):
    raw = (model_raw or "").strip().strip('"')
    if not raw:
        return None
    if os.path.isdir(raw):
        return raw if glob.glob(os.path.join(raw, "*.safetensors")) else None
    if os.path.isfile(raw) and raw.lower().endswith(".safetensors"):
        return os.path.dirname(raw)
    return None


# ---------------------------------------------------------------- GGUF 解析 --

def _read_gguf_string(f):
    (ln,) = struct.unpack("<Q", f.read(8))
    if ln > 4194304:
        raise ValueError("bad gguf string length")
    return f.read(ln).decode("utf-8", "replace")


def _skip_gguf_value(f, vt):
    sizes = {0: 1, 1: 1, 7: 1, 2: 2, 3: 2, 4: 4, 5: 4, 6: 4, 10: 8, 11: 8, 12: 8}
    if vt in sizes:
        f.seek(sizes[vt], 1)
    elif vt == 8:
        _read_gguf_string(f)
    elif vt == 9:
        (et,) = struct.unpack("<I", f.read(4))
        (cnt,) = struct.unpack("<Q", f.read(8))
        for _ in range(cnt):
            _skip_gguf_value(f, et)
    else:
        raise ValueError("unknown gguf value type: %d" % vt)


def read_gguf_arch(model_path):
    """GGUF 头解析,返回 GgufArchInfo 等价 dict。

    键: ok/layers/kv_heads/head_dim/head_count/embed/context_len/architecture/
    sliding_window/full_attention_interval/nextn_predict_layers/expert_count/
    ssm_inner_size/ssm_state_size/ssm_conv_kernel/vocab_size
    (键表对齐 CS ReadGgufArch 当前版,含混合注意力/线性注意力/MTP 头)
    """
    r = {"ok": False, "layers": 0, "kv_heads": 0, "head_dim": 0, "head_count": 0,
         "embed": 0, "context_len": 0, "architecture": "",
         "sliding_window": 0, "full_attention_interval": 0, "nextn_predict_layers": 0,
         "expert_count": 0, "ssm_inner_size": 0, "ssm_state_size": 0, "ssm_conv_kernel": 0,
         "vocab_size": 0}
    try:
        with open(model_path, "rb") as f:
            if f.read(4) != b"GGUF":
                return r
            f.read(4)      # version
            f.read(8)      # tensor_count(u64)
            (kv_count,) = struct.unpack("<Q", f.read(8))
            for _ in range(kv_count):
                key = _read_gguf_string(f)
                (vt,) = struct.unpack("<I", f.read(4))
                if vt in (4, 5):
                    (v,) = struct.unpack("<I", f.read(4))
                    if key.endswith(".block_count"):
                        r["layers"] = v
                    elif key.endswith(".attention.head_count_kv"):
                        r["kv_heads"] = v
                    elif key.endswith(".attention.head_count"):
                        r["head_count"] = v
                    elif key.endswith(".attention.key_length"):
                        r["head_dim"] = v
                    elif key.endswith(".attention.value_length") and r["head_dim"] == 0:
                        r["head_dim"] = v
                    elif key.endswith(".embedding_length"):
                        r["embed"] = v
                    elif key.endswith(".context_length"):
                        r["context_len"] = v
                    elif key.endswith(".attention.sliding_window"):
                        r["sliding_window"] = v
                    elif key.endswith(".full_attention_interval"):
                        r["full_attention_interval"] = v
                    elif key.endswith(".nextn_predict_layers"):
                        r["nextn_predict_layers"] = v
                    elif key.endswith(".expert_count"):
                        r["expert_count"] = v
                    elif key.endswith(".ssm.inner_size"):
                        r["ssm_inner_size"] = v
                    elif key.endswith(".ssm.state_size"):
                        r["ssm_state_size"] = v
                    elif key.endswith(".ssm.conv_kernel"):
                        r["ssm_conv_kernel"] = v
                elif vt in (10, 11):
                    (v,) = struct.unpack("<Q", f.read(8))
                    if key.endswith(".context_length"):
                        r["context_len"] = v
                elif vt == 8:
                    v = _read_gguf_string(f)
                    if key == "general.architecture":
                        r["architecture"] = v
                elif vt == 9:
                    # 数组: 只关心 tokenizer.ggml.tokens 的长度(词表大小)
                    (et,) = struct.unpack("<I", f.read(4))
                    (cnt,) = struct.unpack("<Q", f.read(8))
                    if key == "tokenizer.ggml.tokens":
                        r["vocab_size"] = cnt
                        _skip_gguf_array_fast(f, et, cnt)
                    else:
                        for _ in range(cnt):
                            _skip_gguf_value(f, et)
                else:
                    _skip_gguf_value(f, vt)
            # head_dim 回退链: key_length > value_length > round(embed/head_count) > 128
            if r["head_dim"] == 0 and r["head_count"] > 0 and r["embed"] > 0:
                r["head_dim"] = int(round(r["embed"] / r["head_count"]))
            if r["head_dim"] == 0:
                r["head_dim"] = 128
            if r["layers"] > 0 and r["kv_heads"] > 0 and r["head_dim"] > 0:
                r["ok"] = True
    except Exception:
        r["ok"] = False
    return r


def _skip_gguf_array_fast(f, et, cnt):
    """跳过数组;对定长元素类型走 seek 快路径(tokenizer.ggml.tokens 可达 15 万条)。"""
    fixed = {0: 1, 1: 1, 7: 1, 2: 2, 3: 2, 4: 4, 5: 4, 6: 4, 10: 8, 11: 8, 12: 8}
    if et in fixed:
        f.seek(fixed[et] * cnt, 1)
    elif et == 8:
        for _ in range(cnt):
            (ln,) = struct.unpack("<Q", f.read(8))
            f.seek(ln, 1)
    else:
        for _ in range(cnt):
            _skip_gguf_value(f, et)


def cache_bytes_per_elem(cache_type):
    # 每元素字节数按 ggml.h 各 block 结构定义换算:
    # Q4_0=20B/32, Q4_1=24B/32, Q5_0=28B/32, Q5_1=32B/32, Q8_0=34B/32,
    # IQ4_NL=20B/32, Q6_K=210B/256(0.8203), F16/BF16=2B, F32=4B
    t = (cache_type or "").strip().lower()
    return {"f32": 4.0, "f16": 2.0, "bf16": 2.0, "q8_0": 1.0625, "q4_0": 0.625,
            "iq4_nl": 0.625, "q4_1": 0.75, "q5_0": 0.875, "q5_1": 1.0,
            "q6_0": 0.8203125}.get(t, 2.0)


# ---------------------------------------------------------------- 命令拼装 --

def find_llama_server(llama_dir_raw):
    """定位 llama-server;返回存在的可执行路径或抛 FileNotFoundError 语义(None, msg)。"""
    text = (llama_dir_raw or "").strip().strip('"')
    if not text:
        return None, "未设置 llama.cpp 目录"
    if os.path.isdir(text):
        for name in ("llama-server", "llama-server.exe"):
            p = os.path.join(text, name)
            if os.path.isfile(p):
                return os.path.abspath(p), None
        return None, "llama-server not found: %s(目录下没有 llama-server)" % text
    if os.path.splitext(text)[1]:
        return text if os.path.isfile(text) else None, "llama-server not found: %s" % text
    for cand in (text, text + ".exe"):
        if os.path.isfile(cand):
            return cand, None
    return None, "llama-server not found: %s" % text


def is_spec_decoding_active(st, defs):
    """推测解码是否激活: spec-type 已勾选且有效值 != none。--model-draft 的发射以此为准。"""
    d = st.role_def("spec_type")
    if not d or not st.is_on(d):
        return False
    return (st.effective_value(d) or "").lower() != "none"


def build_cmd(profile, defs):
    """返回 (cmd_list, violations, flag_args)。

    flag_args: {flag: [命令行片段...]} — 每个旗标实际发射的片段,
    供逐参数真实启动参数回显(未发射参数由前端显示灰色预览)。
    """
    st = ParamState(profile, defs)
    if not (profile.get("LlamaDir") or "").strip().strip('"'):
        # CS 语义: 目录为空 → 空命令(前端显示 "Empty"), 不抛错
        return [], [], {}
    exe, err = find_llama_server(profile.get("LlamaDir"))
    if err:
        raise FileNotFoundError(err)
    lst = [exe]
    flag_args = {"model": ["--model"], "model-draft": ["--model-draft"]}

    model_path, m_err = resolve_model_path(profile.get("ModelPath"))
    if m_err:
        raise FileNotFoundError(m_err)
    if model_path:
        lst += ["--model", model_path]
        flag_args["model"] = ["--model", model_path]
    mtp = (profile.get("MtpPath") or "").strip().strip('"').strip()
    if mtp and is_spec_decoding_active(st, defs):
        lst += ["--model-draft", mtp]
        flag_args["model-draft"] = ["--model-draft", mtp]
    mmproj_file = (profile.get("MmprojPath") or "").strip().strip('"').strip()
    mmproj_url = (profile.get("MmprojUrl") or "").strip().strip('"').strip()
    if mmproj_file:
        lst += ["--mmproj", mmproj_file]
        flag_args["mmproj"] = ["--mmproj", mmproj_file]
    elif mmproj_url:
        lst += ["--mmproj-url", mmproj_url]
        flag_args["mmproj-url"] = ["--mmproj-url", mmproj_url]
    if profile.get("NoMmprojOffload") and (mmproj_file or mmproj_url):
        lst.append("--no-mmproj-offload")
        flag_args["__no_mmproj_offload"] = ["--no-mmproj-offload"]

    violations = []
    on = set()
    for d in defs:
        if not d.get("hidden") and d["flag"] not in ("model", "model-draft") and st.is_on(d):
            on.add(d["flag"])
    for d in defs:
        flag = d["flag"]
        if flag in ("model", "model-draft") or d.get("hidden"):
            continue
        if flag not in on:
            continue
        reject = check_param(st, d, on)
        if reject is not None:
            violations.append("--%s(%s)" % (flag, reject))
            continue
        if d["type"] == "Bool":
            lst.append("--" + flag)
            flag_args[flag] = ["--" + flag]
        else:
            v = st.effective_value(d)
            if d.get("role") == "ctx_size":
                v = norm_tok(v)
            if v:
                lst += ["--" + flag, v]
                flag_args[flag] = ["--" + flag, v]
    # "取消勾选=关闭"语义: 配置了 off 语义的参数,未勾选时显式发射关闭值/否定旗标,
    # 覆盖 llama-server 默认值不是"关闭"的选项(--no-jinja / --flash-attn off / --spec-type none 等)。
    # 前置未满足者跳过——否则"前置已取消、后置仍被注入"(如 spec-type=none 时仍发
    # --no-spec-draft-backend-sampling)。这是 ParamRules.Build 的头号回归点(v4 修正)。
    for d in defs:
        flag = d["flag"]
        if flag in ("model", "model-draft") or d.get("hidden") or flag in on:
            continue
        if not requires_met(st, d):
            continue
        if d["type"] == "Bool":
            if d.get("offFlag"):
                lst.append("--" + d["offFlag"])
                flag_args[flag] = ["--" + d["offFlag"]]
        elif d.get("offValue"):
            lst += ["--" + flag, d["offValue"]]
            flag_args[flag] = ["--" + flag, d["offValue"]]
    # 自定义参数原样追加到末尾(48a9a2f 语义)
    custom = profile.get("CustomArgs") or []
    if isinstance(custom, list):
        for a in custom:
            a = str(a).strip()
            if a:
                lst.append(a)
    return lst, violations, flag_args


def build_cmd_simple(profile, defs):
    """兼容旧调用:只返回 (cmd_list, violations)。"""
    lst, violations, _ = build_cmd(profile, defs)
    return lst, violations


def log_param_hints(st, defs, log_fn):
    on = set()
    for d in defs:
        if not d.get("hidden") and d["flag"] not in ("model", "model-draft") and st.is_on(d):
            on.add(d["flag"])
    for d in defs:
        if d["flag"] not in on:
            continue
        if d.get("deprecated"):
            log_fn("[参数] ⚠ --%s 已废弃,建议从参数面板取消勾选" % d["flag"])
        sugg = d.get("suggests") or []
        if sugg and not any(s in on for s in sugg):
            log_fn("[参数] 提示: --%s 通常配合 %s 使用" % (d["flag"], " / ".join("--" + s for s in sugg)))


# ------------------------------------------------------------------ 格式化 --

def qa(a):
    if " " in a or '"' in a:
        return '"' + a.replace('"', '\\"') + '"'
    return a


def fmt_cmd(cmd_list, defs=None):
    """命令预览格式化。868253e: 值以 -- 开头时纯启发式必然错位,
    改为按 schema 已知旗标判断取值类型,未知旗标回退启发式。"""
    if not cmd_list:
        return ""
    value_flags = set()
    if defs:
        for d in defs:
            if d.get("type") != "Bool":
                value_flags.add(d["flag"].lower())
            for a in (d.get("aliases") or []):
                value_flags.add(a.lower())
    out = [qa(cmd_list[0])]
    i = 1
    while i < len(cmd_list):
        t = cmd_list[i]
        takes = False
        if t.startswith("--") and i + 1 < len(cmd_list):
            name = t[2:].split("=", 1)[0].lower()
            if name in value_flags:
                takes = True
            elif not cmd_list[i + 1].startswith("--"):
                takes = True  # 未知旗标: 旧启发式兜底
        if takes:
            out.append("    %s %s" % (qa(t), qa(cmd_list[i + 1])))
            i += 2
        else:
            out.append("    " + qa(t))
            i += 1
    return "\n".join(out)




# ---------------------------------------------------------------- 显存估算 --

def ggml_bpe(t):
    """GGML 数据类型 → 每元素字节数(bytes per block / elems per block)。
    表对齐 CS GgmlBpe(36 项, 未知类型 1.0 保守)。"""
    table = {
        0: 4.0, 1: 2.0, 2: 20.0 / 32, 3: 24.0 / 32, 4: 24.0 / 32, 5: 24.0 / 32,
        6: 24.0 / 32, 7: 28.0 / 32, 8: 34.0 / 32, 9: 36.0 / 32,
        10: 84.0 / 256, 11: 110.0 / 256, 12: 144.0 / 256, 13: 176.0 / 256,
        14: 210.0 / 256, 15: 200.0 / 256,
        16: 66.0 / 256, 17: 74.0 / 256, 18: 98.0 / 256, 19: 50.0 / 256,
        20: 20.0 / 32, 21: 110.0 / 256, 22: 98.0 / 256, 23: 136.0 / 256,
        24: 1.0, 25: 2.0, 26: 4.0, 27: 8.0, 28: 8.0, 29: 56.0 / 256, 30: 2.0,
        31: 20.0 / 32, 32: 20.0 / 32, 33: 20.0 / 32, 34: 18.0 / 64, 35: 84.0 / 256,
        36: 17.0 / 32,
    }
    return table.get(t, 1.0)


def gguf_shard_paths(model_path):
    """官方分片加载约定下 --model 只给第一片;枚举全部同族分片。"""
    p = model_path
    m = re.search(r"-(\d{5})-of-(\d{5})\.gguf$", os.path.basename(p), re.I)
    if not m:
        return [p]
    stem = os.path.basename(p)[:m.start()]
    d = os.path.dirname(p) or "."
    total = int(m.group(2))
    out = []
    for i in range(1, total + 1):
        cand = os.path.join(d, "%s-%05d-of-%05d.gguf" % (stem, i, total))
        if os.path.isfile(cand):
            out.append(cand)
    return out or [p]


def gguf_shard_missing(model_path):
    """分片家族缺了几片(0 = 齐全, -1 = 不是分片模型)。

    gguf_shard_paths 只列出**存在**的分片, 所以缺片时它看起来完全正常: 权重按已有分片
    算(少算一整片 = 预算偏乐观), 张量表 shards_ok == shards_total 也照样"ok"。
    缺失数必须按文件名里声明的 -of-NNNNN 单独算出来。
    """
    m = re.search(r"-(\d{5})-of-(\d{5})\.gguf$", os.path.basename(model_path or ""), re.I)
    if not m:
        return -1
    stem = os.path.basename(model_path)[:m.start()]
    d = os.path.dirname(model_path) or "."
    total = int(m.group(2))
    have = 0
    for i in range(1, total + 1):
        if os.path.isfile(os.path.join(d, "%s-%05d-of-%05d.gguf" % (stem, i, total))):
            have += 1
    return total - have


def gguf_shard_bytes(model_path):
    return float(sum(os.path.getsize(s) for s in gguf_shard_paths(model_path) if os.path.isfile(s)))


def read_gguf_tensor_stats(model_path, layer_count):
    """解析 GGUF 张量表,按张量名的层号与是否专家精确拆分字节。

    分片模型的张量表在各分片头部各自列出,逐片累加;单分片失败继续其余,
    但"部分分片解析成功"绝不冒充完整(ok 仅全部分片成功才置位, 其余标 partial)。
    返回 dict: ok/partial/embed_output_bytes/dense_per_layer/expert_per_layer/expert_total_bytes/layer_count
    """
    r = {"ok": False, "partial": False, "embed_output_bytes": 0.0,
         "dense_per_layer": [0.0] * layer_count if layer_count > 0 else [],
         "expert_per_layer": [0.0] * layer_count if layer_count > 0 else [],
         "expert_total_bytes": 0.0, "layer_count": layer_count}
    shards_total = 0
    shards_ok = 0
    for shard in gguf_shard_paths(model_path):
        shards_total += 1
        parsed = False
        try:
            with open(shard, "rb") as f:
                if f.read(4) != b"GGUF":
                    continue
                f.read(4)  # version
                (tensor_count,) = struct.unpack("<Q", f.read(8))
                (kv_count,) = struct.unpack("<Q", f.read(8))
                for _ in range(kv_count):
                    _read_gguf_string(f)
                    (vt,) = struct.unpack("<I", f.read(4))
                    _skip_gguf_value(f, vt)
                # KV 之后直接是 tensor infos(alignment 只影响数据区起点,无填充)
                for _ in range(tensor_count):
                    name = _read_gguf_string(f)
                    (n_dims,) = struct.unpack("<I", f.read(4))
                    elems = 1
                    for _ in range(n_dims):
                        (d,) = struct.unpack("<Q", f.read(8))
                        elems *= d
                    (ttype,) = struct.unpack("<I", f.read(4))
                    f.read(8)  # offset
                    nbytes = elems * ggml_bpe(ttype)
                    expert = "ffn_" in name and ("exps" in name or "shexp" in name)
                    lm = re.search(r"(?:^|\.)blk\.(\d+)\.", name)
                    if lm:
                        l = int(lm.group(1))
                        if expert:
                            r["expert_total_bytes"] += nbytes
                            if l < r["layer_count"]:
                                r["expert_per_layer"][l] += nbytes
                        elif l < r["layer_count"]:
                            r["dense_per_layer"][l] += nbytes
                    else:
                        r["embed_output_bytes"] += nbytes
                parsed = True
        except Exception:
            continue
        if parsed:
            shards_ok += 1
    # 部分分片成功若被当成功, 显存预估偏乐观 → 启动后 OOM 且用户无从知道
    # 估算基于残缺数据(Windows 版同型修复: Ok 仅全部分片成功才置位)
    r["ok"] = shards_ok > 0 and shards_ok == shards_total
    r["partial"] = 0 < shards_ok < shards_total
    # 缺片是另一种"残缺": 枚举只给存在的分片, 于是每片都解析成功也照样"完整"。
    _miss = gguf_shard_missing(model_path)
    r["missing_shards"] = _miss if _miss > 0 else 0
    if _miss > 0:
        r["ok"], r["partial"] = False, shards_ok > 0
    return r


def estimate_vram_needed(profile, defs, query_gpu_fn):
    """显存预算(公式,对齐 llama breakdown;A.1 最终算法)。

    total = 权重(GPU)×0.95 + KV + 计算图 + 运行时基线 + 视觉编码器 + 草稿模型
    返回 dict 或 None;warnings 列出元数据缺失等告警,detail 为展示文本。
    """
    try:
        st = ParamState(profile, defs)
        model_path, m_err = resolve_model_path(profile.get("ModelPath"))
        if m_err or not model_path or not os.path.isfile(model_path):
            return None
        file_bytes = gguf_shard_bytes(model_path)
        if file_bytes <= 0:
            return None

        mmproj_bytes = 0.0
        mmpp = (profile.get("MmprojPath") or "").strip().strip('"')
        if not profile.get("NoMmprojOffload") and mmpp and os.path.isfile(mmpp):
            mmproj_bytes = float(os.path.getsize(mmpp))

        gi = read_gguf_arch(model_path)
        layers = gi["layers"]
        head_dim = gi["head_dim"] if gi["head_dim"] > 0 else (
            int(gi["embed"] / gi["head_count"]) if gi["embed"] > 0 and gi["head_count"] > 0 else 128)
        full_layers = layers
        if layers > 0 and gi["full_attention_interval"] > 0:
            full_layers = max(1, -(-layers // gi["full_attention_interval"]))  # ceil

        # 上 GPU 的层数: ngl=-1/auto/缺失 视为全层(fit 会下调,预算偏保守)
        gpu_layers = layers
        gpu_frac = 1.0
        ngl_str = st.role_text("ngl")
        if ngl_str:
            ngl_v = _try_float(ngl_str.strip())
            if ngl_v is not None and ngl_v >= 0.0:
                gpu_layers = int(min(ngl_v, layers)) if layers > 0 else int(ngl_v)
                if layers > 0:
                    gpu_frac = min(1.0, max(0.0, ngl_v / layers))
        # 移 CPU 的专家层数: --cpu-moe=全部; --n-cpu-moe N=前 N 层专家
        ncm_str = st.role_text("n_cpu_moe")
        ncm_v = 0.0
        try:
            ncm_v = float(ncm_str)
        except (TypeError, ValueError):
            ncm_v = 0.0
        has_ncm = ncm_v > 0.0
        moe_cpu_layers = 0
        if st.role_bool("cpu_moe"):
            moe_cpu_layers = layers
        elif has_ncm:
            moe_cpu_layers = int(min(max(round(ncm_v), 0), layers)) if layers > 0 else 0

        ts = read_gguf_tensor_stats(model_path, layers)
        expert_gpu_bytes = 0.0
        if ts["ok"]:
            # 非 blk 张量(token_embd/output 等)在 ngl>0 时上 GPU
            embed_out = ts["embed_output_bytes"] if gpu_layers > 0 else 0.0
            dense_gpu = 0.0
            for l in range(min(gpu_layers, ts["layer_count"])):
                dense_gpu += ts["dense_per_layer"][l]
                if l >= moe_cpu_layers:
                    expert_gpu_bytes += ts["expert_per_layer"][l]
            # moe-cache-mib: 专家权重显存缓存上限(MiB),超出部分驻留 CPU
            moe_detail = ""
            mcm_str = st.raw_value("moe-cache-mib")
            mcm_v = _try_float(mcm_str) if mcm_str else None
            if mcm_v is not None and mcm_v > 0.0 and expert_gpu_bytes > mcm_v * 1048576.0:
                expert_gpu_bytes = mcm_v * 1048576.0
                moe_detail = "(受 --moe-cache-mib 限制)"
            expert_cpu_bytes = ts["expert_total_bytes"] - expert_gpu_bytes
            gpu_weights = embed_out + dense_gpu + expert_gpu_bytes
            if ts["expert_total_bytes"] > 0.0:
                moe_detail = "(专家 %.2f GiB,其中 CPU 保留 %.2f GiB%s)" % (
                    ts["expert_total_bytes"] / GIB, expert_cpu_bytes / GIB,
                    (" " + moe_detail) if moe_detail else "")
            else:
                moe_detail = "(dense 模型,无专家张量)"
        else:
            # 张量表解析失败回退: 旧的层数比例近似
            weights = file_bytes
            if layers > 0 and has_ncm:
                weights *= min(1.0, max(0.02, 1.0 - ncm_v / layers))
            elif st.role_bool("cpu_moe"):
                weights *= 0.12
            gpu_weights = weights * gpu_frac
            moe_detail = "(张量表解析失败,按层比例近似)"

        # KV: 只有全注意力层随上下文线性增长;FA 关闭时 V 量化回退 f16
        ck = st.role_text("cache_type_k") or "f16"
        cv = st.role_text("cache_type_v") or "f16"
        kv_note = ""
        fa_str = (st.eff_value("flash-attn") or "").strip()
        if fa_str == "off" and cv != "f16":
            cv = "f16"
            kv_note = "(FA 关闭,V 量化回退 f16)"
        ctx = 0
        ctx_str = st.role_text("ctx_size")
        if ctx_str:
            try:
                ctx = int(float(norm_tok(ctx_str)))
            except ValueError:
                ctx = 0
        kv_per_tok = full_layers * gi["kv_heads"] * head_dim * (cache_bytes_per_elem(ck) + cache_bytes_per_elem(cv))
        kv_bytes = kv_per_tok * ctx * gpu_frac

        # SWA/混合注意力层三分类: 滑动窗口 / 线性注意力(SSM) / 未知(警告)
        swa_layers = max(0, layers - full_layers)
        swa_kv_bytes = 0.0
        rec_state_bytes = 0.0
        swa_note = ""
        meta_warn = []
        # 缺片必须单独说: 枚举只列出存在的分片, 所以"每片都读通了"也可能是残缺模型 ——
        # 权重按已有分片算(少一整片), 而 llama-server 加载时会直接报缺片。
        if ts.get("missing_shards"):
            meta_warn.append("⚠ 分片不完整: 声明的分片里有 %d 片不在位(按已存在的分片估算, 权重偏小), "
                             "llama-server 加载时会报缺片 —— 请补全分片或改用单文件模型"
                             % ts["missing_shards"])
        elif ts.get("partial"):
            meta_warn.append("⚠ 张量表仅解析到部分分片, 显存预估可能偏乐观(请核对分片文件是否齐全)")
        swa_full = st.bools.get("swa-full", False)
        if swa_layers > 0:
            if gi["sliding_window"] > 0 and gi["kv_heads"] > 0 and head_dim > 0 and ctx > 0:
                swa_ctx = ctx if swa_full else min(ctx, gi["sliding_window"])
                swa_kv_bytes = swa_layers * gi["kv_heads"] * head_dim * (
                    cache_bytes_per_elem(ck) + cache_bytes_per_elem(cv)) * swa_ctx * gpu_frac
                swa_note = "(含 SWA %d 层 × %s: %.0f MiB)" % (
                    swa_layers, "全 ctx" if swa_full else "窗口 %d" % gi["sliding_window"],
                    swa_kv_bytes / 1048576.0)
            elif gi["ssm_inner_size"] > 0 and gi["ssm_state_size"] > 0:
                # conv 状态 + recurrent 状态(f32),常数级/层,不随 ctx 线性但随槽数增长
                rec_state_bytes = swa_layers * (
                    gi["ssm_inner_size"] * gi["ssm_conv_kernel"] + gi["ssm_inner_size"] * gi["ssm_state_size"]) * 4.0 * gpu_frac
                swa_note = "(含线性注意力 %d 层,state 不随 ctx 增长: %.0f MiB)" % (
                    swa_layers, rec_state_bytes / 1048576.0)
            else:
                meta_warn.append("⚠ %d 个混合注意力层(窗口/状态未知)KV 未计入预算(如遇 OOM 请实测)" % swa_layers)
        # 线性注意力 state 按槽数线性增长(实测 149.62 MiB = 1 seq;--parallel N → N 份)
        if rec_state_bytes > 0.0:
            par_str = (st.raw_value("parallel") or "").strip()
            par_n = _try_int(par_str)
            if par_n is not None and par_n > 1:
                rec_state_bytes *= par_n
                swa_note = swa_note.replace("MiB)", "MiB × %d 槽)" % par_n)
            elif par_n is not None and par_n < 0:
                meta_warn.append("⚠ --parallel -1(auto):槽数由 ctx 自动推导,线性注意力 state 会成倍增长(预算按 1 槽计)")
        kv_bytes += swa_kv_bytes + rec_state_bytes

        # 计算图: n_ubatch × n_embd × 173B(实测校准: qwen35/ubatch512 = 432 MiB;±50%)
        ubatch = 512.0
        ub_str = st.role_text("ubatch_size")
        ub_v = _try_float(ub_str) if ub_str else None
        if ub_v is not None and ub_v > 0.0:
            ubatch = ub_v
        embed = gi["embed"] if gi["embed"] > 0 else 4096.0
        compute_bytes = ubatch * embed * 173.0
        # 输出 logits 缓冲不占显卡 VRAM(0.7.0 起放 host 侧,动态分配),不计入
        # 草稿模型(推测解码激活时)整体进显存;--spec-draft-ngl 0 时驻留 CPU 不计入
        draft_bytes = 0.0
        if is_spec_decoding_active(st, defs):
            dp = (profile.get("MtpPath") or "").strip().strip('"')
            sdm = st.raw_value("spec-draft-model")
            if sdm:
                dp = sdm
            draft_on_cpu = (st.raw_value("spec-draft-ngl") or "").strip() == "0"
            if not draft_on_cpu and dp and os.path.isfile(dp):
                draft_bytes = gguf_shard_bytes(dp)
        # 运行时基线(后端 context/队列等): Vulkan 实测 ~300 MiB
        runtime_bytes = 300.0 * 1048576.0
        # 权重显存 = 张量字节 × 0.95(CPU 回退 + 对齐损耗,实测 breakdown ~95%)
        total = gpu_weights * 0.95 + kv_bytes + compute_bytes + runtime_bytes + mmproj_bytes + draft_bytes

        g = query_gpu_fn() or {}
        total_vram = _try_float(g.get("vram_total")) or 0.0
        used_vram = _try_float(g.get("vram_used")) or 0.0
        avail = max(0.0, total_vram - used_vram)
        # 探针读不到显存时 total_vram=0, 与"显存 0 的显卡"同形。这种机器(CPU-only、
        # i915 无 mem_info_vram_*、nvidia-smi 挂了)以前会得出 fits=True ⇒ 面板写"在可用
        # 范围内", 用户据此启动然后 bad allocation。C# 侧有第三态("未取到显卡显存信息,
        # 无法判断"), Linux 移植时丢了 —— 这里补回三态: fits=None 表示无法判断。
        vram_known = total_vram > 0.0

        if layers <= 0 or gi["kv_heads"] <= 0 or ctx <= 0:
            meta_warn.append("⚠ KV 元数据不完整(层数=%d KV头=%d ctx=%d),KV 可能算成 0,预算仅供参考" % (layers, gi["kv_heads"], ctx))
        fa_auto = (st.eff_value("flash-attn") or "").strip()
        if fa_auto == "auto" and cv != "f16":
            meta_warn.append("⚠ FA=auto:若后端不支持 FA,V 缓存会回退 f16,实际占用更高")
        lora_str = st.raw_value("lora")
        if lora_str:
            meta_warn.append("⚠ LoRA 适配器占用未计入(按文件大小预留)")

        detail = ("显存预算(公式,对齐 llama breakdown)\n"
                  + "权重(GPU): %.2f GiB%s\n" % (gpu_weights * 0.95 / GIB, moe_detail)
                  + "KV(%s/%s, %d 层全注意力): %.2f GiB%s\n" % (ck, cv, full_layers, kv_bytes / GIB, swa_note)
                  + "计算图(ubatch %.0f): %.0f MiB\n" % (ubatch, compute_bytes / 1048576.0)
                  + (("草稿模型: %.2f GiB\n" % (draft_bytes / GIB)) if draft_bytes > 0.0 else "")
                  + "运行时基线: %.0f MiB\n" % (runtime_bytes / 1048576.0)
                  + (("视觉编码器: %.0f MiB\n" % (mmproj_bytes / 1048576.0)) if mmproj_bytes > 0 else "")
                  + "预计总占用: %.2f GiB\n" % (total / GIB)
                  + ("可用显存: %.2f GiB" % (avail / GIB) if vram_known
                     else "可用显存: 未取到(显卡探针无数据, 无法判断能否装下)"))
        if meta_warn:
            detail = "\n".join(meta_warn) + "\n" + detail
        if kv_note:
            detail = detail.replace("KV(%s/%s" % (ck, cv), "KV(%s/%s%s" % (ck, cv, kv_note.replace("(FA 关闭", "(FA 关闭")))
        return {"est_bytes": total, "model_bytes": file_bytes, "kv_bytes": kv_bytes,
                "avail_bytes": avail, "detail": detail, "ok": True,
                "warnings": meta_warn,
                # None = 探针没有显存数据, 无法判断(调用方不得当成"装得下")
                "fits": (total <= avail) if vram_known else None,
                "vram_known": vram_known,
                "breakdown": {"weights": gpu_weights * 0.95, "kv": kv_bytes,
                              "compute": compute_bytes, "runtime": runtime_bytes,
                              "mmproj": mmproj_bytes, "draft": draft_bytes}}
    except Exception:
        return None


def _try_int(s):
    try:
        return int(str(s).strip())
    except (TypeError, ValueError):
        return None


def qwen35_partial_offload_warning(profile, defs):
    """非 qwen35/全量卸载时返回 None,否则返回警告文案。"""
    try:
        st = ParamState(profile, defs)
        model_path, m_err = resolve_model_path(profile.get("ModelPath"))
        if m_err or not model_path or not os.path.isfile(model_path):
            return None
        gi = read_gguf_arch(model_path)
        if not gi["ok"] or gi["layers"] <= 0 or gi["architecture"].lower() != "qwen35":
            return None
        ngl = st.role_text("ngl")
        ngl_v = _try_float(ngl) if ngl else None
        if ngl_v is not None and ngl_v >= 0.0 and ngl_v < gi["layers"]:
            return ("模型架构 %s(Gated Delta Net 线性注意力)检测到部分卸载(--gpu-layers %d < %d 层)。"
                    "部分卸载会禁用 fused op,compute/KV 缓冲可能整段驻留显存,极易 OOM 崩溃。"
                    "建议 --gpu-layers 设为 %d(全量卸载)。" % (gi["architecture"], int(ngl_v), gi["layers"], gi["layers"]))
        if st.role_bool("cpu_moe"):
            return ("模型架构 %s(Gated Delta Net 线性注意力)检测到 MoE 专家卸载到 CPU(--cpu-moe 生效)。"
                    "该架构需要全量 GPU 卸载才能启用 fused op,否则 compute/KV 缓冲可能整段驻留显存,极易 OOM 崩溃。"
                    "建议关闭 --cpu-moe。" % gi["architecture"])
        ncm = st.role_text("n_cpu_moe")
        ncm_v = _try_float(ncm) if ncm else None
        if ncm_v is not None and ncm_v > 0.0:
            return ("模型架构 %s(Gated Delta Net 线性注意力)检测到部分 MoE 专家卸载到 CPU(--n-cpu-moe %d)。"
                    "该架构需要全量 GPU 卸载才能启用 fused op,否则 compute/KV 缓冲可能整段驻留显存,极易 OOM 崩溃。"
                    "建议 --n-cpu-moe 设为 0。" % (gi["architecture"], int(ncm_v)))
    except Exception:
        pass
    return None
