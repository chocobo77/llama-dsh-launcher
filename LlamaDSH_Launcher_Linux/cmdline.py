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
        first_shards = sorted(glob.glob(os.path.join(raw, "*-00001-of-*.gguf")))
        if first_shards:
            return first_shards[0], None
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
    """返回 dict(ok, layers, kv_heads, head_dim, context_len, architecture)。"""
    r = {"ok": False, "layers": 0, "kv_heads": 0, "head_dim": 0, "context_len": 0, "architecture": ""}
    try:
        with open(model_path, "rb") as f:
            magic = f.read(4)
            if magic != b"GGUF":
                return r
            f.read(4)      # version
            f.read(8)      # tensor_count(u64)
            (kv_count,) = struct.unpack("<Q", f.read(8))
            embed = heads = 0
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
                        heads = v
                    elif key.endswith(".attention.key_length"):
                        r["head_dim"] = v
                    elif key.endswith(".attention.value_length") and r["head_dim"] == 0:
                        r["head_dim"] = v
                    elif key.endswith(".embedding_length"):
                        embed = v
                    elif key.endswith(".context_length"):
                        r["context_len"] = v
                elif vt in (10, 11):
                    (v,) = struct.unpack("<Q", f.read(8))
                    if key.endswith(".context_length"):
                        r["context_len"] = v
                elif vt == 8:
                    v = _read_gguf_string(f)
                    if key == "general.architecture":
                        r["architecture"] = v
                else:
                    _skip_gguf_value(f, vt)
            if r["head_dim"] == 0 and heads > 0 and embed > 0:
                r["head_dim"] = int(round(embed / heads))
            if r["layers"] > 0 and r["kv_heads"] > 0 and r["head_dim"] > 0:
                r["ok"] = True
    except Exception:
        r["ok"] = False
    return r


def cache_bytes_per_elem(cache_type):
    t = (cache_type or "").strip().lower()
    return {"f32": 4.0, "f16": 2.0, "bf16": 2.0, "q8_0": 1.0625, "q4_0": 0.5625,
            "iq4_nl": 0.5625, "q4_1": 0.625, "q5_0": 0.6875, "q5_1": 0.75,
            "q6_0": 0.875, "tq3_0": 0.4375}.get(t, 2.0)


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
    """返回 (cmd_list, violations)。异常直接抛出(与原版一致,由调用方弹窗)。"""
    st = ParamState(profile, defs)
    exe, err = find_llama_server(profile.get("LlamaDir"))
    if err:
        raise FileNotFoundError(err)
    lst = [exe]

    model_path, m_err = resolve_model_path(profile.get("ModelPath"))
    if m_err:
        raise FileNotFoundError(m_err)
    if model_path:
        lst += ["--model", model_path]
    mtp = (profile.get("MtpPath") or "").strip().strip('"').strip()
    if mtp and is_spec_decoding_active(st, defs):
        lst += ["--model-draft", mtp]
    mmproj_file = (profile.get("MmprojPath") or "").strip().strip('"').strip()
    mmproj_url = (profile.get("MmprojUrl") or "").strip().strip('"').strip()
    if mmproj_file:
        lst += ["--mmproj", mmproj_file]
    elif mmproj_url:
        lst += ["--mmproj-url", mmproj_url]
    if profile.get("NoMmprojOffload") and (mmproj_file or mmproj_url):
        lst.append("--no-mmproj-offload")

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
        else:
            v = st.effective_value(d)
            if d.get("role") == "ctx_size":
                v = norm_tok(v)
            if v:
                lst += ["--" + flag, v]
    # "取消勾选=关闭"语义: 配置了 off 语义的参数,未勾选时显式发射关闭值/否定旗标,
    # 覆盖 llama-server 默认值不是"关闭"的选项(--no-jinja / --flash-attn off / --spec-type none 等)
    for d in defs:
        flag = d["flag"]
        if flag in ("model", "model-draft") or d.get("hidden") or flag in on:
            continue
        if d["type"] == "Bool":
            if d.get("offFlag"):
                lst.append("--" + d["offFlag"])
        elif d.get("offValue"):
            lst += ["--" + flag, d["offValue"]]
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


def fmt_cmd(cmd_list):
    if not cmd_list:
        return ""
    out = [qa(cmd_list[0])]
    i = 1
    while i < len(cmd_list):
        t = cmd_list[i]
        if t.startswith("--") and i + 1 < len(cmd_list) and not cmd_list[i + 1].startswith("--"):
            out.append("    %s %s" % (qa(t), qa(cmd_list[i + 1])))
            i += 2
        else:
            out.append("    " + qa(t))
            i += 1
    return "\n".join(out)


# ---------------------------------------------------------------- 显存估算 --

