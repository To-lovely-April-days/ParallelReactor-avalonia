#!/bin/bash
# gen-curves-vectors.sh — 用 WebKit 实际渲染样稿 research/pc-v3（snap 工具，PORT-PLAN 附录 B），跑 probe-curves.js，把曲线量到的结果写成
# ../../ParallelReactor.Control.App.Tests/CurvesMockVectors.cs：反应釜页「曲线」面板（.reactor-curves：面板矩形、图例显示着的项、分段的当前值、
# 曲线容器 .reactor-chart 的尺寸与 SVG）、总览「温度趋势」（.overview-chart）、组件陈列「曲线」（三张 .gallery-chart），以及参考线 / 标记文字的
# 量宽（ui.textWidth 14/600）。App.Tests 的 CurvesTests 用同一个地址的演示数据（ReactorCurvesVm / OverviewTrendVm / GalleryCharts）+ 这里的容器尺寸与字宽
# 排出 Controls/Chart/ChartScene，逐元素对照这里的 SVG —— 期望值全部来自样稿自己的 JS / CSS 在 WebKit 里的结果，不手写。
# 用法（必须在 bash 下；输出目录放在 scratch）：
#   bash gen-curves-vectors.sh <snap 可执行文件> <research/pc-v3 目录> <输出目录> > ../../ParallelReactor.Control.App.Tests/CurvesMockVectors.cs
set -euo pipefail
SNAP="$1"; MOCK="$2"; OUT="$3"
HERE="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$OUT"
: > "$OUT/index.tsv"
probe() { # name w h route
  "$SNAP" "$MOCK/index.html" "$OUT/$1-$2.png" "$2" "$3" "$4" 1300 "$(cat "$HERE/probe-curves.js")" 2>&1 | grep -E 'PROBE' | sed 's/^\[page\] warn: PROBE //' > "$OUT/$1-$2.json"
  printf '%s\t%s\t%s\t%s\n' "$1" "$2" "$3" "$4" >> "$OUT/index.tsv"
}
B="bare=1&freeze=1"
for sz in 1920x1080 1600x900 1366x768; do
  w=${sz%x*}; h=${sz#*x}
  probe rv11 "$w" "$h" "/reactor/1/1?$B"
  probe rv13 "$w" "$h" "/reactor/1/3?$B"
  probe rv21 "$w" "$h" "/reactor/2/1?$B"
  probe rv311 "$w" "$h" "/reactor/3/11?$B&sel=3/11"
  probe rv-stress1 "$w" "$h" "/reactor/1/1?$B&stress=1"
  probe rv-stress2 "$w" "$h" "/reactor/1/1?$B&stress=2"
  probe rv-stress1-14 "$w" "$h" "/reactor/1/4?$B&stress=1"
  probe rv-stress2-316 "$w" "$h" "/reactor/3/16?$B&stress=2&sel=3/16"
  probe rv-off1 "$w" "$h" "/reactor/1/1?$B&offline=1"
  probe rv-estop1 "$w" "$h" "/reactor/1/1?$B&estop=1"
  probe rv-alarm21 "$w" "$h" "/reactor/2/1?$B&alarm=2:1"
  probe ov-devs1 "$w" "$h" "/overview?$B&devs=1"
done
probe ov-trend-stress2 1920 1080 "/overview?$B&devs=PR16,PR4&stress=2"
probe ov-trend-sel2 1920 1080 "/overview?$B&devs=PR16,PR4&sel=2"
probe gallery 1920 5100 "/gallery?$B"
python3 - "$OUT" <<'PY'
import json, os, sys
out = sys.argv[1]
rows = [l.rstrip('\n').split('\t') for l in open(os.path.join(out, 'index.tsv'), encoding='utf-8') if l.strip()]
print('namespace ParallelReactor.Control.Tests;')
print()
print('/// <summary>')
print('/// 样稿的曲线（WebKit 里实际渲染、从 DOM 读出来的；由 ParallelReactor.Control.Tests/MockVectors/gen-curves-vectors.sh 用 snap 跑 probe-curves.js 生成，不要手改）：')
print('/// 每行一个场景（JSON）：名字、窗口宽、地址、反应釜页「曲线」面板（矩形、图例、分段）、曲线容器的尺寸与 SVG（长路径记 #fnv:长度）、参考线 / 标记文字的量宽；')
print('/// 总览的「温度趋势」与组件陈列的三张曲线同样。样稿改了就重新生成。')
print('/// </summary>')
print('internal static class CurvesMockVectors')
print('{')
print('    public const string Lines = """')
for name, w, h, route in rows:
    d = json.load(open(os.path.join(out, f'{name}-{w}.json'), encoding='utf-8'))
    d = {'name': name, 'w': int(w), 'h': int(h), 'route': route, **d}
    print(json.dumps(d, ensure_ascii=False, separators=(',', ':')))
print('""";')
print('}')
PY
