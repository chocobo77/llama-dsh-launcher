# -*- coding: utf-8 -*-
"""系统/GPU 监控:CPU、内存、显卡利用率/显存/温度。

原版依赖 PerformanceCounter + WMI + gputemp.exe;Linux 版:
- CPU/内存: /proc/stat、/proc/meminfo
- GPU: nvidia-smi → amd-smi/rocm-smi → xpu-smi → sysfs/drm+hwmon 兜底
- 温度: 同上(hwmon),不再需要 gputemp.exe
"""
import os
import re
import glob
import time
import subprocess

_CACHE_SECS = 5.0
_cache = {"t": 0.0, "v": {}}
_vendor_cache = {"t": 0.0, "v": "unknown"}

# ---------------------------------------------------------------- CPU / 内存 --

_cpu_prev = {"t": 0.0, "total": 0, "idle": 0, "util": 0.0}


def cpu_mem_usage():
    try:
        with open("/proc/stat") as f:
            parts = f.readline().split()[1:]
        vals = [int(x) for x in parts]
        total = sum(vals)
        idle = vals[3] + vals[4]
        now = time.time()
        prev = _cpu_prev
        if now - prev["t"] >= 0.5 and total > prev["total"] >= 0:
            dt, di = total - prev["total"], idle - prev["idle"]
            prev["util"] = max(0.0, min(100.0, 100.0 * (1.0 - di / dt))) if dt > 0 else 0.0
        prev.update(t=now, total=total, idle=idle)
        mem_used = mem_total = 0
        with open("/proc/meminfo") as f:
            info = {}
            for line in f:
                k, v = line.split(":")
                info[k] = int(v.strip().split()[0])  # kB
        mem_total = info.get("MemTotal", 0) * 1024
        mem_avail = info.get("MemAvailable", 0) * 1024
        mem_used = mem_total - mem_avail
        return {"cpu": round(prev["util"], 1),
                "mem_used": mem_used, "mem_total": mem_total,
                "mem_pct": round(100.0 * mem_used / mem_total, 1) if mem_total else 0.0}
    except Exception:
        return {"cpu": 0.0, "mem_used": 0, "mem_total": 0, "mem_pct": 0.0}


# --------------------------------------------------------------------- GPU --

def _run(cmd, timeout=8):
    try:
        return subprocess.run(cmd, capture_output=True, text=True, timeout=timeout).stdout or ""
    except Exception:
        return ""


def detect_vendor():
    now = time.time()
    if now - _vendor_cache["t"] < 300:
        return _vendor_cache["v"]
    v = "unknown"
    if _run(["nvidia-smi", "-L"]):
        v = "nvidia"
    elif _run(["amd-smi", "list"]) or _run(["rocm-smi", "--showproduct"]):
        v = "amd"
    elif _run(["xpu-smi", "discovery"]):
        v = "intel"
    else:
        for pdir in glob.glob("/sys/class/drm/card[0-9]*"):
            try:
                drv = os.path.basename(os.path.realpath(os.path.join(pdir, "device", "driver")))
            except OSError:
                continue
            if drv in ("nvidia",):
                v = "nvidia"
                break
            if drv in ("amdgpu",):
                v = "amd"
                break
            if drv in ("i915", "xe"):
                v = "intel"
                break
    _vendor_cache.update(t=now, v=v)
    return v


def _query_nvidia():
    out = _run(["nvidia-smi", "--query-gpu=utilization.gpu,memory.used,memory.total,temperature.gpu",
                "--format=csv,noheader,nounits"])
    for line in out.splitlines():
        cells = [c.strip() for c in line.split(",")]
        if len(cells) < 4:
            continue
        try:
            return {"gpu_util": float(cells[0]), "vram_used": float(cells[1]) * 1048576,
                    "vram_total": float(cells[2]) * 1048576, "gpu_temp": float(cells[3]),
                    "gpu_temp_src": "NVIDIA"}
        except ValueError:
            continue
    return {}


