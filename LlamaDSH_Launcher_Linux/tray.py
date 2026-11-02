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

    MENU_SHOW, MENU_LIGHT, MENU_QUIT, MENU_SEP = 1, 2, 3, 4

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
            items = watcher.Get("org.kde.StatusNotifierWatcher",
                                "RegisteredStatusNotifierItems",
                                dbus_interface="org.freedesktop.DBus.Properties")
            if self.name not in items:  # 无条件重注册会让图标周期性重建/闪烁
                watcher.RegisterStatusNotifierItem(self.name,
                    dbus_interface="org.kde.StatusNotifierWatcher")
        except Exception:
            pass
        return True  # 保持 timer

    def set_lightweight(self, on):
        self.lightweight = bool(on)
        self.revision += 1
        # 推送布局更新, 否则客户端复选框永远停在首帧值
        try:
            self.menu.LayoutUpdated(self.revision, 0)
        except Exception:
            pass

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
            (self.MENU_SEP, {"type": "separator"}, []),  # id 0 保留给根, 分隔线需独立 id
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
        def post():
            try:
                req = urllib.request.Request(self.base_url + "/api/lightweight",
                    data=json.dumps({"on": on}).encode(),
                    headers={"Content-Type": "application/json"})
                urllib.request.urlopen(req, timeout=5)
                # 回主线程更新菜单复选态并推送 LayoutUpdated
                from gi.repository import GLib
                GLib.idle_add(self.set_lightweight, on)
                print("[tray] 轻量化模式: %s" % ("开" if on else "关"))
            except Exception as ex:
                print("[tray] 轻量化切换失败: %s" % ex)
        threading.Thread(target=post, daemon=True).start()


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
            return True  # 让客户端每次弹出前重取布局(轻量化复选状态会变)

        @dbus.service.signal("com.canonical.dbusmenu", signature="uu")
        def LayoutUpdated(self, revision, parent):
            pass

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
    # 120 次的预算是"连接拒绝近乎零耗时 + 0.5s sleep"——实际 ~60s,
    # 冷启动(fuseblk 盘 + 大 yml)import 也可能超 20s, 预算必须宽裕
    for _ in range(120):
        if probe(port):
            return base
        time.sleep(0.5)
    for i in range(1, 20):
        if probe(port + i):
            return "http://127.0.0.1:%d" % (port + i)
    return None


def _session_bus_ready():
    """SSH 会话无 DBUS_SESSION_BUS_ADDRESS、或 GNOME 未装 appindicator 扩展
    (无 StatusNotifierWatcher)时, 托盘不可用 —— 返回 False 让 start.sh 降级浏览器。"""
    if not os.environ.get("DBUS_SESSION_BUS_ADDRESS"):
        return False
    try:
        bus = dbus.SessionBus()
        return bool(bus.name_has_owner("org.kde.StatusNotifierWatcher"))
    except Exception:
        return False


def main():
    if not _DBUS_OK:
        print("python3-dbus 不可用, 托盘助手无法运行(apt install python3-dbus)")
        sys.exit(1)
    if not _session_bus_ready():
        print("会话总线/StatusNotifierWatcher 不可达(SSH 会话或未装托盘扩展), 托盘不可用")
        sys.exit(2)  # 约定: start.sh 捕获 2 → 降级浏览器模式
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

    loop = GLib.MainLoop()
    tray = TrayIcon(base,
                    on_show=lambda: _spawn_window(base),
                    on_quit=loop.quit)
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
    print("[tray] 托盘就绪: 左键显示主窗口, 右键菜单 | 进程常驻 ~25MB", flush=True)
    loop.run()


if __name__ == "__main__":
    main()
