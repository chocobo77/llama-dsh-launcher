# -*- coding: utf-8 -*-
"""系统/GPU 监控(Strata Monitor 采集通道的 Linux 适配)。

- GPU 按卡:nvidia-smi --query-gpu(多卡逐行)→ amd-smi/rocm-smi → sysfs/drm+hwmon 兜底;
  字段:型号/利用率/显存 used·total/温度/功耗(及上限)/风扇/频率/PCIe/P-State/驱动。
- 系统:CPU 总利用率 + 逐核利用率(/proc/stat 差分)、CPU 型号(cpuinfo)、
  内存(/proc/meminfo)、磁盘(os.statvfs + /proc/mounts)、网卡速率(/proc/net/dev 差分)。
- 结果缓存 1s,0 值读数不丢键(d227f3c 语义:用 is not None 判断,不用 falsy)。
"""
import os
import re
import glob
import threading
import time
import subprocess

_CACHE_SECS = 1.0
_gpu_cache = {"t": 0.0, "v": []}
_sys_cache = {"t": 0.0, "v": {}}
_vendor_cache = {"t": 0.0, "v": "unknown"}

# ---------------------------------------------------------------- CPU / 内存 --

_cpu_prev = {"t": 0.0, "cores": [], "util": 0.0, "core_utils": []}


def _read_cpu_times():
    """返回 (total_times, per_core_times) 各为 idle/total 列表。"""
    out = []
    with open("/proc/stat") as f:
        for line in f:
            if not line.startswith("cpu"):
                break
            parts = line.split()
            vals = [int(x) for x in parts[1:8]]  # user nice system idle iowait irq softirq
            idle = vals[3] + vals[4]
            out.append((idle, sum(vals)))
    return out[0], out[1:]


def cpu_mem_usage():
    try:
        totals = _read_cpu_times()
        total, cores = totals[0], totals[1:]
        now = time.time()
        prev = _cpu_prev
        if now - prev["t"] >= 0.5 and prev["cores"]:
            def util(old, new):
                di = new[0] - old[0]
                dt = new[1] - old[1]
                return max(0.0, min(100.0, 100.0 * (1.0 - di / dt))) if dt > 0 else None
            u = util(prev["total"], total)
            if u is not None:
                prev["util"] = u
            core_u = []
            for i, c in enumerate(cores):
                if i < len(prev["cores"]):
                    cu = util(prev["cores"][i], c)
                    if cu is not None:
                        core_u.append(cu)
            prev["core_utils"] = core_u
        prev["t"] = now
        prev["total"] = total
        prev["cores"] = cores
        info = {}
        with open("/proc/meminfo") as f:
            for line in f:
                k, v = line.split(":")
                info[k] = int(v.strip().split()[0]) * 1024  # kB → B
        mem_total = info.get("MemTotal", 0)
        mem_avail = info.get("MemAvailable", 0)
        mem_used = mem_total - mem_avail
        return {"cpu": round(prev["util"], 1),
                "cpu_cores": [round(x, 1) for x in prev["core_utils"]],
                "mem_used": mem_used, "mem_total": mem_total,
                "mem_pct": round(100.0 * mem_used / mem_total, 1) if mem_total else 0.0}
    except Exception:
        return {"cpu": 0.0, "cpu_cores": [], "mem_used": 0, "mem_total": 0, "mem_pct": 0.0}


def cpu_model():
    try:
        with open("/proc/cpuinfo") as f:
            for line in f:
                if line.startswith("model name"):
                    return line.split(":", 1)[1].strip()
    except OSError:
        pass
    return ""


# --------------------------------------------------------------------- 磁盘 --

_disk_prev = {"t": 0.0, "io": {}}


def _disk_statvfs():
    import string
    rows = []
    try:
        with open("/proc/mounts") as f:
            for ln in f:
                parts = ln.split()
                if len(parts) < 3:
                    continue
                dev, mp, fstype = parts[0], parts[1], parts[2]
                if not mp.startswith("/") or fstype.startswith(("proc", "sys", "dev", "tmpfs", "squashfs", "overlay", "efivarfs", "run")):
                    continue
                if mp.startswith("/snap/") or mp.startswith("/boot/efi"):
                    continue
                if not (dev.startswith("/dev/") or fstype in ("zfs", "btrfs", "f2fs")):
                    continue
                rows.append((dev, mp))
    except OSError:
        pass
    seen = set()
    out = []
    for dev, mp in rows:
        try:
            st = os.statvfs(mp)
            fr = st.f_frsize
            total = st.f_blocks * fr
            # used 不能写成 total - f_bavail: 那样 root 保留块(ext4 默认 5%)被算成"已用",
            # 2T 盘白多约 100G、进度条提前进红区, 且与 df/文件管理器的数字对不上
            # (f_bfree 才是"空闲块", f_bavail 是"非 root 可用块")。
            avail = st.f_bavail * fr
            used = (st.f_blocks - st.f_bfree) * fr
            if total <= 0 or mp in seen:
                continue
            seen.add(mp)
            pct_base = used + avail
            out.append({"dev": dev.rsplit("/", 1)[-1][:12], "mount": mp, "total": total,
                        "used": used, "avail": avail,
                        "pct": round(100.0 * used / pct_base, 1) if pct_base > 0 else 0.0})
        except OSError:
            continue
    return out[:6]


