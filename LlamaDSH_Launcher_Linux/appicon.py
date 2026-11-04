#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""应用图标自举 —— 托盘/窗口/应用菜单图标在任何启动方式下都可见。

GNOME 的 ubuntu-appindicators 扩展解析托盘图标有两条路(读其源码
appIndicator.js `_getIconData`/`_createIcon` 确认):
 1) IconName + IconThemePath: 扩展用 Gtk.IconTheme(search_path=[IconThemePath])
    按**图标主题布局**查找, 即必须存在 `<IconThemePath>/hicolor/<尺寸>x<尺寸>/apps/<IconName>.png`;
    若 IconThemePath 本身已在系统默认主题搜索路径里(如 ~/.local/share/icons),
    扩展会直接走默认主题查找。
    → 把 IconThemePath 指向一个只放 icon64.png 的扁平 assets 目录永远查不到 = 没图标。
 2) IconPixmap 兜底: 名称查找失败时用自带位图, 格式 (宽, 高, ARGB32 网络字节序)。
两条都填: 换桌面/换发行版也不哑。纯标准库(zlib+struct 解 PNG), 不导入 GTK(托盘要微型)。
"""
import os
import struct
import zlib
import subprocess
import threading

APP_DIR = os.path.dirname(os.path.abspath(__file__))
ICON_NAME = "llamadsh-launcher"
SIZES = (16, 32, 24, 48, 64, 128, 256)   # 24 优先复用 icon32 缩放? 不做缩放: 缺尺寸就跳过
_lock = threading.Lock()
_theme_path = None          # 缓存: 自举成功后的 IconThemePath
_pixmaps = None             # 缓存: [(w, h, argb_bytes), ...]


def _src_for(size):
    p = os.path.join(APP_DIR, "assets", "icon%d.png" % size)
    return p if os.path.isfile(p) else None


def _ensure_index_theme(root):
    """hicolor 目录**缺 index.theme 时 Gtk/Shell 会整目录忽略**——图标文件铺好了也查不到
    (实测: ~/.local/share/icons/hicolor 里 6 个尺寸齐全, lookup 仍全 None;
    补上 index.theme 后 22/24/32 → 32x32、48 → 48x48 立刻命中)。"""
    hc = os.path.join(root, "hicolor")
    idx = os.path.join(hc, "index.theme")
    if os.path.isfile(idx):
        return True
    try:
        os.makedirs(hc, exist_ok=True)
    except OSError:
        return False
    sysidx = "/usr/share/icons/hicolor/index.theme"
    try:
        if os.path.isfile(sysidx):
            with open(sysidx, "rb") as f, open(idx + ".part", "wb") as g:
                g.write(f.read())
            os.replace(idx + ".part", idx)
            return True
    except OSError:
        pass
    dirs = ["%dx%d/apps" % (s, s) for s in SIZES if _src_for(s)]
    body = ["[Icon Theme]", "Name=hicolor", "Comment=LlamaDSH Launcher icons",
            "Directories=" + ",".join(dirs)]
    for d in dirs:
        size = int(d.split("x")[0])
        body += ["[%s]" % d, "Size=%d" % size, "Context=apps", "Type=directory",
                 "MinSize=%d" % size, "MaxSize=%d" % size]
    try:
        with open(idx + ".part", "w", encoding="utf-8") as g:
            g.write("\n".join(body) + "\n")
        os.replace(idx + ".part", idx)
        return True
    except OSError:
        return False


def _fill_hicolor(root):
    """把 assets 图标按主题布局铺进 root/hicolor/NxN/apps/ICON_NAME.png。
    返回**就位**的尺寸数(不是本次写入数: 早已装好时也算成功)。"""
    present = 0
    wrote = 0
    made_index = not os.path.isfile(os.path.join(root, "hicolor", "index.theme"))
    _ensure_index_theme(root)
    for size in SIZES:
        src = _src_for(size)
        if not src:
            continue
        dst_dir = os.path.join(root, "hicolor", "%dx%d" % (size, size), "apps")
        dst = os.path.join(dst_dir, ICON_NAME + ".png")
        try:
            if not (os.path.isfile(dst) and os.path.getsize(dst) == os.path.getsize(src)):
                os.makedirs(dst_dir, exist_ok=True)
                with open(src, "rb") as f, open(dst + ".part", "wb") as g:
                    g.write(f.read())
                os.replace(dst + ".part", dst)   # 半写文件会让主题读到坏 PNG
                wrote += 1
            present += 1
        except OSError:
            continue
    if wrote or made_index:
        try:  # 主题索引缓存(缺工具不影响按名查找)
            subprocess.run(["gtk-update-icon-cache", "-f", "-t", root],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                           timeout=10)
        except Exception:
            pass
    return present


def install_user_icons():
    """把图标装进用户图标主题(免 root), 失败退回私有缓存目录。返回 IconThemePath。"""
    global _theme_path
    with _lock:
        if _theme_path is not None:
            return _theme_path
        path = ""
        user_icons = os.path.expanduser("~/.local/share/icons")
        try:
            if _fill_hicolor(user_icons):
                path = user_icons      # 在默认搜索路径内 → 应用菜单/窗口/托盘统一按名命中
        except Exception:
            pass
        if not path:
            priv = os.path.join(os.path.expanduser("~/.cache"),
                                "llamadsh-launcher", "trayicons")
            try:
                if _fill_hicolor(priv):
                    path = priv
            except Exception:
                path = ""
        _theme_path = path
        return path


def theme_path():
    return install_user_icons()


# ------------------------------------------------------------------ SNI 位图 --

def _png_to_argb(path):
    """PNG(8bit, 非隔行, RGB/RGBA) → (w, h, ARGB32 网络字节序字节串)。纯标准库。"""
    with open(path, "rb") as f:
        data = f.read()
    if not data.startswith(b"\x89PNG\r\n\x1a\n"):
        raise ValueError("非 PNG")
    pos, idat, w, h, bd, ct, inter = 8, [], 0, 0, 0, 0, 0
    while pos + 8 <= len(data):
        ln = struct.unpack(">I", data[pos:pos + 4])[0]
        typ = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + ln]
        if typ == b"IHDR":
            w, h, bd, ct, _comp, _filt, inter = struct.unpack(">IIBBBBB", body)
        elif typ == b"IDAT":
            idat.append(body)
        elif typ == b"IEND":
            break
        pos += 12 + ln
    if bd != 8 or ct not in (2, 6) or inter:
        raise ValueError("仅支持 8bit 非隔行 RGB/RGBA(bd=%d ct=%d inter=%d)" % (bd, ct, inter))
    raw = zlib.decompress(b"".join(idat))
    bpp = 3 if ct == 2 else 4
    stride = w * bpp
    out = bytearray(h * stride)
    prev = bytearray(stride)
    p = 0
    for y in range(h):
        f = raw[p]
        p += 1
        line = bytearray(raw[p:p + stride])
        p += stride
        if f == 0:
            pass
        elif f == 1:      # Sub
            for i in range(bpp, stride):
                line[i] = (line[i] + line[i - bpp]) & 0xFF
        elif f == 2:      # Up
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 0xFF
        elif f == 3:      # Average
            for i in range(stride):
                a = line[i - bpp] if i >= bpp else 0
                line[i] = (line[i] + ((a + prev[i]) >> 1)) & 0xFF
        elif f == 4:      # Paeth
            for i in range(stride):
                a = line[i - bpp] if i >= bpp else 0
                b = prev[i]
                c = prev[i - bpp] if i >= bpp else 0
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pr) & 0xFF
        else:
            raise ValueError("未知 PNG filter %d" % f)
        out[y * stride:(y + 1) * stride] = line
        prev = line
    argb = bytearray(w * h * 4)
    for px in range(w * h):
        s = px * bpp
        d = px * 4
        argb[d] = out[s + 3] if ct == 6 else 255   # SNI 要求 ARGB(网络字节序)
        argb[d + 1] = out[s]
        argb[d + 2] = out[s + 1]
        argb[d + 3] = out[s + 2]
    return w, h, bytes(argb)


def icon_pixmaps():
    """托盘 IconPixmap(小尺寸在前, 桌面自己挑)。解不出则空表。"""
    global _pixmaps
    if _pixmaps is not None:
        return _pixmaps
    got = []
    for size in (32, 48, 64):
        src = _src_for(size)
        if not src:
            continue
        try:
            w, h, argb = _png_to_argb(src)
            got.append((w, h, argb))
        except Exception:
            continue
    _pixmaps = got
    return got


def window_icon_path():
    """窗口/桌面图标文件(优先仓库 assets, 其次自举后的主题文件)。"""
    p = os.path.join(APP_DIR, "assets", "icon256.png")
    if os.path.isfile(p):
        return p
    tp = theme_path()
    if tp:
        q = os.path.join(tp, "hicolor", "256x256", "apps", ICON_NAME + ".png")
        if os.path.isfile(q):
            return q
    return ""
