#!/usr/bin/env bash
# 把编译好的程序装到 /opt/reactor，建好配置 / 数据目录，装 systemd 服务文件。
# **不启用、不启动、不停止任何服务**（切换用 switch-to-cpp.sh）。可以重复运行。
#
# 用法（在板子上）：
#   sudo ./install.sh 编译目录 [--user teamhd]
# 编译目录 = 里面有 reactord、reactor-hmi、rtu-probe、fonts/ 的目录，例如
#   ~/reactor-cpp/build                                   （板子上直接编）
#   ~/.vs/reactor-cpp/<哈希>/out/build/板子-Release        （VS2022 远程编译）
#
# 装出来的样子：
#   /opt/reactor/bin/{reactord, reactor-hmi, rtu-probe, run_tests, fonts/}   上一版留在 /opt/reactor/bin.prev/
#   /opt/reactor/deploy/            本目录的拷贝（switch-to-cpp.sh、rollback.sh、gen-token.sh … 以后从这里运行）
#   /etc/reactord/                  hardware.json、api.json、reactord.env（已存在的不覆盖）；settings.json 由 reactord 写
#   /var/lib/reactord/              数据（遥测 CSV、事件、会话、泄漏结果、程序库）
#   /etc/systemd/system/reactord.service、reactord-hmi.service
#   /etc/logrotate.d/reactord
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PREFIX="/opt/reactor"
CONF_DIR="/etc/reactord"
DATA_DIR="/var/lib/reactord"
UNIT_DIR="/etc/systemd/system"
RUN_USER="teamhd"
BUILD_DIR=""

die() { echo "错误：$*" >&2; exit 1; }
info() { echo "==> $*"; }
warn() { echo "注意：$*" >&2; }

while [[ $# -gt 0 ]]; do
    case "$1" in
        --user)
            [[ $# -ge 2 ]] || die "--user 后面要跟用户名"
            RUN_USER="$2"
            shift 2
            ;;
        -h | --help)
            sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'
            exit 0
            ;;
        -*) die "不认识的参数 $1" ;;
        *)
            [[ -z "$BUILD_DIR" ]] || die "只能给一个编译目录"
            BUILD_DIR="$1"
            shift
            ;;
    esac
done

[[ $EUID -eq 0 ]] || die "要用 sudo 运行：sudo $0 编译目录"
[[ -n "$BUILD_DIR" ]] || die "缺编译目录。用法：sudo $0 编译目录"
BUILD_DIR="$(cd "$BUILD_DIR" && pwd)" || die "编译目录不存在"
for f in reactord reactor-hmi rtu-probe; do
    [[ -x "$BUILD_DIR/$f" ]] || die "$BUILD_DIR/$f 不存在或不可执行（先编译）"
done
[[ -d "$BUILD_DIR/fonts" ]] || die "$BUILD_DIR/fonts/ 不存在（reactor-hmi 编译后会自动拷过去，先完整编译一次）"
getent passwd "$RUN_USER" >/dev/null || die "用户 $RUN_USER 不存在"
RUN_UID="$(id -u "$RUN_USER")"
RUN_GROUP="$(id -gn "$RUN_USER")"

if command -v file >/dev/null 2>&1; then
    if ! file -L "$BUILD_DIR/reactord" | grep -q "aarch64"; then
        warn "$BUILD_DIR/reactord 看起来不是 aarch64 程序：$(file -bL "$BUILD_DIR/reactord")"
        if [[ "$(uname -m)" == "aarch64" ]]; then die "架构不对，停止安装"; fi
    fi
fi

# ---------- 1. 程序 ----------
# 先把要装的文件拷到临时目录：编译目录也可以是 $PREFIX/bin.prev（装回上一版），下面会覆盖它
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
for f in reactord reactor-hmi rtu-probe run_tests; do
    if [[ -x "$BUILD_DIR/$f" ]]; then cp -p "$BUILD_DIR/$f" "$STAGE/$f"; fi
done
cp -r "$BUILD_DIR/fonts" "$STAGE/fonts"
BUILD_DIR="$STAGE"

info "安装程序到 $PREFIX/bin"
mkdir -p "$PREFIX"
if [[ -d "$PREFIX/bin" ]]; then
    rm -rf "$PREFIX/bin.prev"
    cp -a "$PREFIX/bin" "$PREFIX/bin.prev"
    info "上一版已备份到 $PREFIX/bin.prev"
fi
mkdir -p "$PREFIX/bin"
# 先写 .new 再改名：替换正在运行的程序也不会出「Text file busy」，运行中的进程继续用旧文件
install_bin() {
    local src="$1" dst="$2"
    install -m 0755 -o root -g root "$src" "$dst.new"
    mv -f "$dst.new" "$dst"
}
for f in reactord reactor-hmi rtu-probe; do
    install_bin "$BUILD_DIR/$f" "$PREFIX/bin/$f"
done
if [[ -x "$BUILD_DIR/run_tests" ]]; then
    install_bin "$BUILD_DIR/run_tests" "$PREFIX/bin/run_tests"
else
    warn "编译目录里没有 run_tests（REACTOR_BUILD_TESTS=OFF？），跳过"
fi
rm -rf "$PREFIX/bin/fonts.new"
cp -r "$BUILD_DIR/fonts" "$PREFIX/bin/fonts.new"
rm -rf "$PREFIX/bin/fonts"
mv "$PREFIX/bin/fonts.new" "$PREFIX/bin/fonts"
chmod -R a+rX "$PREFIX/bin/fonts"

