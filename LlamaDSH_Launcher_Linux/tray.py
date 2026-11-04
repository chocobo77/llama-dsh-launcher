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
import signal
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

try:
    # 托盘的 stdout 平时被重定向进日志文件 → CPython 默认块缓冲,
    # 诊断信息会长时间看不见(本轮排查就被它骗过一次"什么都没打印")
    sys.stdout.reconfigure(line_buffering=True)
except Exception:
    pass

APP_DIR = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, APP_DIR)
import appicon   # noqa: E402  图标自举(纯标准库, 不导入 GTK)


def _api_token():
    """main.py 启动时写入的 0600 token 文件; POST 需带 X-Auth。"""
    try:
        with open(os.path.expanduser("~/.dsh_launcher_token"), encoding="utf-8") as f:
            return f.read().strip()
    except OSError:
        return ""


class TrayIcon:
    """org.kde.StatusNotifierItem + com.canonical.dbusmenu 最小实现。

    GNOME(ubuntu-appindicator 扩展)即认这套协议; 左键 Activate 显示主窗口,
    右键弹出菜单: 显示主窗口 / 轻量化模式(复选) / 退出客户端。
    """

    MENU_SHOW, MENU_LIGHT, MENU_QUIT, MENU_SEP = 1, 2, 3, 4
    # "退出"的二次确认三选(确认框按标签回传, 所以标签即协议值, 见 traydialog.py)
    BTN_SERVICES, BTN_CLIENT, BTN_CANCEL = "关闭服务并退出", "退出", "取消"

    def __init__(self, base_url, on_show, on_quit):
        self.base_url = base_url
        self.on_show = on_show
        self.on_quit = on_quit
        self.lightweight = False
        self.revision = 1
        self.xdg_token = ""
        self._exit_busy_until = 0.0   # 确认框在弹/退出流程进行中(防重复弹)的时间闩
        self.bus = dbus.SessionBus()
        # 规范: org.kde.StatusNotifierItem-<pid>(不能把带 . : 的 URL 拼进总线名)
        self.name = "org.kde.StatusNotifierItem-%d" % os.getpid()
        self.menu_path = "/MenuBar"
        self.item = _SniObject(self.bus, "/StatusNotifierItem", self)
        self.menu = _DBusMenuObject(self.bus, self.menu_path, self)
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
        # IconThemePath 必须指向**图标主题布局**的目录(hicolor/NxN/apps/<IconName>.png)。
        # 以前直接给扁平 assets 目录 → 扩展按名查找必然失败, 加上 IconPixmap 为空,
        # 结果就是"托盘里根本没有启动器图标"。自举见 appicon.py。
        return appicon.theme_path()

    def pixmaps(self):
        return appicon.icon_pixmaps()

    # dbusmenu 布局: (id, props, children)
    def _layout(self):
        return (0, {"children-display": "submenu"}, [
            (self.MENU_SHOW, {"label": "显示主窗口", "icon-name": "view-restored"}, []),
            (self.MENU_LIGHT, {"label": "轻量化模式(后台低频保活)",
                               "toggle-type": "checkmark",
                               "toggle-state": 1 if self.lightweight else 0}, []),
            (self.MENU_SEP, {"type": "separator"}, []),  # id 0 保留给根, 分隔线需独立 id
            # 真正的退出方式在二次确认里选(关闭服务并退出 / 退出 / 取消),
            # 菜单这一行本身不再隐含"只退托盘"的语义
            (self.MENU_QUIT, {"label": "退出"}, []),
        ])

    def _fire(self, item_id):
        from gi.repository import GLib
        fn = {self.MENU_SHOW: self.on_show,
              self.MENU_LIGHT: self.toggle_light,
              # 不是直接退出: 先二次确认(见 ask_exit), 否则一键就灭掉托盘没有反悔机会
              self.MENU_QUIT: self.ask_exit}.get(item_id)
        if fn:
            GLib.idle_add(fn)

    def dbg(self, what, *args):
        """宿主(桌面)对托盘的每一次调用留痕(默认关闭, DSH_TRAY_DEBUG=1 打开)。

        "右键无菜单/左键无反应"这类问题的答案只存在于宿主侧: 是宿主没调用、
        调用了但参数不同、还是我们应答失败 —— 有留痕一眼可判(本轮的 Menu 属性
        类型错误就是靠"宿主一个方法都没调用"这条反证定位的)。"""
        if not os.environ.get("DSH_TRAY_DEBUG"):
            return
        print("[tray.dbus] %s %s" % (what, " ".join(str(a)[:70] for a in args)), flush=True)

    def toggle_light(self):
        on = not self.lightweight
        def post():
            try:
                req = urllib.request.Request(self.base_url + "/api/lightweight",
                    data=json.dumps({"on": on}).encode(),
                    headers={"Content-Type": "application/json", "X-Auth": _api_token()})
                urllib.request.urlopen(req, timeout=5)
                # 回主线程更新菜单复选态并推送 LayoutUpdated
                from gi.repository import GLib
                GLib.idle_add(self.set_lightweight, on)
                print("[tray] 轻量化模式: %s" % ("开" if on else "关"))
            except Exception as ex:
                print("[tray] 轻量化切换失败: %s" % ex)
        threading.Thread(target=post, daemon=True).start()

    # ---- 退出(菜单"退出" → 二次确认三选) ----
    def ask_exit(self):
        """点"退出"先弹确认框(traydialog.py 一次性子进程), 选完才动手。

        确认框不能在 D-Bus 主线程里等: 那会卡死主循环, 托盘图标当场失联,
        宿主还会以为我们死了。所以子进程放后台线程, 结果用 idle_add 回主线程。
        """
        now = time.time()
        if now < self._exit_busy_until:
            print("[tray] 退出确认已在进行中, 忽略重复点击")
            return
        # 窗口开着就一直占住, 直到 _do_exit 里判定"取消"才放开(见 _do_exit),
        # 免得用户在等待期间再点一次, 弹第二个框/重复停服。
        # 上界要盖住 worker 的两段超时: --check 30s + 确认框 300s + 回投余量。
        self._exit_busy_until = now + 400

        def worker():
            rc, out, err = 2, "", ""
            stage = "check"
            try:
                dlg = os.path.join(APP_DIR, "traydialog.py")
                # 先 --check: 无桌面/GTK3 缺失时 GTK 自己 exit(1), 会被当成"用户取消"
                # 而静默无动作 —— 那就又像"点了没反应"。先问一句, 把原因留在日志里。
                chk = subprocess.run([sys.executable, dlg, "--check"],
                                     capture_output=True, text=True, timeout=30)
                if chk.returncode != 0:
                    err = (chk.stderr or chk.stdout or "").strip() or "桌面不可用"
                    rc = 2
                else:
                    stage = "dialog"
                    p = subprocess.run(
                        [sys.executable, dlg,
                         "退出 LlamaDSH 启动器",
                         "“退出”只关掉本客户端(托盘助手与主窗口),"
                         " llama.cpp 与 DSH 继续运行;\n"
                         "“关闭服务并退出”先停止 llama.cpp 与 DSH, 再退出客户端与本地服务。",
                         self.BTN_SERVICES, self.BTN_CLIENT, self.BTN_CANCEL],
                        capture_output=True, text=True, timeout=300)
                    rc, out, err = (p.returncode, (p.stdout or "").strip(),
                                    (p.stderr or "").strip())
            except subprocess.TimeoutExpired:
                rc, err = 4, ("桌面探测(--check)超过 30s 未返回" if stage == "check"
                              else "确认框超过 300s 没有应答")
            except Exception as ex:
                rc, err = 3, repr(ex)
            try:
                from gi.repository import GLib
                GLib.idle_add(self._do_exit, out, rc, err)
            except Exception as ex:
                # 回主线程失败也必须放开闩, 否则"退出"这一行从此永远点不动
                print("[tray] 退出结果回投失败: %s" % ex)
                self._exit_busy_until = 0.0

        threading.Thread(target=worker, daemon=True).start()

    def _do_exit(self, choice, rc, err):
        if choice == self.BTN_SERVICES:
            print("[tray] %s: 停止 llama.cpp 与 DSH…" % choice)
            threading.Thread(target=self._stop_all_then_quit, daemon=True).start()
        elif choice == self.BTN_CLIENT:
            print("[tray] %s: 仅退出客户端(llama.cpp/DSH/服务不动)" % choice)
            self._exit_busy_until = 0.0
            self._close_window()
            self.on_quit()
        else:
            self._exit_busy_until = 0.0     # 取消/弹框失败 → 允许再点一次
            if rc == 2:
                print("[tray] 退出确认框无法显示: %s" % (err or "未知原因"))
            elif rc == 3:
                print("[tray] 退出确认框进程异常: %s" % (err or "未知原因"))
            elif rc == 4:
                print("[tray] %s, 按取消处理" % err)
        return False                        # idle_add 只跑一次

    def _stop_all_then_quit(self):
        """"关闭服务并退出": POST /api/shutdown(服务端停 llama+DSH 后自尽),
        确认服务真的没了再收托盘 —— 否则用户会看到"托盘没了但 llama 还在跑"。

        停服前先把 llama.cpp/DSH 的 PID 抄下来: 服务一死就再也问不到, 只能按 PID
        复核它们是否真的停了(以 root 启动的服务我们停不掉, 必须把残留说清楚)。
        """
        svc_pids = self._service_pids()
        try:
            req = urllib.request.Request(
                self.base_url + "/api/shutdown", data=b"{}",
                headers={"Content-Type": "application/json", "X-Auth": _api_token()})
            urllib.request.urlopen(req, timeout=15)
        except Exception as ex:
            print("[tray] 关闭服务请求未送达: %s" % ex)
        deadline = time.time() + 45         # 停止 llama/DSH 本身可能要几秒
        while time.time() < deadline and self._server_alive():
            time.sleep(0.5)
        if self._server_alive():
            # 兜底: 服务没自尽(老服务无 /api/shutdown 等), 按 /proc + 锁文件找出来终止
            targets = _find_py_pids("main.py")
            lp = _lock_pid("main.py")
            if lp and lp not in targets:
                targets.append(lp)          # 异目录/非 .py 入口时 /proc 扫不到, 锁文件能兜住
            killed, denied = [], []
            for pid in targets:
                try:
                    os.kill(pid, signal.SIGTERM)
                    killed.append(pid)
                except PermissionError:
                    denied.append(pid)      # root 属主的服务普通权限杀不掉, 必须说出来
                except OSError:
                    pass
            msg = "[tray] 服务未自行退出, 已发 SIGTERM: %s" % (killed or "未找到进程")
            if denied:
                msg += "; ⚠ 这些进程属主不是本人(可能 sudo 启动), 普通权限杀不掉: %s" % denied
            print(msg)
            for _ in range(20):             # 新 main.py 收到 SIGTERM 也会自行收尾
                if not self._server_alive():
                    break
                time.sleep(0.5)
        left = [p for p in svc_pids if os.path.isdir("/proc/%d" % p)]
        if left:
            print("[tray] ⚠ llama.cpp/DSH 未能停止, 残留进程 PID: %s"
                  " (若以 root 启动需手动关闭)" % ", ".join(str(p) for p in left))
        elif svc_pids:
            print("[tray] llama.cpp 与 DSH 已停止")
        else:
            # 停服前一个 PID 都没拿到(服务没回 /api/status, 或 llama 只按端口存在而
            # status 里 pid 为 null): 这是"不知道", 不是"已停止" —— 不能替用户下结论
            print("[tray] 未能确认 llama.cpp/DSH 是否已停止(停服前服务未回 PID),"
                  " 请在 Web 界面确认一眼")
        self._close_window()
        try:
            from gi.repository import GLib
            GLib.idle_add(self.on_quit)
        except Exception as ex:
            print("[tray] 退出主循环投递失败: %s" % ex)

    def _service_pids(self):
        """当前在跑的 llama.cpp / DSH 进程 PID(退出后复核用)。"""
        try:
            with urllib.request.urlopen(self.base_url + "/api/status", timeout=3) as r:
                st = json.loads(r.read().decode("utf-8", "replace"))
            return [p for p in ((st.get("llama") or {}).get("pid"),
                                (st.get("dsh") or {}).get("pid")) if p]
        except Exception:
            return []

    def _server_alive(self):
        """服务是否还活着: HTTP 通 **或** 服务进程还在。

        只靠 HTTP 会假阴性 —— 服务在 stop() 里忙到连接超时/被拒时, 托盘会以为它已经
        没了, 于是跳过兜底, 结果就是"托盘消失了但 llama 还在跑"。
        """
        try:
            with urllib.request.urlopen(self.base_url + "/api/status", timeout=1.5) as r:
                json.loads(r.read().decode("utf-8", "replace"))
            return True
        except Exception:
            pass
        if _lock_pid("main.py"):
            return True                     # 服务自己写的锁文件里还记着活 PID
        return bool(_find_py_pids("main.py"))

    def _close_window(self):
        """关掉主窗口进程(若开着): 走它自己的 SIGTERM 收尾路径。"""
        pids = _find_py_pids("client.py")
        lp = _lock_pid("client.py")
        if lp and lp not in pids:
            pids.append(lp)
        for pid in pids:
            try:
                os.kill(pid, signal.SIGTERM)
            except OSError:
                pass


