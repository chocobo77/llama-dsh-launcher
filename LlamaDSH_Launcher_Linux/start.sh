#!/usr/bin/env bash
# 启动 LlamaDSH Launcher: 后台服务 + 托盘助手(QQ/微信式常驻)。
# 托盘左键/菜单"显示主窗口"按需拉起图形窗口(GTK+WebKit 渲染同一套网页 UI);
# 无 GTK 时降级为浏览器模式。(请勿用 sudo——HOME 会错位)
cd "$(dirname "$0")"

if python3 -c "import dbus, dbus.mainloop.glib" 2>/dev/null; then
    python3 tray.py "$@"
    rc=$?
    if [ "$rc" -ne 2 ]; then
        exit "$rc"   # 2=会话总线不可达(SSH 等), 其余退出码直接透传
    fi
    echo "托盘不可用(无图形会话/未装托盘扩展), 降级为浏览器模式"
fi

echo "未检测到 python3-dbus, 降级为浏览器模式(sudo apt install python3-dbus 可启用托盘)"
python3 main.py "$@" &
SERVER_PID=$!
sleep 2
URL="http://127.0.0.1:8788"
echo "服务已在后台运行(PID $SERVER_PID): $URL"
xdg-open "$URL" 2>/dev/null
wait $SERVER_PID
