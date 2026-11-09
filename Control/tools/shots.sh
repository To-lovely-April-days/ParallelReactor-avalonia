#!/usr/bin/env bash
# shots.sh — 演示模式截图 + 审计 + 左右对照（PORT-PLAN §5 M1「截图工具」、§5.9）。
#
# 先构建一次 Debug（截图代码 Shot/* 只在 Debug 里；连同引用的 Core / Client / Contracts 一起构建，干净的检出也能跑，
# 改了 Core 也不会拿旧 DLL 截图），再按尺寸各起一个无头进程：
#   PR_CONTROL_HEADLESS=1 PR_CONTROL_DEMO=1 PR_CONTROL_SHOT=<输出目录> PR_CONTROL_SHOT_SIZE=WxH
# 配置和数据目录都指到输出目录下（不往用户目录写）。每个尺寸先跑一次审计自检（确认审计本身是活的），
# 再按 Shot/ShotPlan.cs 截图；macOS 上再用系统字体栈（更窄的西文）把每张截图审计一遍（alt-font/，只审计不对照）——
# 两种宽度下都不许有问题。最后打印所有非空的 *.audit.txt，有任何一个非空或任何进程失败就以非 0 退出。
# 开跑前删掉输出目录里上一次留下的 *.audit.txt、error*.txt、shot*.log（旧结果不能让这次失败或误导）。
#
# 用法：Control/tools/shots.sh [输出目录] [尺寸 ...]
#   输出目录默认 $PR_SHOTS_OUT，否则 ./shots-out；尺寸默认 $PR_SHOTS_SIZES，否则外壳的三个尺寸（1920x1080 1600x900 1366x768：
#   overview、device1、reactor11）加组件陈列页的三个（1920x5100 1600x4200 1366x4200：gallery）。
#   PR_CONTROL_SHOT_NAMES=gallery,…  只跑这些截图（任意尺寸）。
#   PR_CONTROL_TREE_DUMP=1           每张截图另写 <name>-<w>.tree.txt（可视树：类型、名字、类、矩形、文字、字号、实际字体）。
#   PR_CONTROL_SHOT_REF=<目录>        参照图目录（final-<name>-<w>.png）；默认用样稿的 research/pc-v3/shots。
#   PR_CONTROL_CONFIG / PR_CONTROL_DATA 可覆盖（默认 <输出目录>/cfg、<输出目录>/data）。
#   PR_SHOTS_ALT_FONT=<字体栈>        第二遍审计的字体栈（macOS 默认 Theme/UiFonts.MacSystemStack；none = 不跑第二遍）。
#   PR_SHOTS_FAKE2=0                  不跑场景 fake2（默认：外壳的三个尺寸各跑一次 —— 四台进程内的假 HMI 经真 ReactorConnection 连接，
#                                     fake2-overview / fake2-device1 / fake2-reactor11 / fake2-device3 / fake2-device4 / fake2-devices /
#                                     fake2-client / fake2-exit（真机才有的两个对话框：本机客户端名、退出确认）；
#                                     只给了 PR_CONTROL_SHOT_NAMES 时，名字里有 fake2 才跑）。
#   DOTNET=…                          dotnet 路径（默认 ~/.dotnet/dotnet，不在 PATH 上）。
#   SKIP_BUILD=1                      不重新构建。
set -u
export DOTNET_CLI_TELEMETRY_OPTOUT=1 AVALONIA_TELEMETRY_OPTOUT=1
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APPDIR="$(cd "$HERE/../ParallelReactor.Control" && pwd)"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
OUT="${1:-${PR_SHOTS_OUT:-$PWD/shots-out}}"
shift $(( $# > 0 ? 1 : 0 ))
SIZES=("$@")
if [ ${#SIZES[@]} -eq 0 ]; then read -r -a SIZES <<< "${PR_SHOTS_SIZES:-1920x1080 1600x900 1366x768 1920x5100 1600x4200 1366x4200}"; fi
REF="${PR_CONTROL_SHOT_REF:-$(cd "$HERE/../../.." && pwd)/research/pc-v3/shots}"
ALT_FONT="${PR_SHOTS_ALT_FONT:-}"
if [ -z "$ALT_FONT" ] && [ "$(uname -s)" = "Darwin" ]; then ALT_FONT=".AppleSystemUIFont, PingFang SC, fonts:Inter#Inter"; fi
[ "$ALT_FONT" = "none" ] && ALT_FONT=""

mkdir -p "$OUT" || exit 2
OUT="$(cd "$OUT" && pwd)"
CFG="${PR_CONTROL_CONFIG:-$OUT/cfg}"
DATA="${PR_CONTROL_DATA:-$OUT/data}"
mkdir -p "$CFG" "$DATA"

# 上一次的结果（只删本脚本自己产生的这几类文件）
for d in "$OUT" "$OUT/selftest" "$OUT/alt-font"; do
  [ -d "$d" ] || continue
  rm -f "$d"/*.audit.txt "$d"/*.result.txt "$d"/*.tree.txt "$d"/error.txt "$d"/error-*.txt "$d"/shot.log "$d"/shot-*.log "$d"/stdout*.txt
done

if [ "${SKIP_BUILD:-0}" != "1" ]; then
  echo "== build Debug"
  "$DOTNET" build "$APPDIR/ParallelReactor.Control.csproj" -c Debug -nologo -v q > "$OUT/build.log" 2>&1
  rc=$?
  grep -E "warning|error" "$OUT/build.log" | grep -v "^\s*[0-9]* \(Warning\|Error\)(s)" | sort -u
  if [ $rc -ne 0 ]; then echo "build failed (see $OUT/build.log)"; exit 2; fi
fi
DLL="$APPDIR/bin/Debug/net8.0/ParallelReactor.Control.dll"
[ -f "$DLL" ] || { echo "missing $DLL"; exit 2; }

# 这个尺寸要拍的截图名（计划由程序自己给：PR_CONTROL_SHOT_PLAN=1 打印「名字 路由 尺寸,尺寸」；给了 PR_CONTROL_SHOT_NAMES 时就是这些名字）
NAMES="${PR_CONTROL_SHOT_NAMES:-${PR_CONTROL_SHOTS:-}}"
plan_names() {   # plan_names <尺寸>
  if [ -n "$NAMES" ]; then echo "$NAMES" | tr ',' '\n' | tr -d ' '; return; fi
  PR_CONTROL_SHOT_PLAN=1 "$DOTNET" "$DLL" 2>/dev/null | awk -v sz="$1" '{ n = split($3, a, ","); for (i = 1; i <= n; i++) if (a[i] == sz) print $1 }'
}
# 开拍前删掉这个尺寸上一次留下的截图、对照图与树转储（旧图不能冒充这次的结果；同宽度的其它尺寸的截图名不同，不受影响）
clean_size() {   # clean_size <尺寸>
  local w="${1%x*}" n d
  for n in $(plan_names "$1"); do
    for d in "$OUT" "$OUT/alt-font"; do rm -f "$d/$n-$w.png" "$d/cmp-$n-$w.png" "$d/$n-$w.tree.txt"; done
  done
  rm -f "$OUT/selftest/audit-selftest-$w".*
  # fake2 只在外壳的三个尺寸拍：组件陈列页的尺寸（同宽度 1920x5100 …）不能删它们
  if fake2_wanted "$1"; then
    for n in $FAKE2_NAMES; do
      for d in "$OUT" "$OUT/alt-font"; do rm -f "$d/$n-$w.png" "$d/$n-$w.tree.txt"; done
    done
  fi
}

# 场景 fake2（Shot/ShotRunner.Fake2.cs）：外壳的三个尺寸；不是演示模式（PR_CONTROL_DEMO=0），数据来自进程内的假 HMI
FAKE2_NAMES="fake2-overview fake2-device1 fake2-reactor11 fake2-device3 fake2-device4 fake2-devices fake2-client fake2-exit"
fake2_wanted() {   # fake2_wanted <尺寸>
  [ "${PR_SHOTS_FAKE2:-1}" = "0" ] && return 1
  case "$1" in 1920x1080|1600x900|1366x768) ;; *) return 1 ;; esac
  [ -z "$NAMES" ] && return 0
  echo "$NAMES" | tr ',' '\n' | tr -d ' ' | grep -q '^fake2'
}
# 演示截图的名字（给了 PR_CONTROL_SHOT_NAMES 时去掉 fake2 的；只剩 fake2 时不跑演示截图）
DEMO_NAMES="$(echo "$NAMES" | tr ',' '\n' | tr -d ' ' | grep -v '^fake2' | grep -v '^$' | paste -sd, -)"
demo_wanted() { [ -z "$NAMES" ] || [ -n "$DEMO_NAMES" ]; }

fail=0
run() {   # run <子目录> <尺寸> [额外环境变量 …]
  local dir="$1" size="$2"; shift 2
  mkdir -p "$dir"
  env PR_CONTROL_HEADLESS=1 PR_CONTROL_DEMO=1 PR_CONTROL_SHOT="$dir" PR_CONTROL_SHOT_SIZE="$size" \
      PR_CONTROL_SHOT_REF="$REF" PR_CONTROL_CONFIG="$CFG" PR_CONTROL_DATA="$DATA" "$@" \
      "$DOTNET" "$DLL" > "$dir/stdout-$size.txt" 2>&1
}
# 一个尺寸的截图（主字体或第二遍字体）：退出码、日志按宽度改名保存
shots() {   # shots <目录> <尺寸> [额外环境变量 …]
  local dir="$1" size="$2"; shift 2
  run "$dir" "$size" "$@"
  local rc=$?
  [ -f "$dir/shot.log" ] && mv "$dir/shot.log" "$dir/shot-$size.log"
  [ -f "$dir/error.txt" ] && mv "$dir/error.txt" "$dir/error-$size.txt"
  if [ $rc -eq 2 ]; then echo "   exit 2:"; head -n 20 "$dir/error-$size.txt" 2>/dev/null; fail=1
  elif [ $rc -ne 0 ] && [ $rc -ne 1 ]; then echo "   exit $rc"; fail=1; fi
  grep -E "^(saved|audit|compare)" "$dir/shot-$size.log" 2>/dev/null | sed 's/^/   /'
}

# 场景 fake2 的一个尺寸：日志另存 shot-fake2-<尺寸>.log（不覆盖演示截图的日志）
fake2() {   # fake2 <目录> <尺寸> [额外环境变量 …]
  local dir="$1" size="$2"; shift 2
  mkdir -p "$dir"
  env PR_CONTROL_HEADLESS=1 PR_CONTROL_DEMO=0 PR_CONTROL_SCENARIO=fake2 PR_CONTROL_SHOT="$dir" PR_CONTROL_SHOT_SIZE="$size" \
      PR_CONTROL_CONFIG="$CFG" PR_CONTROL_DATA="$DATA" "$@" \
      "$DOTNET" "$DLL" > "$dir/stdout-fake2-$size.txt" 2>&1
  local rc=$?
  [ -f "$dir/shot.log" ] && mv "$dir/shot.log" "$dir/shot-fake2-$size.log"
  [ -f "$dir/error.txt" ] && mv "$dir/error.txt" "$dir/error-fake2-$size.txt"
  if [ $rc -eq 2 ]; then echo "   exit 2:"; head -n 20 "$dir/error-fake2-$size.txt" 2>/dev/null; fail=1
  elif [ $rc -ne 0 ] && [ $rc -ne 1 ]; then echo "   exit $rc"; fail=1; fi
  grep -E "^(saved|audit)" "$dir/shot-fake2-$size.log" 2>/dev/null | sed 's/^/   /'
}

for size in "${SIZES[@]}"; do
  w="${size%x*}"
  clean_size "$size"
  echo "== $size: audit self-test"
  run "$OUT/selftest" "$size" PR_CONTROL_AUDIT_SELFTEST=1
  rc=$?
  # 自检的日志按尺寸改名（同一宽度的两个尺寸也分开）
  [ -f "$OUT/selftest/shot.log" ] && mv "$OUT/selftest/shot.log" "$OUT/selftest/shot-$size.log"
  [ -f "$OUT/selftest/error.txt" ] && mv "$OUT/selftest/error.txt" "$OUT/selftest/error-$size.txt"
  tail -n 1 "$OUT/selftest/audit-selftest-$w.result.txt" 2>/dev/null
  if [ $rc -ne 0 ]; then echo "   self-test exit $rc"; fail=1; fi

  if demo_wanted; then
    echo "== $size: shots"
    shots "$OUT" "$size" ${NAMES:+PR_CONTROL_SHOT_NAMES="$DEMO_NAMES"} ${NAMES:+PR_CONTROL_SHOTS=}
    if [ -n "$ALT_FONT" ]; then
      echo "== $size: audit again with font \"$ALT_FONT\""
      shots "$OUT/alt-font" "$size" PR_CONTROL_UI_FONT="$ALT_FONT" PR_CONTROL_SHOT_REF= ${NAMES:+PR_CONTROL_SHOT_NAMES="$DEMO_NAMES"} ${NAMES:+PR_CONTROL_SHOTS=}
    fi
  fi
  if fake2_wanted "$size"; then
    echo "== $size: scenario fake2 (in-process fake HMIs, real ReactorConnection)"
    fake2 "$OUT" "$size"
    if [ -n "$ALT_FONT" ]; then
      echo "== $size: fake2 audited again with font \"$ALT_FONT\""
      fake2 "$OUT/alt-font" "$size" PR_CONTROL_UI_FONT="$ALT_FONT"
    fi
  fi
done

echo "== non-empty audits"
found=0
for f in "$OUT"/*.audit.txt "$OUT"/alt-font/*.audit.txt; do
  [ -e "$f" ] || continue
  if [ -s "$f" ]; then found=1; echo "--- ${f#$OUT/}"; cat "$f"; fi
done
[ $found -eq 0 ] && echo "(none)"
[ $found -ne 0 ] && fail=1

# 不许往用户目录写（PORT-PLAN §5.0 完成标准 4）
n=$(ls "$HOME/Library/Application Support" 2>/dev/null | grep -ci parallelreactor)
if [ "${n:-0}" != "0" ]; then echo "!! ~/Library/Application Support 里出现了 ParallelReactor 目录"; fail=1; fi

exit $fail
