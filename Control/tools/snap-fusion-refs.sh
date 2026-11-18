#!/usr/bin/env bash
# snap-fusion-refs.sh — 按融合版样稿 research/main-fusion.html 的地址参数现拍参照图（融合版 F1；F2 / F3 及以后的截图对照用）。
#
# 样稿读的地址参数（放在 # 后面，snap 工具把第 5 个参数当 hash）：
#   freeze=1（冻结时钟和动画）dev=N（选中第 N 台；不给 = 总览）page=main|prog|curve|alarm|test|rec|set
#   mv=run|gas|vessel|bar（主页通道视图）ovm=G|T|P（叠加曲线：吸气量 / 温度 / 压力）apop=0（不弹报警弹窗）
#   net=lost|back（断线横条 / 恢复）pgm=mx（程序页条件矩阵）set=night|upt（设置页：无人值守 / 吸气量计算）ch=N（选中通道）
#   ctl=1（打开反应釜控制）scene=maint（维护模式）
# 对话框 / 弹出层由第 7 个参数的 JS 点出来（同 pc-v3 的 snap-refs.sh）。
#
# 输出：<输出目录>/final-<名字>-<宽>.png（文件名同截图计划 Shot/ShotPlan.cs：F2 / F3 加同名的截图条目，shots.sh 就自动拼左右对照图）。
# 名字表（下面 REFS）就是 F2 / F3 要对上的状态清单；每个状态默认拍 1920×1080 和 1366×768。
# 必须在 bash 下运行（snap 是 WebKit 截图工具；scratch 被清掉时用 snap.swift 重新编译，见 pc-v3 PORT-PLAN 附录 B）。
#
# 用法：Control/tools/snap-fusion-refs.sh [输出目录] [名字前缀 …]
#   SNAP=<snap 可执行文件>   默认 /private/tmp/claude-501/…/47b193a1-…/scratchpad/snap/snap
#   MOCK=<样稿>              默认 ../../../research/main-fusion.html（相对本脚本）
#   SIZES="1920x1080 1366x768"  覆盖尺寸
#   WAIT=900                 打开后等多久再执行 JS、截图（毫秒）
#   名字前缀：只重拍某几张（home- / page- / home-estop）；不给 = 全部。
set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../../.." && pwd)"
OUT="${1:-$ROOT/research/fusion-port/ref}"
[ $# -gt 0 ] && shift
PREFIXES=("$@")
SNAP="${SNAP:-/private/tmp/claude-501/-Users-huanhuanremote-Desktop-Project----/47b193a1-3c4f-4246-b5b7-9081c378992c/scratchpad/snap/snap}"
MOCK="${MOCK:-$ROOT/research/main-fusion.html}"
read -r -a SIZES <<< "${SIZES:-1920x1080 1366x768}"
WAIT="${WAIT:-900}"
[ -x "$SNAP" ] || { echo "snap 不可执行：$SNAP"; exit 2; }
[ -f "$MOCK" ] || { echo "没有样稿：$MOCK"; exit 2; }
mkdir -p "$OUT" || exit 2
OUT="$(cd "$OUT" && pwd)"

# 名字 | 地址参数（freeze=1 自动加）| JS（0 = 不执行）| 给谁用
REFS=(
  # —— F2 外壳：六个页面的框架（标题条、菜单栏、运行胶囊、工具条、停靠面板、状态栏）——
  "home|dev=1&page=main&apop=0|0|F2 外壳 / F3 主页：1号机运行表 + 叠加曲线 + 共用资源"
  "home-overview|page=main&dev=0&apop=0|0|F2：不选设备（总览；dev=0，不给 dev 时样稿默认选 1号机）"
  "page-prog|dev=1&page=prog&apop=0|0|F2 页面框架（F6 程序页）"
  "page-curve|dev=1&page=curve&apop=0|0|F2 页面框架（F5 曲线页）"
  "page-alarm|dev=1&page=alarm&apop=0|0|F2 页面框架（F5 报警页）"
  "page-test|dev=1&page=test&apop=0|0|F2 页面框架（F8 性能测试）"
  "page-rec|dev=1&page=rec&apop=0|0|F2 页面框架（F7 记录页）"
  "page-set|dev=1&page=set&apop=0|0|F2 页面框架（F8 设置页）"
  "home-ctl|dev=1&page=main&apop=0&ctl=1|0|F2 检查器（1366 时浮出）+ F3 反应釜控制"
  "home-alarms|dev=1&page=main&apop=0|document.querySelector('.gal').click()|F2 报警摘要按钮 → 报警列表"
  "frame-rail|dev=1&page=main&apop=0&pc=pDev|0|F2 宽屏把设备面板收起成竖条（设备号小方块）"
  "frame-float|dev=1&page=main&apop=0&po=pDev|0|F2 窄屏展开设备面板（浮出 330）"
  "frame-insp|dev=1&page=main&apop=0&po=pInsp|0|F2 展开检查器（宽屏停靠 340 / 窄屏浮出 400）"
  # —— F3 主页核心 ——
  "home-apop|dev=1&page=main|0|F3 报警弹窗（默认弹出）"
  "home-ovt|dev=1&page=main&apop=0&ovm=T|0|F3 叠加曲线：温度"
  "home-ovp|dev=1&page=main&apop=0&ovm=P|0|F3 叠加曲线：压力（上限虚线）"
  "home-ch4|dev=1&page=main&apop=0&ch=4|0|F3 选中报警通道 RV4"
  "home-dev2|dev=2&page=main&apop=0|0|F3 4 路设备"
  "home-dev3|dev=3&page=main&apop=0|0|F3 16 路设备（紧凑行）"
  "home-netlost|dev=1&page=main&apop=0&net=lost|0|F3 断线横条"
  "home-netback|dev=1&page=main&apop=0&net=back|0|F3 恢复连接横条"
  "home-estop|dev=1&page=main&apop=0|document.querySelector('.cap [data-act=estop]').click()|F3 急停对话框"
  "home-stopall|dev=1&page=main&apop=0|document.querySelector('.cap [data-act=stopall]').click()|F3 全部停止对话框"
  "home-stopch|dev=1&page=main&apop=0&ctl=1|document.querySelector('[data-act=stopch]').click()|F3 停止通道对话框"
  # —— F4 主页其余视图（顺手拍上）——
  "home-gas|dev=1&page=main&apop=0&mv=gas|0|F4 气路图"
  "home-vessel|dev=1&page=main&apop=0&mv=vessel|0|F4 卡片"
  "home-bar|dev=1&page=main&apop=0&mv=bar|0|F4 棒图"
  "home-maint|dev=1&page=main&apop=0&mv=gas&scene=maint|0|F4 维护模式"
  # —— 以后的页面状态 ——
  "page-prog-mx|dev=1&page=prog&apop=0&pgm=mx|0|F6 条件矩阵"
  "page-set-night|dev=1&page=set&apop=0&set=night|0|F8 无人值守"
  "page-set-upt|dev=1&page=set&apop=0&set=upt|0|F8 吸气量计算"
)

fail=0; n=0
for row in "${REFS[@]}"; do
  IFS='|' read -r name params js what <<< "$row"
  if [ ${#PREFIXES[@]} -gt 0 ]; then
    hit=0; for p in "${PREFIXES[@]}"; do case "$name" in "$p"*) hit=1 ;; esac; done
    [ $hit -eq 1 ] || continue
  fi
  for sz in "${SIZES[@]}"; do
    w="${sz%x*}"; h="${sz#*x}"
    dest="$OUT/final-$name-$w.png"
    if "$SNAP" "$MOCK" "$dest" "$w" "$h" "freeze=1&$params" "$WAIT" "$js" > "$OUT/log-$name-$w.txt" 2>&1 && [ -s "$dest" ]; then
      echo "   $name-$w  #freeze=1&$params${js:+  js}"; rm -f "$OUT/log-$name-$w.txt"; n=$((n + 1))
    else
      echo "!! $name-$w 失败（见 log-$name-$w.txt）"; fail=1
    fi
  done
done
# 名字表（给 F2 / F3 对着加截图条目）
{
  echo "# 融合版参照图（tools/snap-fusion-refs.sh 生成；样稿 research/main-fusion.html；尺寸 ${SIZES[*]}）"
  echo "# 名字 | 地址参数（另加 freeze=1）| JS | 给谁用"
  printf '%s\n' "${REFS[@]}"
} > "$OUT/INDEX.txt"
echo "$n 张 → $OUT"
exit $fail
