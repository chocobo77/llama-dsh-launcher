#!/usr/bin/env bash
# 打 Linux 发布包: 源码 + 客户端 + 安装器 + 图标, tar.gz 适配当前环境。
set -e
APP_DIR="$(cd "$(dirname "$0")" && pwd)"
VER="$(grep -m1 'AppVersion' "$APP_DIR/../LlamaDSH_Launcher_CS/installer.iss" 2>/dev/null | tr -d '\r\"' | sed 's/.*=//' | tr -dc '0-9.')"
VER="${VER:-1.1.0}"
OUT="$APP_DIR/dist/LlamaDSH_Launcher_Linux_v${VER}.tar.gz"
STAGE="$(mktemp -d)/llamadsh-launcher-v${VER}"
mkdir -p "$STAGE" "$APP_DIR/dist"
# 源码与资产(排除缓存/截图/发布产物)
cd "$APP_DIR"
cp *.py llama_params.yml start.sh install.sh build_release.sh "$STAGE"/
cp -r webui assets "$STAGE"/
rm -rf "$STAGE/webui/__pycache__"
find "$STAGE" -name "__pycache__" -type d -exec rm -rf {} + 2>/dev/null || true
cat > "$STAGE/VERSION" <<V
LlamaDSH Launcher (Linux) v${VER}
适配环境: Ubuntu/GNOME(X11 或 Wayland) · Python 3.10+ · GTK3+WebKit2(python3-gi) · pyyaml
启动: bash start.sh   安装: bash install.sh
V
tar -czf "$OUT" -C "$(dirname "$STAGE")" "$(basename "$STAGE")"
echo "✓ 发布包: $OUT ($(du -h "$OUT" | cut -f1))"
