#!/bin/bash
# ============================================================
# 多通道平行反应仪 · 真机启动脚本
# 用法：把本脚本放到 ParallelReactor 可执行文件同目录，
#       chmod +x run.sh 后执行 ./run.sh 启动。
# 串口权限：首次需 sudo usermod -aG dialout $USER 再重新登录。
# 全部模块串口参数：9600 8-N-1。
# ============================================================
cd "$(dirname "$0")"

# ---------- 温控 AI-8（1 拖 8：一台、一个站地址、8 路）----------
export PR_TEMP_PORT=/dev/ttyS5
export PR_TEMP_SLAVE=1
export PR_TEMP_DPT=1          # 小数位：寄存器=实际值×10^dPt（1=一位小数）

# ---------- 搅拌 DM2C ----------
export PR_STIR_PORT=/dev/ttyS4
export PR_STIR_SLAVE=1

# ---------- 压力 8AI（4-20mA / 0-600psi，已是默认）----------
export PR_PRESS_PORT=/dev/ttyS6
export PR_PRESS_SLAVE=1
# export PR_PRESS_SIGNAL=4-20      # 若变送器是 0-10V 改成 0-10
# export PR_PRESS_FULLSCALE=600    # 变送器满量程（psi）

# ---------- 阀门 IO16R ----------
export PR_IO_PORT=/dev/ttyS8
export PR_IO_SLAVE=1
# export PR_IO_ENERGIZE=close      # 若点"开"反而关（常开阀），取消本行注释翻转方向

# ---------- 显示 / 字体（可选，按需开启）----------
# export PR_UI_SCALE=1.5           # 高 DPI 渲染缩放（默认 1.5，适配 1080p）
# export PR_TEXT_MODE=antialias    # 文本渲染：alias / antialias / subpixel

./ParallelReactor