# 磁盘容量行后台快照: statvfs 打到失联的 fuse/网络设备会卡进内核 D 态,
# 每拍在采集线程上直接跑会把 llama 状态/GPU 采样/显存熔断整条链一起挂住。
# 后台 30s 刷新、采集侧只读缓存(Windows 版 DrivesCached 的 Linux 等价);
# 读写速率仍走 /proc/diskstats 每拍差分, 不受影响。
_disk_rows = {"rows": [], "at": 0.0, "busy": False}


def _disk_rows_cached():
    now = time.time()
    c = _disk_rows
    if now - c["at"] > 30.0 and not c["busy"]:
        c["busy"] = True

        def refresh():
            try:
                rows = _disk_statvfs()
                if rows:                    # 刷新失败保留旧行, 面板不清空
                    c["rows"] = rows
            except Exception:
                pass
            finally:
                c["at"] = time.time()
                c["busy"] = False
        threading.Thread(target=refresh, daemon=True).start()
    return [dict(d) for d in c["rows"]]


def _parent_disk(name):
    """分区名 → 整盘名: nvme0n1p2→nvme0n1、mmcblk0p1→mmcblk0、sda2→sda、xvda1→xvda。
    整盘名(sda/nvme0n1)原样返回。规则不对会导致整盘 I/O 被算到每个分区上(表头翻倍)。"""
    m = re.match(r"^(nvme\d+n\d+)p\d+$", name) or re.match(r"^(mmcblk\d+)p\d+$", name)
    if m:
        return m.group(1)
    m = re.match(r"^([a-z]+)(\d+)$", name)
    if m and m.group(1)[-1].isalpha() and m.group(1) not in ("md", "loop", "fd", "dm"):
        return m.group(1)     # md0/loop0 本身就是整设备, 不是 sda2 那种分区
    return name


def _disk_io_rates():
    rates = {}
    try:
        with open("/proc/diskstats") as f:
            now = time.time()
            cur = {}
            for ln in f:
                parts = ln.split()
                if len(parts) < 14:
                    continue
                name = parts[2]
                if not re.match(r"(sd|vd|nvme|hd)[a-z0-9]*$", name):
                    continue
                cur[name] = (int(parts[5]), int(parts[9]))  # 读扇区/写扇区
            prev = _disk_prev
            if prev["io"] and now - prev["t"] > 0.3:
                for name, (rs, ws) in cur.items():
                    if name in prev["io"]:
                        prs, pws = prev["io"][name]
                        rates[name] = (max(0, (rs - prs) * 512 / 1048576.0 / (now - prev["t"])),
                                       max(0, (ws - pws) * 512 / 1048576.0 / (now - prev["t"])))
            prev.update(t=now, io=cur)
    except OSError:
        pass
    return rates


# --------------------------------------------------------------------- 网络 --

_net_prev = {"t": 0.0, "dev": {}}


def net_rates():
    """前 6 个物理网卡: {name: (rx MB/s, tx MB/s)}。"""
    out = {}
    try:
        with open("/proc/net/dev") as f:
            now = time.time()
            cur = {}
            for ln in f.readlines()[2:]:
                if ":" not in ln:
                    continue
                name, data = ln.split(":", 1)
                name = name.strip()
                cells = data.split()
                if len(cells) < 16:
                    continue
                if name in ("lo",) or name.startswith(("veth", "docker", "br-", "virbr")):
                    continue
                cur[name] = (int(cells[0]), int(cells[8]))
            prev = _net_prev
            if prev["dev"] and now - prev["t"] > 0.3:
                for name, (rx, tx) in cur.items():
                    if name in prev["dev"]:
                        prx, ptx = prev["dev"][name]
                        dt = now - prev["t"]
                        r = (max(0, (rx - prx) / 1048576.0 / dt), max(0, (tx - ptx) / 1048576.0 / dt))
                        if r[0] > 0.01 or r[1] > 0.01:
                            out[name] = r
            prev.update(t=now, dev=cur)
    except OSError:
        pass
    return dict(sorted(out.items(), key=lambda kv: -(kv[1][0] + kv[1][1]))[:6])


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


