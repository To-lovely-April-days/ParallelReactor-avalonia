#!/usr/bin/env bash
# 把屏幕输出设成触摸屏的实际分辨率（默认 1280×800）。
#
# 为什么要这个：这块 10.1 寸屏物理上是 1280×800（16:10），但它通过 HDMI 只报了 1920×1080 一个模式，
# 板子默认就按 1080p 输出，屏幕再自己缩成 1280×800 —— 横竖缩的比例不一样（圆变椭圆），还会发虚。
# 直接输出 1280×800：一个像素对一个像素，界面按 1 倍画，最清楚。
#
# 用法：set-display.sh [宽 高]          可重复运行；已经是这个分辨率就什么都不做
#   reactord-hmi.service 启动界面前会先跑它（ExecStartPre）；桌面登录时也会跑（/etc/xdg/autostart/reactor-display.desktop）
# 环境变量：DISPLAY（默认 :0）、REACTOR_OUTPUT（默认取第一个已连接的输出，比如 HDMI-1）
# 恢复成 1080p：xrandr --output HDMI-1 --mode 1920x1080
set -u

W="${1:-1280}"
H="${2:-800}"
export DISPLAY="${DISPLAY:-:0}"
NAME="rp${W}x${H}"

command -v xrandr >/dev/null || { echo "set-display: 没有 xrandr，跳过" >&2; exit 0; }

# X 可能还没起来（开机时服务比桌面早）：最多等 20 s
for _ in $(seq 1 20); do
    xrandr >/dev/null 2>&1 && break
    sleep 1
done
xrandr >/dev/null 2>&1 || { echo "set-display: 连不上 X（DISPLAY=$DISPLAY），跳过" >&2; exit 0; }

OUT="${REACTOR_OUTPUT:-$(xrandr | awk '/ connected/{print $1; exit}')}"
[[ -n "$OUT" ]] || { echo "set-display: 没有已连接的输出，跳过" >&2; exit 0; }

# 已经是目标分辨率
if xrandr | grep -A0 "^$OUT connected" | grep -q " ${W}x${H}+"; then
    exit 0
fi

# 加模式（cvt 算时序；已经加过就不再加）
if ! xrandr | grep -q "^ *$NAME "; then
    if ! xrandr | grep -q "\"$NAME\""; then
        TIMING=$(cvt "$W" "$H" 60 | awk '/Modeline/{$1="";$2="";print}')
        # shellcheck disable=SC2086
        xrandr --newmode "$NAME" $TIMING || { echo "set-display: 加模式失败" >&2; exit 0; }
    fi
    xrandr --addmode "$OUT" "$NAME" || { echo "set-display: $OUT 不接受 $NAME" >&2; exit 0; }
fi
xrandr --output "$OUT" --mode "$NAME" && echo "set-display: $OUT → ${W}x${H}"
exit 0