def estimate_vram_needed(profile, defs, query_gpu_fn):
    """返回 dict 或 None:est_bytes/model_bytes/kv_bytes/avail_bytes/detail/ok。"""
    try:
        st = ParamState(profile, defs)
        model_path, m_err = resolve_model_path(profile.get("ModelPath"))
        if m_err or not model_path or not os.path.isfile(model_path):
            return None
        model_bytes = float(os.path.getsize(model_path))
        if model_bytes <= 0:
            return None
        m = re.search(r"-(\d{5})-of-(\d{5})\.gguf$", os.path.basename(model_path), re.I)
        if m:
            sdir = os.path.dirname(model_path)
            sbase = os.path.basename(model_path)
            dash = sbase.rfind("-" + m.group(1))
            if dash > 0:
                sbase = sbase[:dash]
                for sh in glob.glob(os.path.join(sdir, sbase + "-*-of-*.gguf")):
                    if os.path.abspath(sh) != os.path.abspath(model_path):
                        model_bytes += os.path.getsize(sh)
        mmpp = (profile.get("MmprojPath") or "").strip().strip('"')
        mm_cpu = bool(profile.get("NoMmprojOffload"))
        if not mm_cpu and mmpp and os.path.isfile(mmpp):
            model_bytes += os.path.getsize(mmpp)

        ctx = 0
        ctx_str = st.role_text("ctx_size")
        if ctx_str:
            ctx_str = norm_tok(ctx_str)
            try:
                ctx = int(float(ctx_str))
            except ValueError:
                ctx = 0
        ck = st.role_text("cache_type_k") or "f16"
        cv = st.role_text("cache_type_v") or "f16"
        bytes_k = cache_bytes_per_elem(ck)
        bytes_v = cache_bytes_per_elem(cv)
        gi = read_gguf_arch(model_path)
        gpu_frac = 1.0
        ngl = st.role_text("ngl")
        ngl_v = _try_float(ngl) if ngl else None
        if ngl_v is not None and ngl_v >= 0.0 and gi["ok"] and gi["layers"] > 0:
            gpu_frac = min(1.0, max(0.0, ngl_v / gi["layers"]))
        cpu_moe_on = st.role_bool("cpu_moe")
        moe_factor = 1.0
        if cpu_moe_on:
            moe_factor = 0.12
        else:
            ncm = st.role_text("n_cpu_moe")
            ncm_v = _try_float(ncm) if ncm else None
            if ncm_v is not None and ncm_v > 0 and gi["ok"] and gi["layers"] > 0:
                moe_factor = min(1.0, 0.12 + 0.88 * (ncm_v / gi["layers"]))
        model_file_bytes = model_bytes
        if moe_factor < 1.0:
            model_bytes *= moe_factor
        if gi["ok"]:
            kv_per_token = gi["kv_heads"] * gi["head_dim"] * (bytes_k + bytes_v) * gi["layers"]
        else:
            kv_per_token = 64.0 * 8.0 * 128.0 * (bytes_k + bytes_v)
        kv_bytes = kv_per_token * ctx * gpu_frac
        buffer_bytes = 1.5 * GIB
        est_vram = model_bytes * gpu_frac + kv_bytes + buffer_bytes

        g = query_gpu_fn() or {}
        total = _try_float(g.get("vram_total")) or 0.0
        used = _try_float(g.get("vram_used")) or 0.0
        avail = max(0.0, total - used)

        safe_ctx = 0
        gpu_kv_per_token = kv_per_token * gpu_frac
        if gpu_frac > 0.0 and gpu_kv_per_token > 0.0 and avail > 0.0:
            safe_ctx = int((avail - model_bytes * gpu_frac - buffer_bytes) / gpu_kv_per_token)
            if safe_ctx < 0:
                safe_ctx = 0
            safe_ctx = (safe_ctx // 1024) * 1024

        detail = ("模型文件: %.1f GiB\n" % (model_file_bytes / GIB)
                  + "上下文: %d token (%dK)\n" % (ctx, ctx // 1024)
                  + "KV 缓存(按当前缓存类型估算): %.2f GiB\n" % (kv_bytes / GIB)
                  + "预计总占用: %.1f GiB\n" % (est_vram / GIB)
                  + "可用显存: %.1f GiB" % (avail / GIB))
        if moe_factor < 1.0:
            detail += "\nMoE 专家在 CPU: 显存按系数 %.2f 折算" % moe_factor
        if safe_ctx > 0:
            detail += "\n建议: 当前显存下安全上下文约 %d token (%dK)" % (safe_ctx, safe_ctx // 1024)
        return {"est_bytes": est_vram, "model_bytes": model_bytes, "kv_bytes": kv_bytes,
                "avail_bytes": avail, "detail": detail, "ok": True}
    except Exception:
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
