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


def set_lightweight(base, on):
    try:
        req = urllib.request.Request(base + "/api/lightweight",
                                     data=json.dumps({"on": bool(on)}).encode(),
                                     headers={"Content-Type": "application/json"})
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

    dbus_loop_ok = False
    tray = None
    try:
        import dbus
        from dbus.mainloop.glib import DBusGMainLoop
        DBusGMainLoop(set_as_default=True)
        dbus_loop_ok = True
    except Exception:
        pass

    state = {"win": None, "lightweight": False, "quit": False}

    # ---------------------------------------------------------- 轻量化切换 --
    def apply_lightweight(on):
        state["lightweight"] = on
        threading.Thread(target=set_lightweight, args=(base_url, on), daemon=True).start()
        if tray is not None:
            tray.set_lightweight(on)
        print("[client] 轻量化模式: %s" % ("开(后台低频保活)" if on else "关"))

    # -------------------------------------------------------------- 主窗口 --
    def build_window():
        GLib.set_prgname(APP_ID)
        GLib.set_application_name("DeepSeek Harness & Llama 启动器")
        win = Gtk.Window(title="DeepSeek Harness & Llama 启动器")
        win.set_default_size(1400, 950)
        win.set_icon_from_file(os.path.join(ICON_DIR, "icon256.png"))
        try:
            win.set_wmclass(APP_ID, APP_ID)  # XWayland 下的 StartupWMClass
        except Exception:
            pass

        web = WebKit2.WebView()
        ctx = web.get_context()
        ctx.set_disk_cache_directory(os.path.expanduser(
            "~/.cache/llamadsh-launcher/webkit"))
        # 外部链接(http://127.0.0.1:3080 等)交给系统浏览器打开
        web.connect("decide-policy", _on_policy)
        sw = Gtk.ScrolledWindow()
        sw.add(web)
        win.add(sw)
        win.show_all()

        def on_delete(_w, _e):
            # QQ/微信式收进托盘: 窗口进程整体退出(WebKit 子进程随之退出),
            # 托盘助手(tray.py)常驻图标; 托盘"显示主窗口"时按需重新拉起本进程
            apply_lightweight(True)
            state["quit"] = True
            GLib.idle_add(Gtk.main_quit)
            return True  # 阻止默认销毁
        win.connect("delete-event", on_delete)
        state["win"] = win
        web.load_uri(base_url)
        return win

    def _on_policy(_web, decision, _type):
        try:
            uri = decision.get_request().get_uri()
            if uri.startswith(base_url):
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

    signal.signal(signal.SIGINT, lambda *_: GLib.idle_add(quit_client))
    Gtk.main()


# ------------------------------------------------------ SNI 托盘(纯 Python) --

class TrayIcon:
    """org.kde.StatusNotifierItem + com.canonical.dbusmenu 最小实现。

    GNOME(ubuntu-appindicator 扩展)即认这套协议; 左键 Activate 显示主窗口,
    右键弹出菜单: 显示主窗口 / 轻量化模式(复选) / 退出客户端。
    """

    MENU_SHOW, MENU_LIGHT, MENU_QUIT, MENU_MIN = 1, 2, 3, 4

    def __init__(self, base_url, on_show, on_light, on_quit, on_minimize):
        import dbus
        self._dbus = dbus
        self.base_url = base_url
        self.on_show = on_show
        self.on_light = on_light
        self.on_quit = on_quit
        self.on_minimize = on_minimize
        self.lightweight = False
        self.revision = 1
        self.bus = dbus.SessionBus()
        # 规范: org.kde.StatusNotifierItem-<pid>(不能把带 . : 的 URL 拼进总线名)
        self.name = "org.kde.StatusNotifierItem-%d" % os.getpid()
        self.item = _SniObject(self.bus, "/StatusNotifierItem", self)
        self.menu = _DBusMenuObject(self.bus, "/MenuBar", self)
        # 申请 well-known 名; 结果不校验——真正的注册是 RegisterStatusNotifierItem
        self.bus.request_name(self.name)

    def start(self):
        from gi.repository import GLib
        self._register()
        # watcher 重启后自动重注册(每 10s 兜底)
        GLib.timeout_add_seconds(10, self._register)

    def _register(self):
        try:
            watcher = self.bus.get_object("org.kde.StatusNotifierWatcher",
                                          "/StatusNotifierWatcher")
            watcher.RegisterStatusNotifierItem(self.name,
                dbus_interface="org.kde.StatusNotifierWatcher")
        except Exception:
            pass
        return True  # 保持 timer

    def set_lightweight(self, on):
        self.lightweight = bool(on)
        self.revision += 1

    def icon_path(self):
        for s in (64, 48, 32):
            p = os.path.join(ICON_DIR, "icon%d.png" % s)
            if os.path.isfile(p):
                return p
        return ""

    # dbusmenu 布局: (id, props, children)
    def _layout(self):
        return (0, {"children-display": "submenu"}, [
            (self.MENU_SHOW, {"label": "显示主窗口", "icon-name": "view-restored"}, []),
            (self.MENU_LIGHT, {"label": "轻量化模式(后台低频保活)",
                               "toggle-type": "checkmark",
                               "toggle-state": 1 if self.lightweight else 0}, []),
            (0, {}, []),  # 分隔线
            (self.MENU_MIN, {"label": "收进托盘(轻量化)"}, []),
            (self.MENU_QUIT, {"label": "退出客户端(服务与 llama/DSH 继续运行)"}, []),
        ])

    def _fire(self, item_id):
        from gi.repository import GLib
        if item_id == self.MENU_SHOW:
            GLib.idle_add(self.on_show)
        elif item_id == self.MENU_LIGHT:
            self.set_lightweight(not self.lightweight)
            GLib.idle_add(self.on_light, self.lightweight)
        elif item_id == self.MENU_MIN:
            GLib.idle_add(self.on_minimize)
        elif item_id == self.MENU_QUIT:
            GLib.idle_add(self.on_quit)


