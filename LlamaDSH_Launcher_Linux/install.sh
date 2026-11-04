#!/usr/bin/env bash
# LlamaDSH Launcher(Linux)安装器: 装到用户目录, 注册应用菜单/托盘图标, 免 root。
set -e
DEST="$HOME/.local/share/llamadsh-launcher"
APP_DIR="$(cd "$(dirname "$0")" && pwd)"
DESKTOP="$HOME/.local/share/applications/llamadsh-launcher.desktop"
ICON_DIR="$HOME/.local/share/icons/hicolor"

echo "=== LlamaDSH Launcher(Linux)安装 ==="

# 依赖体检(全部缺失才致命)
ok=1
python3 -c "import yaml" 2>/dev/null || { echo "✗ 缺 python3-pyyaml: sudo apt install python3-pip && python3 -m pip install pyyaml(或 sudo apt install python3-yaml)"; ok=0; }
python3 -c "import gi; gi.require_version('Gtk','3.0')" 2>/dev/null || echo "⚠ 无 GTK3(python3-gi): 客户端窗口不可用, 将降级为浏览器模式(sudo apt install python3-gi gir1.2-webkit2-4.1 可启用客户端)"
python3 -c "import dbus" 2>/dev/null || echo "⚠ 无 python3-dbus: 托盘不可用(sudo apt install python3-dbus 可启用)"
[ "$ok" = "1" ] || exit 1

# 文件落位(拷贝失败必须中止: set -e 下不得吞, 否则装出半套文件树还报成功)
mkdir -p "$DEST"
cp -r "$APP_DIR"/*.py "$APP_DIR"/webui "$APP_DIR"/llama_params.yml "$APP_DIR"/assets "$APP_DIR"/start.sh "$DEST"/
# 图标进 hicolor(应用菜单/托盘 IconName 查找)
for s in 16 32 48 64 128 256; do
  [ -f "$APP_DIR/assets/icon$s.png" ] && mkdir -p "$ICON_DIR/${s}x${s}/apps" && \
    cp "$APP_DIR/assets/icon$s.png" "$ICON_DIR/${s}x${s}/apps/llamadsh-launcher.png"
done
# hicolor 缺 index.theme 会被 Gtk/GNOME Shell 整目录忽略(图标文件铺好也查不到)——补齐
[ -f "$ICON_DIR/index.theme" ] || cp /usr/share/icons/hicolor/index.theme "$ICON_DIR/index.theme" 2>/dev/null || true

# .desktop(客户端; WebKit 缺失时 start.sh 自动降级为浏览器)
cat > "$DESKTOP" <<DESK
[Desktop Entry]
Type=Application
Name=LlamaDSH Launcher
Comment=DeepSeek Harness & Llama 启动器(客户端 + 后台服务)
Exec=$DEST/start.sh
Icon=llamadsh-launcher
Terminal=false
Categories=Development;
StartupWMClass=llamadsh-launcher
StartupNotify=true
DESK
chmod +x "$DEST/start.sh"

# 卸载器
cat > "$DEST/uninstall.sh" <<UNIN
#!/usr/bin/env bash
rm -rf "$DEST" "$DESKTOP" "$HOME/.config/autostart/llamadsh-launcher.desktop"
echo "已卸载(配置/模型/服务数据保留)"
UNIN
chmod +x "$DEST/uninstall.sh"

# 可选: 开机自启(托盘助手 + 服务常驻)
read -r -p "注册开机自启(托盘常驻, 轻量化)? [y/N] " yn
if [ "$yn" = "y" ] || [ "$yn" = "Y" ]; then
  mkdir -p "$HOME/.config/autostart"
  cat > "$HOME/.config/autostart/llamadsh-launcher.desktop" <<AUTO
[Desktop Entry]
Type=Application
Name=LlamaDSH Launcher(托盘)
Exec=$DEST/start.sh --minimized
Icon=llamadsh-launcher
X-GNOME-Autostart-enabled=true
AUTO
  echo "✓ 已注册开机自启"
fi

update-desktop-database "$HOME/.local/share/applications" 2>/dev/null || true
gtk-update-icon-cache -f -t "$ICON_DIR" 2>/dev/null || true
echo "✓ 安装完成: $DEST"
echo "  应用菜单: LlamaDSH Launcher(或运行 $DEST/start.sh)"
echo "  卸载: $DEST/uninstall.sh"
