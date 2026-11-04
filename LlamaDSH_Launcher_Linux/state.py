# -*- coding: utf-8 -*-
"""配置/方案/状态持久化(与原版 JSON 文件互通,PascalCase 字段)。

- 配置: <配置目录>/LlamaDSH_Launcher_config.json  {Profiles, CurrentProfile, WindowWidth, WindowHeight}
- 路径: ~/.llamadsh_config_path.json              {"Dir": ...}
- 状态: ~/.dsh_launcher_state.json                {LlamaPid, DshPid, DshDir}
- 重建基线: ~/.llamadsh_dsh_build.json            {head, builtAt}
"""
import os
import json
import threading
import datetime

import schema

_HOME = os.path.expanduser("~")
CFG_PATH_FILE = os.path.join(_HOME, ".llamadsh_config_path.json")
STATE_FILE = os.path.join(_HOME, ".dsh_launcher_state.json")
BUILD_STATE_FILE = os.path.join(_HOME, ".llamadsh_dsh_build.json")
DEFAULT_CONFIG_FILENAME = "LlamaDSH_Launcher_config.json"

_lock = threading.RLock()

# 状态/基线写盘失败此前一律静默吞掉。这一族的后果不是"少写一个文件"而是接管失效:
# LlamaPid/DshPid 没落盘 → 重启后面板显示"未运行", 用户点"启动"就变成"端口已被占用/
# 启动失败"; save_build_state 失败则升级后 need_rebuild 判定永不触发, 直接跑旧产物。
# 故把失败交给一个可注入的日志出口(main 启动时接 HUB.log), 调用点不必各自处理。
_err_sink = None


def set_error_sink(fn):
    """接入日志出口(测试里传 None 即静默)。"""
    global _err_sink
    _err_sink = fn


def _fail(what, ex):
    if _err_sink is None:
        return
    try:
        _err_sink("[状态] ⚠ %s 写入失败(%s: %s) —— 重启后可能无法接管进程, 面板会显示未运行"
                  % (what, type(ex).__name__, ex))
    except Exception:
        pass


def default_profile():
    return {
        "ModelPath": "", "MtpPath": "", "MmprojPath": "", "MmprojUrl": "",
        "NoMmprojOffload": False,
        "LlamaDir": "", "DshDir": "",
        "DshMode": 0,
        "DshAdminMode": False,  # Linux 版默认关闭(原版 true 为读取 Windows 游戏内存所需)
        "MinimizeToTray": False,
        "BoolParams": {}, "ValueParams": {},
        # CmdPreview 为派生数据不落盘(与 CS [JsonIgnore] 语义一致)
        "VramBreakerEnabled": False, "VramBreakerType": 0, "VramBreakerValue": 90.0,
        # llama.cpp 变体(分支)列表与当前激活名;Dir 与 LlamaDir 冗余,以激活项为准
        "LlamaVariants": [], "ActiveLlamaVariant": "",
        # 命令行末尾原样追加的自定义参数
        "CustomArgs": [],
        # 实测设备侧显存(MiB)与指纹,换模型后作废
        "VramMeasuredMiB": 0.0, "VramMeasuredModel": "", "VramMeasuredFp": "",
        # OOM 自动降层重试(默认开)
        "OomRetryEnabled": True,
        # llama.cpp 后端: ""=按显卡自动;可显式指定 vulkan/rocm/cuda/sycl
        "LlamaBackend": "",
    }


def default_config():
    return {"Profiles": {"默认方案": default_profile()}, "CurrentProfile": "默认方案",
            "WindowWidth": 0, "WindowHeight": 0}


def documents_dir():
    d = os.path.join(_HOME, "Documents")
    if not os.path.isdir(d):
        try:
            os.makedirs(d, exist_ok=True)
        except OSError:
            d = _HOME
    return d


def get_config_dir():
    try:
        if os.path.exists(CFG_PATH_FILE):
            with open(CFG_PATH_FILE, encoding="utf-8") as f:
                d = (json.load(f) or {}).get("Dir") or ""
            if d and os.path.isdir(d):
                return d
    except Exception:
        pass
    return documents_dir()


def set_config_dir(d):
    d = (d or "").strip()
    if not d or not os.path.isdir(d):
        raise ValueError("目录不存在: %s" % d)
    _atomic_write_json(CFG_PATH_FILE, {"Dir": d})