def _hwmon_temperature(dev_dir):
    """从设备关联的 hwmon 读温度(°C)或 None。"""
    for hdir in glob.glob("/sys/class/hwmon/hwmon*"):
        try:
            name = open(os.path.join(hdir, "name")).read().strip().lower()
        except OSError:
            continue
        link = None
        try:
            link = os.path.realpath(hdir)
        except OSError:
            pass
        if link and os.path.realpath(dev_dir) not in link:
            continue
        for tp in sorted(glob.glob(os.path.join(hdir, "temp*_input"))):
            try:
                t = float(open(tp).read().strip()) / 1000.0
                if 0 < t < 150:
                    return t
            except (ValueError, OSError):
                continue
    return None


def _gpus_nvidia():
    out = _run(["nvidia-smi", "--query-gpu=index,name,utilization.gpu,memory.used,memory.total,"
                "temperature.gpu,power.draw,power.limit,fan.speed,clocks.current.graphics,"
                "pcie.link.gen.current,pcie.link.width.current,pstate,driver_version",
                "--format=csv,noheader,nounits"])
    cards = []
    for line in out.splitlines():
        cells = [c.strip() for c in line.split(",")]
        if len(cells) < 14:
            continue
        def num(i):
            try:
                return float(cells[i])
            except (ValueError, IndexError):
                return None
        cards.append({
            "index": int(num(0) or 0), "name": cells[1] or "NVIDIA GPU",
            "util": num(2), "vram_used": (num(3) or 0.0) * 1048576.0,
            "vram_total": (num(4) or 0.0) * 1048576.0,
            "temp": num(5), "power_w": num(6), "power_limit_w": num(7),
            "fan_pct": num(8), "clock_mhz": num(9),
            "pcie_gen": num(10), "pcie_width": num(11),
            "pstate": (cells[12] or "").replace("P", "P"), "driver": cells[13] or "",
            "src": "nvidia",
        })
    return cards


def _gpus_sysfs(vendor):
    """无厂商工具时读 DRM/sysfs + hwmon 合成单卡。"""
    r = {"util": None, "vram_used": None, "vram_total": None, "temp": None}
    for pdir in sorted(glob.glob("/sys/class/drm/card[0-9]*")):
        dev = os.path.join(pdir, "device")
        if not os.path.isdir(dev):
            continue
        busy = os.path.join(dev, "gpu_busy_percent")
        if os.path.isfile(busy) and r["util"] is None:
            try:
                r["util"] = float(open(busy).read().strip())
            except (ValueError, OSError):
                pass
        used_p = os.path.join(dev, "mem_info_vram_used")
        total_p = os.path.join(dev, "mem_info_vram_total")
        if os.path.isfile(used_p) and r["vram_used"] is None:
            try:
                r["vram_used"] = float(open(used_p).read().strip())
                if os.path.isfile(total_p):
                    r["vram_total"] = float(open(total_p).read().strip())
            except (ValueError, OSError):
                pass
        if r["temp"] is None:
            r["temp"] = _hwmon_temperature(dev)
    if r["vram_used"] is None and r["vram_total"] is None and r["util"] is None and r["temp"] is None:
        return []
    return [{"index": 0, "name": {"amd": "AMD GPU", "intel": "Intel GPU"}.get(vendor, "GPU"),
             "util": r["util"], "vram_used": r["vram_used"] or 0.0,
             "vram_total": r["vram_total"] or 0.0, "temp": r["temp"],
             "power_w": None, "power_limit_w": None, "fan_pct": None, "clock_mhz": None,
             "pcie_gen": None, "pcie_width": None, "pstate": "", "driver": "",
             "src": "sysfs", "synthetic": True}]


