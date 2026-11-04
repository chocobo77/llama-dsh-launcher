#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""托盘菜单的二次确认对话框(一次性子进程)。

为什么单独一个进程: 托盘助手刻意不导入 GTK(常驻只有 ~25MB, 导入 GTK/WebKit 会翻好几倍),
所以确认框由短命子进程弹, 选完就退出。

用法:   python3 traydialog.py <标题> <正文> <按钮1> [<按钮2> ...]
约定:   选中的按钮**标签原文**写到 stdout, 退出码 0;
        取消/关闭窗口 → 退出码 1(stdout 为空);
        GTK3 不可用或无显示环境 → 退出码 2(调用方据此提示, 而不是"点了没反应")。
自检:   --check 只验证 GTK3 + 显示可用(不弹窗);
        --respond <标签> [--delay 秒] 定时以该按钮应答(回归用, 无需人点)。
弹窗成功会在 stderr 打一行 "[traydialog] 已弹出" —— 没有输入注入工具时,
这是"框确实显示在桌面上"的唯一可取证痕迹。
"""
import sys


def main():
    argv = sys.argv[1:]
    check, respond, delay = False, None, 0.0
    while argv and argv[0].startswith("--"):
        flag = argv.pop(0)
        if flag == "--check":
            check = True
        elif flag == "--respond" and argv:
            respond = argv.pop(0)
        elif flag == "--delay" and argv:
            delay = float(argv.pop(0))
        else:
            sys.stderr.write("未知参数: %s\n" % flag)
            return 2
    try:
        import gi
        gi.require_version("Gtk", "3.0")
        # Gdk 也必须先钉版本: 否则 GI 会先加载 4.0, 与 Gtk 3.0 的依赖冲突
        # ("Requiring namespace 'Gdk' version '3.0', but '4.0' is already loaded")
        gi.require_version("Gdk", "3.0")
        from gi.repository import Gdk, GLib, Gtk
    except Exception as ex:
        sys.stderr.write("GTK3 不可用: %s\n" % ex)
        return 2
    # 没有显示环境时(纯 SSH、无桌面)必须显式失败,
    # 否则调用方只会看到"菜单点了没反应", 分不清是没弹还是没选
    if Gdk.Display.get_default() is None:
        sys.stderr.write("无显示环境(ssh/无桌面), 无法弹出确认框\n")
        return 2
    if check:
        return 0
    if len(argv) < 3:
        sys.stderr.write("用法: traydialog.py [--check|--respond 标签] <标题> <正文> <按钮...>\n")
        return 2
    title, body, buttons = argv[0], argv[1], argv[2:]

    GLib.set_prgname("llamadsh-launcher")
    try:  # 与应用菜单/托盘同一套图标(自举到 hicolor 主题)
        import appicon
        appicon.install_user_icons()
        Gtk.Window.set_default_icon_name(appicon.ICON_NAME)
    except Exception:
        pass

    dlg = Gtk.MessageDialog(modal=True, message_type=Gtk.MessageType.QUESTION,
                            buttons=Gtk.ButtonsType.NONE, text=title)
    dlg.set_title(title)
    dlg.format_secondary_text(body)
    dlg.set_position(Gtk.WindowPosition.CENTER)
    ids, cancel_id = {}, None
    for i, label in enumerate(buttons):
        rid = 100 + i                      # 不与 Gtk.ResponseType 的取值相撞
        ids[rid] = label
        dlg.add_button(label, rid)
        if label in ("取消", "Cancel"):
            cancel_id = rid
    if cancel_id is None:
        cancel_id = max(ids)
    # 默认响应指向"取消": 回车不会触发破坏性动作; Esc/关窗走 DELETE_EVENT,
    # 它不在 ids 里 → 调用方拿到退出码 1(=未选择)。
    # (set_cancel_response 是 GTK4 才有的, GTK3 上调它会 AttributeError)
    dlg.set_default_response(cancel_id)

    if respond is not None:
        rid = next((k for k, v in ids.items() if v == respond), cancel_id)

        def _auto():
            dlg.response(rid)
            return False          # 一次性
        GLib.timeout_add(max(200, int(delay * 1000)), _auto)
    dlg.show_all()
    print("[traydialog] 已弹出: %s" % title, file=sys.stderr, flush=True)
    resp = dlg.run()
    dlg.destroy()
    label = ids.get(resp)
    if not label:
        return 1
    sys.stdout.write(label)
    sys.stdout.flush()
    return 0


if __name__ == "__main__":
    sys.exit(main())