def config_file_path():
    return os.path.join(get_config_dir(), DEFAULT_CONFIG_FILENAME)


def load_config():
    """读主配置;新格式失败回退旧格式(单方案)。返回 (config, error)。"""
    path = config_file_path()
    if not os.path.exists(path):
        cfg = default_config()
        return cfg, None
    try:
        with open(path, encoding="utf-8") as f:
            data = json.load(f)
        if isinstance(data, dict) and isinstance(data.get("Profiles"), dict):
            for name, p in data["Profiles"].items():
                base = default_profile()
                base.update(p or {})
                data["Profiles"][name] = base
            return data, None
        # 旧格式:整个 json 是单个 ProfileConfig
        base = default_profile()
        base.update(data or {})
        return {"Profiles": {"Default": base}, "CurrentProfile": "Default",
                "WindowWidth": 0, "WindowHeight": 0}, None
    except Exception as ex:
        # 损坏配置留底 .corrupt-*.bak,避免好配置被静默覆盖丢失(4f2ccf5 同款)
        try:
            bak = "%s.corrupt-%s.bak" % (path, datetime.datetime.now().strftime("%Y%m%d-%H%M%S"))
            os.replace(path, bak)
        except OSError:
            bak = "(留底失败)"
        return default_config(), "%s(原文件已留底: %s)" % (ex, bak)


def save_config(cfg):
    """原子写: tmp + os.replace(be42658 同款),复用 _atomic_write_json。"""
    path = config_file_path()
    os.makedirs(os.path.dirname(path), exist_ok=True)
    _atomic_write_json(path, cfg)
    return path


def load_state():
    try:
        if os.path.exists(STATE_FILE):
            with open(STATE_FILE, encoding="utf-8") as f:
                return json.load(f) or {}
    except Exception:
        pass
    return {}


def _atomic_write_json(path, obj):
    """tmp + os.replace 原子写(与 save_config 同语义):进程被杀/断电不留截断文件。"""
    tmp = path + ".tmp"
    with _lock:
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(obj, f, indent=2, ensure_ascii=False)
        os.replace(tmp, path)


def save_state(state):
    """全量替换 —— 调用方必须持有完整状态字典;只想更新个别键请用 update_state。"""
    try:
        _atomic_write_json(STATE_FILE, state)
    except Exception as ex:
        _fail("state.json(全量)", ex)


def update_state(**updates):
    """合并写:先读现有状态,只更新传入的键再原子写回。
    此前 core.py 用 save_state({"LlamaPid": ...}) 单键全量写,
    会抹掉 DshPid/DshDir,重启后另一侧进程无法接管。"""
    try:
        with _lock:
            st = load_state()
            st.update(updates)
            _atomic_write_json(STATE_FILE, st)
    except Exception as ex:
            _fail("state.json(合并写)", ex)


def clear_state():
    try:
        if os.path.exists(STATE_FILE):
            os.remove(STATE_FILE)
    except Exception as ex:
        _fail("state.json(清除)", ex)


def clear_state_for(which, state=None):
    st = state if state is not None else load_state()
    st.pop(which, None)
    if any(k in st for k in ("LlamaPid", "DshPid")):
        save_state(st)
    else:
        clear_state()


def load_build_state():
    try:
        if os.path.exists(BUILD_STATE_FILE):
            with open(BUILD_STATE_FILE, encoding="utf-8") as f:
                return json.load(f) or {}
    except Exception:
        pass
    return {}


def save_build_state(head):
    try:
        _atomic_write_json(BUILD_STATE_FILE, {"head": head or "",
                            "builtAt": datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")})
    except Exception as ex:
        # 这一条失败的另一半后果: 升级基线没记下, 下次 DSH 启动不会提示"仓库已升级需要
        # 全量重建", 于是直接跑旧编译产物然后崩。
        if _err_sink is not None:
            try:
                _err_sink("[状态] ⚠ DSH 重建基线写入失败(%s: %s) —— 升级后可能不再提示需要重建"
                          % (type(ex).__name__, ex))
            except Exception:
                pass


def migrate_profile_flags_in_config(cfg, defs):
    for p in (cfg.get("Profiles") or {}).values():
        schema.migrate_profile_flags(p, defs)