def _query_amd():
    out = _run(["amd-smi", "metric", "--temperature", "--gpu-utilization", "--vram-usage"])
    if not out:
        out = _run(["rocm-smi", "--showuse", "--showmemuse", "--showtemp"])
    r = {}
    m = re.search(r"([\d.]+)\s*%", out)
    if m:
        r["gpu_util"] = float(m.group(1))
    temps = re.findall(r"([\d.]+)\s*C\b", out)
    if temps:
        r["gpu_temp"] = float(temps[0])
        r["gpu_temp_src"] = "AMD"
    m = re.search(r"([\d.]+)\s*MiB", out)
    if m:
        r["vram_used"] = float(m.group(1)) * 1048576
        r["vram_total"] = 0
    return r


def _query_intel():
    out = _run(["xpu-smi", "stats", "-d", "0"])
    r = {}
    m = re.search(r"GPU Utilization\s*[:=]\s*([\d.]+)", out) or re.search(r"([\d.]+)\s*%", out)
    if m:
        r["gpu_util"] = float(m.group(1))
    m = re.search(r"([\d.]+)\s*MiB", out)
    if m:
        r["vram_used"] = float(m.group(1)) * 1048576
    return r


def _query_sysfs():
    """无厂商工具时读 DRM/sysfs + hwmon。"""
    r = {}
    for pdir in sorted(glob.glob("/sys/class/drm/card[0-9]*")):
        dev = os.path.join(pdir, "device")
        if not os.path.isdir(dev):
            continue
        busy = os.path.join(dev, "gpu_busy_percent")
        if os.path.isfile(busy) and "gpu_util" not in r:
            try:
                r["gpu_util"] = float(open(busy).read().strip())
            except (ValueError, OSError):
                pass
        used_p = os.path.join(dev, "mem_info_vram_used")
        total_p = os.path.join(dev, "mem_info_vram_total")
        if os.path.isfile(used_p) and "vram_used" not in r:
            try:
                r["vram_used"] = float(open(used_p).read().strip())
                if os.path.isfile(total_p):
                    r["vram_total"] = float(open(total_p).read().strip())
            except (ValueError, OSError):
                pass
    for hdir in glob.glob("/sys/class/hwmon/hwmon*"):
        try:
            name = open(os.path.join(hdir, "name")).read().strip().lower()
        except OSError:
            continue
        if name in ("amdgpu", "i915", "xe", "nvidia", "nouveau") and "gpu_temp" not in r:
            for tp in sorted(glob.glob(os.path.join(hdir, "temp*_input"))):
                try:
                    t = float(open(tp).read().strip()) / 1000.0
                    if 0 < t < 150:
                        r["gpu_temp"] = t
                        r["gpu_temp_src"] = "Sensor"
                        break
                except (ValueError, OSError):
                    continue
    return r


def query_gpu():
    """QueryGpu:结果缓存 5s。返回 {gpu_util, vram_used, vram_total, gpu_temp, gpu_temp_src}。"""
    now = time.time()
    if now - _cache["t"] < _CACHE_SECS and _cache["v"]:
        return _cache["v"]
    v = detect_vendor()
    g = {}
    if v == "nvidia":
        g = _query_nvidia()
    elif v == "amd":
        g = _query_amd()
        if not g:
            g = _query_sysfs()
    elif v == "intel":
        g = _query_intel()
        if not g:
            g = _query_sysfs()
    else:
        g = _query_nvidia() or _query_amd() or _query_sysfs()
    g.setdefault("gpu_util", 0.0)
    g.setdefault("vram_used", 0.0)
    g.setdefault("vram_total", 0.0)
    g.setdefault("gpu_temp", 0.0)
    g.setdefault("gpu_temp_src", "")
    _cache.update(t=now, v=g)
    return g


def has_gpu():
    g = query_gpu()
    return g.get("vram_total", 0) > 0
