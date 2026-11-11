# -*- coding: utf-8 -*-
r"""以 root 启动时切回发起 sudo 的桌面用户(一次封顶)。

为什么单独成模块、而且必须比其它 import 更早调用：applog / core / main 都在**模块 import
时**按 `os.path.expanduser("~")` 算出日志目录、token 文件、锁文件、/tmp 私有命名空间。
血统切换如果发生在 main() 里，前面那一轮 import 已经用 root 的 HOME 把
`/root/.dsh_launcher_logs` 建出来、写过启动横幅、甚至跑过一次历史日志重写(scrub)
—— 2026-11-11 review 实测到的正是这个顺序问题("修掉 ~ 错位"只修了 exec 之后的那一半)。

因此 main.py 的结构固定为：标准库 → posixuser/safeout/lineage → **reset_if_sudo()** →
再 import applog/core/… 这些会快照 `~` 的模块。这条顺序由 selftest.py §32 守着。

与 C# 端 Services\LineageGuard.cs 共享同一条硬约束：**自重启一次封顶**。C# 那边凭证只能走
标记文件(explorer 重启会丢掉附加参数)，这里 execvp 会带上显式 env，所以用环境变量。
少了封顶，任何"切完还是 root"的情形(SUDO_USER=root、sudoers 策略、家目录解析失败后仍 exec)
都会变成无限自我重启 —— 与 2026-11-10 Windows 端「装完无限反复启动」是同一类缺陷。
"""
import os
import sys

import posixuser
import safeout

safeout.fix_streams()

# sudo 血统标记: 切过一次就以这个环境标记为准, 不再自我重启(一次封顶)
LINEAGE_RESET_ENV = "LlamaDSH_LINEAGE_RESET"
# 显式关闭自检重启用: 测试里 import main 时不该真去 exec
DISABLE_ENV = "LlamaDSH_NO_LINEAGE_RESET"


def reset_if_sudo() -> bool:
    """以 root 跑时切回发起 sudo 的桌面用户。返回 True = 已 exec 出去，调用方直接退出。"""
    if os.environ.get(DISABLE_ENV):
        return False
    if os.geteuid() != 0:
        return False
    if os.environ.get(LINEAGE_RESET_ENV):
        print("⚠ 血统已切过一次但仍是 root(%s=1): 不再自我重启, 否则会无限循环" % LINEAGE_RESET_ENV)
        return False
    user = posixuser.sudo_initiator()
    if not user:
        print("⚠ 正在以 root 运行:配置/检索/日志都会指向 /root,建议改用桌面用户启动")
        return False
    home = posixuser.user_home(user)
    if not home:
        # 查不到家目录就不 exec: 带着一个不存在的 HOME 重启, 比保持 root 更难排查
        print("⚠ 以 root 运行, 且查不到用户 %s 的家目录: 保持当前身份继续(配置/日志会指向 /root)" % user)
        return False
    argv = [sys.executable, os.path.abspath(sys.argv[0])] + sys.argv[1:]
    print("检测到以 sudo(root)启动——所有 ~ 路径会错位,自动切换为用户 %s 重新启动…" % user)
    try:
        os.execvp("sudo", ["sudo", "-u", user,
                           "env", "HOME=" + home, "USER=" + user,
                           "LANG=" + os.environ.get("LANG", "C.UTF-8"),
                           LINEAGE_RESET_ENV + "=1"] + argv)
    except OSError as ex:
        # exec 失败(没有 sudo / 被 sudoers 拒 / 用户不存在)不能让启动器凭空消失
        print("⚠ 切换到用户 %s 失败(%s): 保持当前身份继续" % (user, ex))
        return False
    return True