if _DBUS_OK:

    class _SniObject(dbus.service.Object):
        """org.kde.StatusNotifierItem + 手动实现的 Properties 接口。"""

        def __init__(self, bus, path, tray):
            self.tray = tray
            super().__init__(bus, path)

        # 签名严格按 KDE SNI 规范: GNOME 的 appindicator 扩展是 **带坐标** 调
        # Activate(x, y)/SecondaryActivate(x, y)。声明成无参 → 宿主调用直接
        # TypeError(journal: "failed to activate"), 左键/双击永远打不开窗口。
        @dbus.service.method("org.kde.StatusNotifierItem", in_signature="ii", out_signature="")
        def Activate(self, x, y):
            self.tray.dbg("Activate", "x=%s y=%s" % (x, y))
            self.tray.on_show()

        @dbus.service.method("org.kde.StatusNotifierItem", in_signature="ii", out_signature="")
        def SecondaryActivate(self, x, y):
            self.tray.dbg("SecondaryActivate", "x=%s y=%s" % (x, y))
            self.tray.on_show()

        @dbus.service.method("org.kde.StatusNotifierItem", in_signature="ii", out_signature="")
        def ContextMenu(self, x, y):
            self.tray.dbg("ContextMenu", "x=%s y=%s" % (x, y))
            # 菜单由 ItemIsMenu + /MenuBar 承载; 宿主若走这条路说明它没建 dbusmenu 客户端

        # Wayland 激活令牌: 宿主(扩展 appIndicator.js:774)会在 Activate 前递过来;
        # 不实现则扩展记 hasProvideXdgActivationToken=false 并跳过(不报错, 但窗口焦点策略会退化)
        @dbus.service.method("org.kde.StatusNotifierItem", in_signature="s", out_signature="")
        def ProvideXdgActivationToken(self, token):
            self.tray.xdg_token = str(token or "")

        @dbus.service.method("org.kde.StatusNotifierItem", in_signature="u", out_signature="")
        def XAyatanaSecondaryActivate(self, timestamp):
            self.tray.on_show()

        # 滚轮: 缺声明会让宿主每次滚动都收到 UnknownMethod(刷日志), 语义上我们没有快捷动作
        @dbus.service.method("org.kde.StatusNotifierItem", in_signature="is", out_signature="")
        def Scroll(self, delta, orientation):
            pass

        @dbus.service.method("org.kde.StatusNotifierItem", in_signature="ii", out_signature="")
        def ScrollValue(self, x_value, y_value):
            pass

        @dbus.service.method("org.kde.StatusNotifierItem", in_signature="", out_signature="s")
        def Title(self):
            return "LlamaDSH 启动器"

        @dbus.service.method("org.freedesktop.DBus.Properties", in_signature="ss", out_signature="v")
        def Get(self, iface, prop):
            vals = self.GetAll(iface)
            if prop not in vals:
                raise dbus.exceptions.DBusException(
                    "No such property: %s" % prop,
                    name="org.freedesktop.DBus.Error.UnknownProperty")
            return vals[prop]   # 缺属性时返回 None 会撞 'v' 签名类型错误

        @dbus.service.method("org.freedesktop.DBus.Properties", in_signature="s", out_signature="a{sv}")
        def GetAll(self, iface):
            return {
                "Category": "ApplicationStatus",
                "Id": "llamadsh-launcher",
                "Title": "LlamaDSH 启动器",
                "Status": "Active",
                "IconName": appicon.ICON_NAME,
                "IconThemePath": self.tray.icon_dir(),
                # 自带位图兜底: 名称查找失败(主题/权限/桌面差异)时仍一定有图标
                "IconPixmap": _pixmap_array(self.tray.pixmaps()),
                "OverlayIconName": "",
                "AttentionIconName": "",
                "ItemIsMenu": True,
                # Menu 在规范里是 **object path(o)**, 不是字符串: GDBus 按 StatusNotifierItem.xml
                # 校验属性类型, s 与声明的 o 不符就把整个属性丢掉(journal: "Received property Menu
                # with type s does not match expected type o"), 扩展的 this._proxy.Menu 变 null
                # → menuPath 为空 → 宿主从不创建 dbusmenu 客户端 → 右键只有空菜单
                "Menu": dbus.ObjectPath(self.tray.menu_path),
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
        """com.canonical.dbusmenu 最小实现(静态三项目录)。

        入参签名必须按规范: GetLayout(i parent, i depth, **as** props)、
        GetGroupProperties(**ai** ids, as props)。原来写成 iia{ss} / ias,
        扩展按规范发 (i,i,as) / (ai,as) 时类型不符被 python-dbus 拒掉,
        布局与属性永远取不到 → 右键弹出的是空菜单(即"无交互菜单")。
        """

        def __init__(self, bus, path, tray):
            self.tray = tray
            super().__init__(bus, path)

        @dbus.service.method("com.canonical.dbusmenu", in_signature="iias",
                             out_signature="u(ia{sv}av)")
        def GetLayout(self, parent, depth, props):
            self.tray.dbg("GetLayout", "parent=%s" % parent, "depth=%s" % depth,
                          "props=%s" % list(props))
            root = self.tray._layout()
            node = _find_node(root, int(parent)) or root
            # depth: -1 = 全展开, 0 = 只要本节点不带子项
            return (self.tray.revision, _layout_node(node, int(depth)))

        @dbus.service.method("com.canonical.dbusmenu", in_signature="i", out_signature="b")
        def AboutToShow(self, _id):
            self.tray.dbg("AboutToShow", "id=%s" % _id)
            return True  # 让客户端每次弹出前重取布局(轻量化复选状态会变)

        # 规范: LayoutUpdated(**u** revision, **i** parent) —— parent 是 int32 不是 uint32
        @dbus.service.signal("com.canonical.dbusmenu", signature="ui")
        def LayoutUpdated(self, revision, parent):
            pass

        # 规范: GetGroupProperties(ai ids, as propertyNames) → **a(ia{sv})**(结构体数组)
        # 扩展按 (ai, as) 调用并把回包当 a(ia{sv}) 解 —— 签名/回包形状任一处不合,
        # 菜单项的 label/toggle 状态就全取不到(表现为右键弹出空菜单)。
        @dbus.service.method("com.canonical.dbusmenu", in_signature="aias",
                             out_signature="a(ia{sv})")
        def GetGroupProperties(self, ids, props):
            self.tray.dbg("GetGroupProperties", "ids=%s" % list(ids),
                          "props=%s" % list(props))
            flat = _flatten_layout(self.tray._layout())
            rows = []
            for raw in ids:
                i = int(raw)
                if i not in flat:
                    continue
                sel = {k: v for k, v in flat[i].items() if not props or k in props}
                rows.append(dbus.Struct((dbus.Int32(i),
                                         dbus.Dictionary(sel, signature="sv")),
                                        signature=None))
            return dbus.Array(rows, signature="(ia{sv})")

        # 对象级属性: 宿主(GNOME 扩展)用自带 DBusMenu.xml 建 GDBus 代理并按声明类型校验
        # 缓存属性 —— 类型不符(比如把 as 写成 s)会让代理报 "property is invalid"。
        # XML 声明: Version(u) TextDirection(s) Status(s) IconThemePath(**as**)
        @dbus.service.method("org.freedesktop.DBus.Properties", in_signature="ss", out_signature="v")
        def Get(self, iface, prop):
            vals = self.GetAll(iface)
            if prop not in vals:
                raise dbus.exceptions.DBusException(
                    "No such property: %s" % prop,
                    name="org.freedesktop.DBus.Error.UnknownProperty")
            return vals[prop]

        @dbus.service.method("org.freedesktop.DBus.Properties", in_signature="s", out_signature="a{sv}")
        def GetAll(self, iface):
            return {
                "Version": dbus.UInt32(3),
                "TextDirection": "none",
                "Status": "normal",
                "IconThemePath": dbus.Array([], signature="s"),
            }

        @dbus.service.method("org.freedesktop.DBus.Properties", in_signature="ssv", out_signature="")
        def Set(self, iface, prop, value):
            pass

        @dbus.service.method("com.canonical.dbusmenu", in_signature="isvu", out_signature="")
        def Event(self, _id, event, _data, _ts):
            self.tray.dbg("Event", "id=%s" % _id, "event=%s" % event)
            if event == "clicked":
                try:
                    self.tray._fire(int(_id))
                except Exception:
                    pass




def _pixmap_array(items):
    """SNI IconPixmap: a(iiay), 像素是 ARGB32 网络字节序。"""
    import dbus
    return dbus.Array([dbus.Struct((dbus.Int32(w), dbus.Int32(h),
                                    dbus.Array(argb, signature="y")),
                                   signature=None)
                       for (w, h, argb) in items], signature="(iiay)")


def _find_node(node, target_id):
    """按 id 定位子树(宿主可能只请求某个 submenu 的布局)。"""
    rid, _props, children = node
    if rid == target_id:
        return node
    for c in children:
        got = _find_node(c, target_id)
        if got:
            return got
    return None


def _flatten_layout(node, out=None):
    """布局展平成 {id: props}, 供 GetGroupProperties 应答。"""
    if out is None:
        out = {}
    rid, props, children = node
    out[rid] = props
    for c in children:
        _flatten_layout(c, out)
    return out


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





def _lock_pid(script):
    """服务/窗口自己写的 fcntl 锁文件里记的 PID —— 判定"我们这一份实例"是否在跑。

    按脚本路径扫 /proc 会漏: 非 .py 入口、`python3 -m`、装在别的目录的同一份、
    cwd 已被删除的进程, 以及 root 进程读不到 cwd 的情况。锁文件里的 PID 是进程自己
    写的, 不依赖这些; 这里再校验一次 cmdline 里有同名脚本, 防 PID 被复用。
    """
    leaf = ".llamadsh_launcher_linux.lock" if script == "main.py" \
        else ".llamadsh_launcher_window.lock"
    try:
        pid = int((open(os.path.join(os.path.expanduser("~"), leaf)).read() or "0").strip())
    except (OSError, ValueError):
        return 0
    if pid <= 0 or not os.path.isdir("/proc/%d" % pid):
        return 0
    try:
        with open("/proc/%d/cmdline" % pid, "rb") as fh:
            args = fh.read().split(b"\0")
    except OSError:
        return 0
    if not any(os.path.basename(a.decode("utf-8", "ignore")) == script for a in args):
        return 0
    return pid


def _find_py_pids(script):
    """按 /proc 找在跑的 **本目录** <script> 进程(收窗口进程 / 兜底终止服务)。

    不用 pgrep -f: 它的正则会被包裹 shell 自己的命令行命中(踩过一次: 把自己杀掉
    且没有任何输出), 这里校验 comm 是 python, bash 壳必然排除。
    按 realpath 比对而不只看文件名: 只看 basename 会把用户自己从别处跑的 main.py
    也算进来, 兜底 SIGTERM 就可能杀掉无关实例。
    """
    target = os.path.realpath(os.path.join(APP_DIR, script))
    me = os.getpid()
    pids = []
    for entry in os.listdir("/proc"):
        if not entry.isdigit() or int(entry) == me:
            continue
        try:
            with open("/proc/%s/comm" % entry, encoding="utf-8", errors="replace") as fh:
                comm = fh.read().strip()
            if not comm.startswith("python"):
                continue
            with open("/proc/%s/cmdline" % entry, "rb") as fh:
                args = [a.decode("utf-8", "ignore") for a in fh.read().split(b"\0")]
        except OSError:
            continue
        try:  # 相对路径要靠 cwd 还原; root 进程的 cwd 读不到 → 只认绝对路径
            cwd = os.readlink("/proc/%s/cwd" % entry)
        except OSError:
            cwd = ""
        hit = False
        for arg in args[1:]:
            if not arg.endswith(".py"):
                continue
            path = arg if os.path.isabs(arg) else (os.path.join(cwd, arg) if cwd else "")
            if path and os.path.realpath(path) == target:
                hit = True
                break
        if hit:
            pids.append(int(entry))
    return pids


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
    # 必须在创建任何总线连接之前挂 GLib 主循环: 默认总线会被缓存,
    # _session_bus_ready() 的预检先建连接的话, 后面导出 SNI 对象会直接抛
    # "D-Bus connections must be attached to a main loop"
    DBusGMainLoop(set_as_default=True)
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

    # 图标先自举: SNI 一旦导出, 主题查找失败不会重试(要重注册才有图标)
    if not appicon.install_user_icons():
        print("[tray] 图标主题自举失败(仅用自带位图显示托盘图标)")
    from gi.repository import GLib
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
                         headers={"Content-Type": "application/json", "X-Auth": _api_token()})
        _u.urlopen(req, timeout=5)
        print("[tray] 轻量化模式: 开(后台低频保活)")
    except Exception:
        pass
    print("[tray] 托盘就绪: 左键显示主窗口, 右键菜单 | 进程常驻 ~25MB", flush=True)
    loop.run()


if __name__ == "__main__":
    main()
