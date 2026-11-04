#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""LlamaDSH Launcher — GTK3 客户端(与网页端同一套 UI,经 WebKit2 渲染)。

- 主窗口 = WebKit2 WebView 指向本地服务;关闭窗口 = 收进托盘并进入轻量化模式
  (QQ/微信式:后台只留低频保活,大幅降低资源占用;再次打开时重建窗口)
- 托盘 = 纯 Python 的 StatusNotifierItem + dbusmenu(GNOME ubuntu-appindicator
  扩展支持),左键/菜单"显示主窗口"重建窗口
- 服务自举:若本地服务(8788)未响应,自动以 detached 方式拉起 main.py——
  客户端退出不影响已启动的 llama/DSH(它们本就是分离会话)
- 运行于 root/无图形环境时自动降级为"仅服务+浏览器"并给出提示

用法:client.py [--minimized] [--url URL] [--port N]
"""
import os
import sys
import json
import time
import signal
import argparse
import threading
import subprocess
import urllib.request

APP_DIR = os.path.dirname(os.path.abspath(__file__))

try:  # python3-dbus: 托盘 SNI 需要; 缺失时客户端降级为无托盘模式
    import dbus
    import dbus.service
    _DBUS_OK = True
except Exception:
    dbus = None
    _DBUS_OK = False
APP_ID = "llamadsh-launcher"
DEFAULT_PORT = 8788
ICON_DIR = os.path.join(APP_DIR, "assets")

# ---------------------------------------------------------------- 服务自举 --

def _server_url(port):
    return "http://127.0.0.1:%d" % port


def _probe(url, timeout=1.5):
    try:
        with urllib.request.urlopen(url + "/api/status", timeout=timeout) as r:
            return json.loads(r.read().decode("utf-8", "replace"))
    except Exception:
        return None


def ensure_server(port):
    """服务未响应则拉起(detached, 客户端退出不影响)。返回实际可用的 URL。"""
    base = _server_url(port)
    if _probe(base) is not None:
        return base
    for i in range(20):  # 端口被占则顺延
        p = port + i
        if _probe(_server_url(p)) is not None:
            return _server_url(p)
    env = dict(os.environ)
    if os.geteuid() == 0 and os.environ.get("SUDO_USER"):
        env["HOME"] = "/home/" + os.environ["SUDO_USER"]  # 保险: sudo 血统修正
    subprocess.Popen([sys.executable, os.path.join(APP_DIR, "main.py"), "-p", str(port)],
                     cwd=APP_DIR, env=env, stdin=subprocess.DEVNULL,
                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                     start_new_session=True)
    for _ in range(40):
        st = _probe(base)
        if st is not None:
            return base
        time.sleep(0.5)
    for i in range(1, 20):  # 兜底: 扫顺延端口
        if _probe(_server_url(port + i)) is not None:
            return _server_url(port + i)
    return None


def _api_token():
    """main.py 启动时写入的 0600 token 文件; POST 需带 X-Auth。"""
    try:
        with open(os.path.expanduser("~/.dsh_launcher_token"), encoding="utf-8") as f:
            return f.read().strip()
    except OSError:
        return ""


def set_lightweight(base, on):
    try:
        req = urllib.request.Request(base + "/api/lightweight",
                                     data=json.dumps({"on": bool(on)}).encode(),
                                     headers={"Content-Type": "application/json",
                                              "X-Auth": _api_token()})
        urllib.request.urlopen(req, timeout=3)
        return True
    except Exception:
        return False


def open_dir_gtk(path):
    subprocess.Popen(["xdg-open", path], stdout=subprocess.DEVNULL,
                     stderr=subprocess.DEVNULL)

# ---------------------------------------------------------------- GTK 客户端 --

def run_gtk(base_url):
    import gi
    gi.require_version("Gtk", "3.0")
    gi.require_version("WebKit2", "4.1")
    from gi.repository import Gtk, GLib, Gdk, WebKit2

    state = {"win": None, "quit": False}

    # ---------------------------------------------------------- 轻量化切换 --
    def apply_lightweight(on):
        # T1: 关窗路径在 main_quit 前同步调用(带 3s 超时), daemon 线程会在
        # 进程退出时被硬杀 —— 异步发送会让轻量化永远到不了服务端
        ok = set_lightweight(base_url, on)
        print("[client] 轻量化模式: %s%s" % ("开(后台低频保活)" if on else "关",
              "" if ok else "(通知服务失败)"))

    # -------------------------------------------------------------- 主窗口 --
    def build_window():
        GLib.set_prgname(APP_ID)
        GLib.set_application_name("DeepSeek Harness & Llama 启动器")
        win = Gtk.Window(title="DeepSeek Harness & Llama 启动器")
        win.set_default_size(1400, 950)
        win.set_icon_from_file(os.path.join(ICON_DIR, "icon256.png"))
        try:
            import warnings as _w
            with _w.catch_warnings():
                _w.simplefilter("ignore", DeprecationWarning)
                win.set_wmclass(APP_ID, APP_ID)  # XWayland 下的 StartupWMClass
        except Exception:
            pass

        # WebKit 磁盘缓存: set_disk_cache_directory 在新版已废弃且"does nothing"
        # (实测告警), 改用 WebsiteDataManager —— 必须在创建 WebView 之前挂上
        ctx = WebKit2.WebContext.get_default()
        try:
            mgr = WebKit2.WebsiteDataManager(
                base_data_directory=os.path.expanduser(
                    "~/.local/share/llamadsh-launcher/webkit"),
                base_cache_directory=os.path.expanduser(
                    "~/.cache/llamadsh-launcher/webkit"))
            ctx.set_website_data_manager(mgr)
        except Exception:
            pass
        web = WebKit2.WebView()
        # 外部链接(http://127.0.0.1:3080 等)交给系统浏览器打开
        web.connect("decide-policy", _on_policy)
        sw = Gtk.ScrolledWindow()
        sw.add(web)
        win.add(sw)
        win.show_all()
        apply_lightweight(False)  # 窗口打开 = 恢复 3s 正常监控(轻量化只属于托盘驻留态)

        def on_delete(_w, _e):
            # QQ/微信式收进托盘: 窗口进程整体退出(WebKit 子进程随之退出),
            # 托盘助手(tray.py)常驻图标; 托盘"显示主窗口"时按需重新拉起本进程
            apply_lightweight(True)
            state["quit"] = True
            GLib.idle_add(Gtk.main_quit)
            return True  # 阻止默认销毁
        # SIGTERM/SIGINT 走同一路径(CPython 的 signal handler 在 Gtk.main() 阻塞于 C 时
        # 不会被调度; unix_signal_add 挂进 GLib 主循环才能及时处理注销会话/kill 场景)
        for sig in (signal.SIGTERM, signal.SIGINT):
            GLib.unix_signal_add(GLib.PRIORITY_DEFAULT, sig,
                                 lambda s=sig: (apply_lightweight(True),
                                                state.__setitem__("quit", True),
                                                Gtk.main_quit()) and False or False)
        win.connect("delete-event", on_delete)
        state["win"] = win
        web.load_uri(base_url)
        return win

    def _on_policy(_web, decision, dtype):
        try:
            import warnings as _w
            with _w.catch_warnings():
                # 新 WebKit 废弃 get_request(导航类才有 navigation_request),
                # 但非导航决定仍需它取 URI —— 静音警告, 语义不变
                _w.simplefilter("ignore", DeprecationWarning)
                uri = decision.get_request().get_uri()
            internal = uri == base_url or uri.startswith(base_url + "/")
            if dtype == WebKit2.PolicyDecisionType.RESPONSE and not internal:
                # 外部主资源响应交系统浏览器
                decision.ignore()
                subprocess.Popen(["xdg-open", uri], stdout=subprocess.DEVNULL,
                                 stderr=subprocess.DEVNULL)
                return
            if internal:
                decision.use()
                return
        except Exception:
            pass
        try:
            subprocess.Popen(["xdg-open", uri], stdout=subprocess.DEVNULL,
                             stderr=subprocess.DEVNULL)
        except Exception:
            pass
        decision.ignore()

    def show_window():
        build_window()

    def quit_client():
        state["quit"] = True
        apply_lightweight(True)  # 服务与已启动的 llama/DSH 都是分离会话, 继续运行
        if state["win"] is not None:
            state["win"].destroy()
        Gtk.main_quit()

    build_window()

    Gtk.main()


def _acquire_window_lock():
    """窗口进程单例: 已有窗口时再点托盘直接退出(fcntl + sys.exit)。"""
    import fcntl
    path = os.path.join(os.path.expanduser("~"), ".llamadsh_launcher_window.lock")
    fh = open(path, "w")
    try:
        fcntl.flock(fh, fcntl.LOCK_EX | fcntl.LOCK_NB)
        fh.write(str(os.getpid()))
        fh.flush()
        # fd 需要持有到进程结束: 挂到模块全局防止 GC 关闭释放锁
        global _win_lock_fh
        _win_lock_fh = fh
        return True
    except OSError:
        return False


_win_lock_fh = None


def main():
    ap = argparse.ArgumentParser(description="LlamaDSH Launcher 客户端(GTK)")
    ap.add_argument("-p", "--port", type=int, default=DEFAULT_PORT)
    ap.add_argument("--url", help="服务地址(由托盘助手传入)")
    ap.add_argument("--show", action="store_true", help="仅显示窗口(托盘助手调用)")
    args = ap.parse_args()

    if not _acquire_window_lock():
        print("[client] 主窗口已打开(另一进程持有窗口锁), 忽略本次请求")
        return
    base = args.url or ensure_server(args.port)
    if base is None:
        print("无法启动/连接本地服务(端口 %d 起)" % args.port)
        sys.exit(1)

    try:
        import gi  # noqa: F401
        gi.require_version("Gtk", "3.0")
        gi.require_version("WebKit2", "4.1")
    except Exception as ex:
        print("GTK/WebKit 不可用(%s)——降级为浏览器模式。" % ex)
        try:
            subprocess.Popen(["xdg-open", base], start_new_session=True)
        except Exception:
            pass
        print("服务已在后台运行: %s(浏览器打开该地址即主窗口)" % base)
        return

    print("[client] 服务: %s | 窗口关闭=收进托盘+轻量化" % base)
    run_gtk(base)


if __name__ == "__main__":
    main()
