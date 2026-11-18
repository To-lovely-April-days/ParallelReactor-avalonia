#!/usr/bin/env bash
# snap-fusion-refs-f7.sh — 融合版 F7「记录」页的参照图（样稿 research/main-fusion.html 的 PAGES.rec 各状态）。
# 同 snap-fusion-refs.sh（共用的名字表不动，免得与 F4–F6 并行改同一个文件）：样稿没有记录页的地址参数，状态用 JS 点出来
# （[data-rcsel] 选会话、[data-rcch] 选通道、[data-rcm] 曲线模式、[data-rct] 下方页签）。
# 输出 <输出目录>/final-<名字>-<宽>.png，名字表另写 INDEX-F7.txt。
# 用法：Control/tools/snap-fusion-refs-f7.sh [输出目录] [名字前缀 …]（SNAP / MOCK / SIZES / WAIT 同 snap-fusion-refs.sh）
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

C() { printf "document.querySelector('%s').click();" "$@"; }
# 名字 | 地址参数（freeze=1 自动加）| JS（0 = 不执行）| 说明
REFS=(
  "rec-all|dev=0&page=rec&apop=0|0|F7 全部设备 · 进行中的会话（1号机 PT-20261115-1305）"
  "rec-hist|dev=1&page=rec&apop=0|$(C '[data-rcsel="PT-20261114-0915"]')|F7 历史会话（批次 6，8 路完成）"
  "rec-ch|dev=1&page=rec&apop=0|$(C '[data-rcsel="PT-20261114-0915"]' '[data-rcch="6"]')|F7 历史会话 · 选中 RV6（面积填充）"
  "rec-ovt|dev=1&page=rec&apop=0|$(C '[data-rcm="T"]')|F7 进行中的会话 · 温度曲线"
  "rec-ovp|dev=1&page=rec&apop=0|$(C '[data-rcm="P"]')|F7 进行中的会话 · 压力曲线"
  "rec-files|dev=1&page=rec&apop=0|$(C '[data-rct="fl"]')|F7 数据文件页签"
  "rec-hold|dev=0&page=rec&apop=0|$(C '[data-rcsel="LK-20261115-0912"]')|F7 保压会话：泄漏测试（3号机 16 路）"
  "rec-tight|dev=1&page=rec&apop=0|$(C '[data-rcsel="PT-20261110-1600"]')|F7 保压会话：气密预检（1号机 8 路）"
  "rec-int|dev=0&page=rec&apop=0|$(C '[data-rcsel="PT-20261113-1402"]')|F7 中断的会话（琥珀说明条）"
  "rec-warn|dev=1&page=rec&apop=0|$(C '[data-rcsel="PT-20261112-0930"]')|F7 完成 · 有提醒（4 路）"
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
      echo "   $name-$w"; rm -f "$OUT/log-$name-$w.txt"; n=$((n + 1))
    else
      echo "!! $name-$w 失败（见 log-$name-$w.txt）"; fail=1
    fi
  done
done
{
  echo "# 融合版 F7 记录页参照图（tools/snap-fusion-refs-f7.sh 生成；样稿 research/main-fusion.html；尺寸 ${SIZES[*]}）"
  echo "# 名字 | 地址参数（另加 freeze=1）| JS | 说明"
  printf '%s\n' "${REFS[@]}"
} > "$OUT/INDEX-F7.txt"
echo "$n 张 → $OUT"
exit $fail