def query_gpus():
    """按卡 GPU 列表;结果缓存 1s。0 值读数不丢键(d227f3c)。"""
    now = time.time()
    if now - _gpu_cache["t"] < _CACHE_SECS and _gpu_cache["v"]:
        return _gpu_cache["v"]
    v = detect_vendor()
    cards = []
    if v == "nvidia":
        cards = _gpus_nvidia()
    if not cards:
        cards = _gpus_sysfs(v)
    # AMD/Intel 专属工具在 sysfs 之上补功耗/风扇(尽力而为)
    if not cards:
        cards = [{"index": 0, "name": "GPU(数据不可用)", "util": None, "vram_used": None,
                  "vram_total": None, "temp": None, "power_w": None, "power_limit_w": None,
                  "fan_pct": None, "clock_mhz": None, "pcie_gen": None, "pcie_width": None,
                  "pstate": "", "driver": "", "src": "none", "synthetic": True}]
    _gpu_cache.update(t=now, v=cards)
    return cards


def gpu_vendor_for_backend():
    """显卡厂商 → llama.cpp 后端偏好(cuda/sycl/vulkan)。读取实际驱动/sysfs。"""
    if detect_vendor() == "nvidia" or _run(["nvidia-smi", "-L"]):
        return "cuda"
    # lspci 分类(Intel Arc → sycl, AMD → vulkan)
    out = _run(["lspci"], timeout=10)
    low = out.lower()
    if "nvidia" in low and ("geforce" in low or "rtx" in low or "gtx" in low or "tesla" in low):
        return "cuda"
    if "intel" in low and ("arc" in low or "data center gpu" in low or "iris" in low):
        return "sycl"
    if "amd" in low or "ati" in low or "radeon" in low:
        return "vulkan"
    if detect_vendor() == "intel":
        return "sycl"
    if detect_vendor() == "amd":
        return "vulkan"
    return "vulkan"


def gpu_friendly_name():
    cards = query_gpus()
    for c in cards:
        if c.get("name") and not c.get("synthetic"):
            return c["name"]
    return cards[0]["name"] if cards else ""


def sample_system():
    """系统面板: CPU(总+逐核)/内存/磁盘/网络。缓存 1s。"""
    now = time.time()
    if now - _sys_cache["t"] < _CACHE_SECS and _sys_cache["v"]:
        return _sys_cache["v"]
    cm = cpu_mem_usage()
    disks = _disk_rows_cached()
    io = _disk_io_rates()
    # I/O 归属要按**分区名精确**匹配。旧写法是双向 startswith + next(): 同一块盘上挂两个
    # 分区(/ 与 /home)时, 两行都先匹配到 diskstats 里排在前面的整盘名 ⇒ 两行显示同一份
    # 整盘速率, 而表头是逐行相加的, 于是读写速率被数两遍(实测翻倍)。
    by_parent = {}
    for d in disks:
        by_parent.setdefault(_parent_disk(d["dev"]), []).append(d)
    for d in disks:
        rate, scope = io.get(d["dev"]), "part"
        if rate is None:
            par = _parent_disk(d["dev"])
            # 没有该分区的独立计数时才退到整盘, 且同一块盘只允许一行这么退(否则表头重复计数)
            if par in io and by_parent[par][0] is d:
                rate, scope = io[par], "disk"
        if rate:
            d["read_mb"], d["write_mb"], d["io_scope"] = round(rate[0], 1), round(rate[1], 1), scope
        else:
            d["read_mb"], d["write_mb"], d["io_scope"] = 0.0, 0.0, ""
    snap = {"cpu": cm["cpu"], "cpu_cores": cm["cpu_cores"], "cpu_model": cpu_model(),
            "mem_used": cm["mem_used"], "mem_total": cm["mem_total"], "mem_pct": cm["mem_pct"],
            "disks": disks, "net": {k: [round(v[0], 2), round(v[1], 2)] for k, v in net_rates().items()}}
    _sys_cache.update(t=now, v=snap)
    return snap


# ------------------------------------------------------------ 兼容旧接口 --

def query_gpu():
    """聚合视图(旧接口): util=均值 vram_used=和 vram_total=和 temp=最高。"""
    cards = query_gpus()
    utils = [c["util"] for c in cards if c.get("util") is not None]
    vus = [c.get("vram_used") or 0.0 for c in cards if c.get("vram_used") is not None]
    vts = [c.get("vram_total") or 0.0 for c in cards if c.get("vram_total") is not None]
    temps = [c.get("temp") for c in cards if c.get("temp") is not None]
    return {"gpu_util": round(sum(utils) / len(utils), 1) if utils else 0.0,
            "vram_used": sum(vus), "vram_total": sum(vts),
            "gpu_temp": max(temps) if temps else 0.0,
            "gpu_temp_src": detect_vendor() if temps else ""}


def has_gpu():
    return any((c.get("vram_total") or 0) > 0 for c in query_gpus())
