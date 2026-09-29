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

_lock = threading.Lock()


def default_profile():
    return {
        "ModelPath": "", "MtpPath": "", "MmprojPath": "", "MmprojUrl": "",
        "NoMmprojOffload": False,
        "LlamaDir": "", "DshDir": "",
        "DshMode": 0,
        "DshAdminMode": False,  # Linux 版默认关闭(原版 true 为读取 Windows 游戏内存所需)
        "MinimizeToTray": False,
        "BoolParams": {}, "ValueParams": {},
        "CmdPreview": "",
        "VramBreakerEnabled": False, "VramBreakerType": 0, "VramBreakerValue": 90.0,
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
    with _lock:
        with open(CFG_PATH_FILE, "w", encoding="utf-8") as f:
            json.dump({"Dir": d}, f, indent=2, ensure_ascii=False)


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
        return default_config(), str(ex)


def save_config(cfg):
    path = config_file_path()
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with _lock:
        with open(path, "w", encoding="utf-8") as f:
            json.dump(cfg, f, indent=2, ensure_ascii=False)
    return path


def load_state():
    try:
        if os.path.exists(STATE_FILE):
            with open(STATE_FILE, encoding="utf-8") as f:
                return json.load(f) or {}
    except Exception:
        pass
    return {}


def save_state(state):
    try:
        with _lock:
            with open(STATE_FILE, "w", encoding="utf-8") as f:
                json.dump(state, f, indent=2, ensure_ascii=False)
    except Exception:
        pass


def clear_state():
    try:
        if os.path.exists(STATE_FILE):
            os.remove(STATE_FILE)
    except Exception:
        pass


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
        with _lock:
            with open(BUILD_STATE_FILE, "w", encoding="utf-8") as f:
                json.dump({"head": head or "",
                           "builtAt": datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")},
                          f, indent=2, ensure_ascii=False)
    except Exception:
        pass


def migrate_profile_flags_in_config(cfg, defs):
    for p in (cfg.get("Profiles") or {}).values():
        schema.migrate_profile_flags(p, defs)
