#!/usr/bin/env bash
# snap-refs.sh — 现拍样稿的参照图（PORT-PLAN §5.0「其它状态的配色参照用 snap 工具现拍」、§5.9；doc §9）。
#
# 按截图计划（PR_CONTROL_SHOT_PLAN=1 打印的「名字 路由 尺寸,尺寸 参数」）给样稿没有 final 图的截图（对话框 dlg-*、状态集 st-*、总览 ov-*、设备页 dv-*、
# 反应釜页 reactor*（冻结的 final 图之外的尺寸）/ rv-* / rvp*）各拍一张：
#   snap <样稿 index.html> <输出目录>/final-<名字>-<宽>.png <宽> <高> "/<路由>?bare=1&freeze=1&<参数>" 1300 "0"
# 参数里有 scroll=end（我们的演示参数：总览滚到底；样稿不认它）时，JS 把样稿的 .overview-scroll 滚到底再拍。
# dv-never（截图准备 adddevice：刚添加、从未连上的 5号机）：样稿先开总览，JS 调 PR.sim.addDevice(...) 再 PR.route.go('device', [k])。
# rvp*（截图准备 rvprops：反应釜页点了「属性查看器」页签）：JS 点样稿同一个页签再拍。
# rv-progx / rv-maxprog / rv-zoom1 / rv-zoom2（截图准备 rvprogx / rvmaxprog / rvzoom1 / rvzoom2）：JS 点样稿「程序」面板的关闭 / 放大 / 放大画布（一次 / 两次）；
# rv-maxalarm21（截图准备 m6g4-maxcurves）：JS 点样稿「曲线」面板的放大；
# rv-never（截图准备 adddevice）：样稿先开总览，JS 调 PR.sim.addDevice(...) 再 PR.route.go('reactor', [k, 1])。
# 样稿冻结的 final-*.png（research/pc-v3/shots，M1 固定的那份）不重拍：给了 FROZEN=<目录> 时把那里的 final-*.png 链接进输出目录，
# 输出目录就能直接当 PR_CONTROL_SHOT_REF 用。必须在 bash 下运行（snap 是 WebKit 截图工具，见 PORT-PLAN 附录 B）。
#
# 用法：Control/tools/snap-refs.sh <输出目录> [名字前缀 …]
#   SNAP=<snap 可执行文件>      必填（scratchpad/snap/snap；scratch 被清掉时用同目录的 snap.swift 重新编译）
#   MOCK=<样稿 index.html>      默认 ../../../research/pc-v3/index.html（相对本脚本）
#   FROZEN=<冻结的参照目录>     可选：把那里的 final-*.png 链接进来（已有同名文件时不覆盖）
#   名字前缀默认「dlg- st- ov- dv- reactor rv- rvp」；只重拍某几张时给前缀或完整名字（dlg-estop2）。
#   SKIP_BUILD=1 不构建 App（截图计划由 App 打印）。
set -u
export DOTNET_CLI_TELEMETRY_OPTOUT=1 AVALONIA_TELEMETRY_OPTOUT=1
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APPDIR="$(cd "$HERE/../ParallelReactor.Control" && pwd)"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
OUT="${1:?用法：snap-refs.sh <输出目录> [名字前缀 …]}"
shift
PREFIXES=("$@")
[ ${#PREFIXES[@]} -eq 0 ] && PREFIXES=("dlg-" "st-" "ov-" "dv-" "reactor" "rv-" "rvp")
SNAP="${SNAP:?请设置 SNAP=<snap 可执行文件>}"
MOCK="${MOCK:-$(cd "$HERE/../../.." && pwd)/research/pc-v3/index.html}"
[ -x "$SNAP" ] || { echo "snap 不可执行：$SNAP"; exit 2; }
[ -f "$MOCK" ] || { echo "没有样稿：$MOCK"; exit 2; }
mkdir -p "$OUT" || exit 2
OUT="$(cd "$OUT" && pwd)"

if [ "${SKIP_BUILD:-0}" != "1" ]; then
  "$DOTNET" build "$APPDIR/ParallelReactor.Control.csproj" -c Debug -nologo -v q > "$OUT/build.log" 2>&1 || { echo "build failed (see $OUT/build.log)"; exit 2; }
fi
DLL="$APPDIR/bin/Debug/net8.0/ParallelReactor.Control.dll"

if [ -n "${FROZEN:-}" ]; then
  for f in "$FROZEN"/final-*.png; do [ -e "$OUT/$(basename "$f")" ] || ln -s "$f" "$OUT/$(basename "$f")"; done
fi

fail=0
while read -r name route sizes query; do
  hit=0
  for p in "${PREFIXES[@]}"; do case "$name" in "$p"*) hit=1 ;; esac; done
  [ $hit -eq 1 ] || continue
  for sz in ${sizes//,/ }; do
    w="${sz%x*}"; h="${sz#*x}"
    dest="$OUT/final-$name-$w.png"
    # 冻结的 final 图（链接进来的）不重拍
    [ -L "$dest" ] && continue
    hash="/$route?bare=1&freeze=1${query:+&$query}"
    js="0"
    wait=1300
    case "&$query&" in *"&scroll=end&"*) js="document.querySelector('.overview-scroll').scrollTop=100000" ;; esac
    case "$name" in
      rvp*)
        js="document.querySelector('[data-ptabs=\"rv-tab\"] .pr-ptab[data-v=\"props\"]').click()" ;;
      rv-progx)
        js="document.querySelector('[data-key=\"rvp-prog\"] [data-panel-close]').click()" ;;
      rv-maxprog)
        js="document.querySelector('[data-key=\"rvp-prog\"] [data-panel-max]').click()" ;;
      rv-maxalarm21)
        js="document.querySelector('[data-key=\"rvp-curves\"] [data-panel-max]').click()" ;;
      rv-zoom1)
        js="document.querySelector('[data-rv-zoom=\"1\"]').click()" ;;
      rv-zoom2)
        js="document.querySelector('[data-rv-zoom=\"1\"]').click(); setTimeout(function(){document.querySelector('[data-rv-zoom=\"1\"]').click()},100)"
        wait=1600 ;;
      rv-never)
        hash="/overview?bare=1&freeze=1"
        js="var d=PR.sim.addDevice({host:'192.168.1.50',port:8080,token:'x',alias:'5号机',autoConnect:true,modelKey:'PR8'}); PR.route.go('reactor',[d.k,1]);"
        wait=1600 ;;
      dv-never)
        hash="/overview?bare=1&freeze=1"
        js="var d=PR.sim.addDevice({host:'192.168.1.50',port:8080,token:'x',alias:'5号机',autoConnect:true,modelKey:'PR8'}); PR.route.go('device',[d.k]);"
        wait=1500 ;;
    esac
    if "$SNAP" "$MOCK" "$dest" "$w" "$h" "$hash" "$wait" "$js" > "$OUT/log-$name-$w.txt" 2>&1; then echo "   $name-$w  $hash"
    else echo "!! $name-$w 失败（见 log-$name-$w.txt）"; fail=1; fi
  done
done < <(PR_CONTROL_SHOT_PLAN=1 "$DOTNET" "$DLL" 2>/dev/null)
exit $fail
