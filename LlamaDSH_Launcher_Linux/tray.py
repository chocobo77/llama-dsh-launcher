#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""LlamaDSH 托盘助手 — 微型常驻进程(纯 python-dbus, 不导入 GTK/WebKit)。

QQ/微信式最小化: 主窗口进程收进托盘时整体退出, 后台只剩
  服务进程(~40MB) + 本助手(~25MB);
菜单"显示主窗口"按需拉起 GTK 客户端窗口进程。

状态栏图标经 org.kde.StatusNotifierItem + com.canonical.dbusmenu
(GNOME ubuntu-appindicator 扩展)。
"""
import os
import sys
import time
import json
import argparse
import threading
import subprocess
import urllib.request

try:
    import dbus
    import dbus.service
    from dbus.mainloop.glib import DBusGMainLoop
    _DBUS_OK = True
except Exception:
    dbus = None
    _DBUS_OK = False

APP_DIR = os.path.dirname(os.path.abspath(__file__))

class TrayIcon:
    """org.kde.StatusNotifierItem + com.canonical.dbusmenu 最小实现。

    GNOME(ubuntu-appindicator 扩展)即认这套协议; 左键 Activate 显示主窗口,
    右键弹出菜单: 显示主窗口 / 轻量化模式(复选) / 退出客户端。
    """

    MENU_SHOW, MENU_LIGHT, MENU_QUIT = 1, 2, 3

    def __init__(self, base_url, on_show, on_quit):
        self.base_url = base_url
        self.on_show = on_show
        self.on_quit = on_quit
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

    def icon_dir(self):
        for base in (os.path.dirname(os.path.abspath(__file__)),
                     os.path.expanduser("~/.local/share/llamadsh-launcher")):
            if os.path.isdir(os.path.join(base, "assets")):
                return os.path.join(base, "assets")
        return ""

    def icon_path(self):
        for base in (os.path.dirname(os.path.abspath(__file__)),
                     os.path.expanduser("~/.local/share/llamadsh-launcher")):
            for s in (64, 48, 32):
                p = os.path.join(base, "assets", "icon%d.png" % s)
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
            (self.MENU_QUIT, {"label": "退出托盘助手(窗口/服务/llama/DSH 不受影响)"}, []),
        ])

    def _fire(self, item_id):
        from gi.repository import GLib
        fn = {self.MENU_SHOW: self.on_show,
              self.MENU_LIGHT: self.toggle_light,
              self.MENU_QUIT: self.on_quit}.get(item_id)
        if fn:
            GLib.idle_add(fn)

    def toggle_light(self):
        on = not self.lightweight
        try:
            req = urllib.request.Request(self.base_url + "/api/lightweight",
                data=json.dumps({"on": on}).encode(),
                headers={"Content-Type": "application/json"})
            urllib.request.urlopen(req, timeout=5)
            self.set_lightweight(on)
            print("[tray] 轻量化模式: %s" % ("开" if on else "关"))
        except Exception as ex:
            print("[tray] 轻量化切换失败: %s" % ex)


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
                "IconThemePath": self.tray.icon_dir(),
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





def _spawn_window(base_url):
    """按需拉起 GTK 客户端窗口进程(关闭窗口即退出并置轻量化)。"""
    print("[tray] 拉起主窗口…")
    subprocess.Popen([sys.executable, os.path.join(APP_DIR, "client.py"),
                      "--url", base_url], cwd=APP_DIR,
                     stdin=subprocess.DEVNULL, start_new_session=True)


def ensure_server(port):
    def probe(p):
        try:
            with urllib.request.urlopen("http://127.0.0.1:%d/api/status" % p, timeout=1.5) as r:
                json.loads(r.read().decode("utf-8", "replace"))
            return True
        except Exception:
            return False
    base = "http://127.0.0.1:%d" % port
    if probe(port):
        return base
    env = dict(os.environ)
    if os.geteuid() == 0 and os.environ.get("SUDO_USER"):
        env["HOME"] = "/home/" + os.environ["SUDO_USER"]
    subprocess.Popen([sys.executable, os.path.join(APP_DIR, "main.py"), "-p", str(port)],
                     cwd=APP_DIR, env=env, stdin=subprocess.DEVNULL,
                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                     start_new_session=True)
    for _ in range(40):
        if probe(port):
            return base
        time.sleep(0.5)
    for i in range(1, 20):
        if probe(port + i):
            return "http://127.0.0.1:%d" % (port + i)
    return None


def main():
    if not _DBUS_OK:
        print("python3-dbus 不可用, 托盘助手无法运行(apt install python3-dbus)")
        sys.exit(1)
    ap = argparse.ArgumentParser(description="LlamaDSH 托盘助手(微型常驻)")
    ap.add_argument("-p", "--port", type=int, default=8788)
    ap.add_argument("--minimized", action="store_true", help="自启模式(默认即无窗口, 参数仅为兼容)")
    args = ap.parse_args()

    base = ensure_server(args.port)
    if base is None:
        print("无法启动/连接本地服务")
        sys.exit(1)
    DBusGMainLoop(set_as_default=True)

    from gi.repository import GLib
    GLib.threads_init()

    tray = TrayIcon(base, on_show=lambda: _spawn_window(base),
                    on_quit=lambda: GLib.MainLoop.default_main_loop_quit() if False else os._exit(0))
    tray.start()
    tray.set_lightweight(True)
    try:
        import urllib.request as _u
        req = _u.Request(base + "/api/lightweight",
                         data=json.dumps({"on": True}).encode(),
                         headers={"Content-Type": "application/json"})
        _u.urlopen(req, timeout=5)
        print("[tray] 轻量化模式: 开(后台低频保活)")
    except Exception:
        pass
    print("[tray] 托盘就绪: 左键显示主窗口, 右键菜单 | 进程常驻 ~25MB")
    loop = GLib.MainLoop()
    loop.run()


if __name__ == "__main__":
    main()