# ---------- 2. 部署脚本拷一份 ----------
if [[ "$SCRIPT_DIR" != "$PREFIX/deploy" ]]; then
    info "拷贝部署脚本到 $PREFIX/deploy"
    rm -rf "$PREFIX/deploy.new"
    mkdir -p "$PREFIX/deploy.new"
    cp -r "$SCRIPT_DIR/." "$PREFIX/deploy.new/"
    rm -rf "$PREFIX/deploy"
    mv "$PREFIX/deploy.new" "$PREFIX/deploy"
    chmod 0755 "$PREFIX/deploy/"*.sh
fi

# ---------- 3. 目录 ----------
info "建目录 $CONF_DIR（$RUN_USER 可写：reactord 要写 settings.json）、$DATA_DIR"
install -d -m 0750 -o "$RUN_USER" -g "$RUN_GROUP" "$CONF_DIR"
install -d -m 0750 -o "$RUN_USER" -g "$RUN_GROUP" "$DATA_DIR"

# ---------- 4. 默认配置（已有的一律不动）----------
install_default() {
    local name="$1" mode="$2"
    if [[ -e "$CONF_DIR/$name" ]]; then
        info "$CONF_DIR/$name 已存在，保留不动"
    else
        install -m "$mode" -o "$RUN_USER" -g "$RUN_GROUP" "$SCRIPT_DIR/config/$name" "$CONF_DIR/$name"
        info "已放默认 $CONF_DIR/$name"
    fi
}
install_default hardware.json 0644
install_default api.json 0640

ENV_FILE="$CONF_DIR/reactord.env"
want_line="REACTORD_HMI_UID=$RUN_UID"
if [[ -f "$ENV_FILE" ]] && grep -qx "$want_line" "$ENV_FILE"; then
    info "$ENV_FILE 已是 $want_line"
else
    if [[ -f "$ENV_FILE" ]]; then
        cp -p "$ENV_FILE" "$ENV_FILE.bak-$(date +%Y%m%d-%H%M%S)"
        grep -v '^REACTORD_HMI_UID=' "$ENV_FILE" >"$ENV_FILE.tmp" || true
    else
        printf '# reactord.service 的环境变量（install.sh 生成）\n# 只认这个 uid 的本机连接（触屏 reactor-hmi 的运行用户 %s）\n' "$RUN_USER" >"$ENV_FILE.tmp"
    fi
    echo "$want_line" >>"$ENV_FILE.tmp"
    chown root:"$RUN_GROUP" "$ENV_FILE.tmp"
    chmod 0644 "$ENV_FILE.tmp"
    mv -f "$ENV_FILE.tmp" "$ENV_FILE"
    info "已写 $ENV_FILE（$want_line）"
fi

# ---------- 5. systemd 服务文件（不启用、不启动）----------
info "桌面登录时把屏幕设成 1280×800（/etc/xdg/autostart/reactor-display.desktop）"
mkdir -p /etc/xdg/autostart
cat > /etc/xdg/autostart/reactor-display.desktop <<DESKTOP
[Desktop Entry]
Type=Application
Name=Reactor display 1280x800
Comment=触摸屏物理分辨率 1280×800：HDMI 默认的 1920×1080 会被屏幕压扁
Exec=$PREFIX/deploy/set-display.sh
X-GNOME-Autostart-enabled=true
NoDisplay=true
DESKTOP

info "安装服务文件 reactord.service、reactord-hmi.service"
reload=0
for unit in reactord.service reactord-hmi.service; do
    tmp="$(mktemp)"
    sed -e "s/^User=teamhd$/User=$RUN_USER/" -e "s/^Group=teamhd$/Group=$RUN_GROUP/" \
        "$SCRIPT_DIR/systemd/$unit" >"$tmp"
    if ! cmp -s "$tmp" "$UNIT_DIR/$unit" 2>/dev/null; then
        install -m 0644 -o root -g root "$tmp" "$UNIT_DIR/$unit"
        reload=1
    fi
    rm -f "$tmp"
done
if [[ $reload -eq 1 ]]; then systemctl daemon-reload; fi

# ---------- 6. 追踪文件轮转 ----------
sed -e "s/su teamhd teamhd/su $RUN_USER $RUN_GROUP/" "$SCRIPT_DIR/config/logrotate-reactord" >/etc/logrotate.d/reactord
chmod 0644 /etc/logrotate.d/reactord

# ---------- 7. 检查（只提示）----------
echo
info "检查"
for p in /dev/ttyS4 /dev/ttyS5 /dev/ttyS6 /dev/ttyS8; do
    if [[ -e "$p" ]]; then echo "  $p 存在"; else warn "$p 不存在（hardware.json 里的端口对吗？）"; fi
done
if id -nG "$RUN_USER" | tr ' ' '\n' | grep -qx dialout; then
    echo "  $RUN_USER 在 dialout 组（手工跑 rtu-probe 能开串口）"
else
    warn "$RUN_USER 不在 dialout 组：服务不受影响（服务文件里有 SupplementaryGroups=dialout），但手工跑 rtu-probe 会没权限。需要时：sudo usermod -aG dialout $RUN_USER 后重新登录"
fi
if systemctl is-active --quiet reactord.service; then
    warn "reactord 正在运行：新程序已就位，但要等下一次重启才生效。只能在没有实验时重启（重启 = 停全部加热、关阀、停搅拌）"
fi
if systemctl is-active --quiet reactord-hmi.service; then
    echo "  触屏 reactord-hmi 在运行：新界面要 sudo systemctl restart reactord-hmi 才生效（只重启界面不影响控制）"
fi
if systemctl is-active --quiet reactor-hmi.service; then
    echo "  旧 C# 程序（reactor-hmi.service）在运行，没有动它。切换：sudo $PREFIX/deploy/switch-to-cpp.sh"
fi
echo
info "安装完成。下一步见 deploy/板子联调指南.md"
