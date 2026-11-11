#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""POSIX 用户信息的极小工具(纯标准库, 不碰 GTK/dbus, 供 main / client / tray 共用)。

存在的理由是一件事: **家目录必须查 passwd, 不能拼 "/home/<用户名>"**。
不是 /home/x 的用户很常见(LDAP/AD 接入、自定义家目录、root 自己)。拼出来的路径不存在时,
"修正过的 HOME" 比不修正更糟: 配置、日志、DSH_HOME、~/.cache 全指向一个不存在的目录,
现场看起来像"程序没配置", 排查的人只会去查配置逻辑。
"""

import os


def user_home(user):
    """返回 user 的家目录; 查不到(或目录不存在)返回 None —— 由调用方决定退回什么,
    绝不返回一个不存在的路径让调用方误用。"""
    if not user:
        return None
    try:
        import pwd
        pw = pwd.getpwnam(user)
        home = pw.pw_dir or None
        if home and os.path.isdir(home):
            return home
    except Exception:
        pass
    # 没有 pwd(非 POSIX)或用户不在 passwd 里: 只认确实存在的常见位置
    guess = "/home/" + user
    return guess if os.path.isdir(guess) else None


def sudo_initiator():
    """发起 sudo 的桌面用户名; 不是 sudo 场景返回 None。"""
    return os.environ.get("SUDO_USER") or None