if _DBUS_OK:

    class _SniObject(dbus.service.Object):
        """org.kde.StatusNotifierItem + 手动实现的 Properties 接口。"""

        def __init__(self, bus, path, tray):
            self.tray = tray
            super().__init__(bus, path)

        @dbus.service.method("org.kde.StatusNotifierItem", in_signature="", out_signature="")
        def Activate(self):
            self.tray.on_show()

        @dbus.service.method("org.kde.StatusNotifierItem", in_signature="ii", out_signature="")
        def SecondaryActivate(self, x, y):
            self.tray.on_show()

        @dbus.service.method("org.kde.StatusNotifierItem", in_signature="ii", out_signature="")
        def ContextMenu(self, x, y):
            pass  # 菜单由 ItemIsMenu + /MenuBar 承载

        @dbus.service.method("org.kde.StatusNotifierItem", in_signature="", out_signature="s")
        def Title(self):
            return "LlamaDSH 启动器"

        @dbus.service.method("org.freedesktop.DBus.Properties", in_signature="ss", out_signature="v")
        def Get(self, iface, prop):
            return self.GetAll(iface).get(prop)

        @dbus.service.method("org.freedesktop.DBus.Properties", in_signature="s", out_signature="a{sv}")
        def GetAll(self, iface):
            return {
                "Category": "ApplicationStatus",
                "Id": "llamadsh-launcher",
                "Title": "LlamaDSH 启动器",
                "Status": "Active",
                "IconName": "llamadsh-launcher",
                "IconThemePath": ICON_DIR,
                "IconPixmap": dbus.Array([], signature="(iiay)"),
                "OverlayIconName": "",
                "AttentionIconName": "",
                "ItemIsMenu": True,
                "Menu": "/MenuBar",
                "ToolTip": dbus.Struct(
                    (dbus.String("llamadsh-launcher"),
                     dbus.Array([], signature="(iiay)"),
                     dbus.String("LlamaDSH 启动器"),
                     dbus.String("左键: 显示主窗口 | 右键: 菜单")),
                    signature=None),
            }

        @dbus.service.method("org.freedesktop.DBus.Properties", in_signature="ssv", out_signature="")
        def Set(self, iface, prop, value):
            pass

        @dbus.service.signal("org.freedesktop.DBus.Properties", signature="sa{sv}as")
        def PropertiesChanged(self, iface, changed, invalidated):
            pass

    class _DBusMenuObject(dbus.service.Object):
        """com.canonical.dbusmenu 最小实现(静态三项目录)。"""

        def __init__(self, bus, path, tray):
            self.tray = tray
            super().__init__(bus, path)

        @dbus.service.method("com.canonical.dbusmenu", in_signature="iia{ss}",
                             out_signature="u(ia{sv}av)")
        def GetLayout(self, parent, depth, props):
            return (self.tray.revision, _layout_node(self.tray._layout(), depth))

        @dbus.service.method("com.canonical.dbusmenu", in_signature="i", out_signature="b")
        def AboutToShow(self, _id):
            return False  # 菜单常开, 靠 revision 推送更新

        @dbus.service.method("com.canonical.dbusmenu", in_signature="ias", out_signature="a{ia{sv}}")
        def GetGroupProperties(self, ids, props):
            return dbus.Dictionary({}, signature="ia{sv}")

        @dbus.service.method("com.canonical.dbusmenu", in_signature="isvu", out_signature="")
        def Event(self, _id, event, _data, _ts):
            if event == "clicked":
                try:
                    self.tray._fire(int(_id))
                except Exception:
                    pass




def _layout_node(node, depth, variant=False):
    import dbus
    rid, props, children = node
    d = dbus.Dictionary(((dbus.String(k), v) for k, v in props.items()),
                        signature="sv")
    if depth == 0:
        kids = dbus.Array([], signature="v")
    else:
        # dbusmenu 规范: children 是 av —— 每个结构体包一层 Variant
        # (python-dbus 的类型构造器都支持 variant_level 参数)
        kids = dbus.Array([_layout_node(c, depth - 1, variant=True)
                           for c in children], signature="v")
    s = dbus.Struct((rid, d, kids), signature=None)
    return dbus.Struct((rid, d, kids), variant_level=1) if variant else s


def main():
    ap = argparse.ArgumentParser(description="LlamaDSH Launcher 客户端(GTK)")
    ap.add_argument("-p", "--port", type=int, default=DEFAULT_PORT)
    ap.add_argument("--url", help="服务地址(由托盘助手传入)")
    ap.add_argument("--show", action="store_true", help="仅显示窗口(托盘助手调用)")
    args = ap.parse_args()

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
