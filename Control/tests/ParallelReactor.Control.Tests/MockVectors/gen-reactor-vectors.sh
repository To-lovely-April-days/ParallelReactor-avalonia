#!/bin/bash
# gen-reactor-vectors.sh — 用 WebKit 实际渲染样稿 research/pc-v3（snap 工具，PORT-PLAN 附录 B），跑 probe-reactor.js（反应釜页「程序」画布）与
# probe-trend.js（总览「温度趋势」）、probe-grid.js（反应釜页面板网格），把量到的版面写成 ReactorMockVectors.cs。Core 的 Layout/StepFlowLayout（StepFlowLayoutTests）与
# Chart/OverviewTrendOptions（ChartTicksTests 的趋势部分）拿它当参照：期望值全部来自样稿自己的 CSS / JS 在 WebKit 里的结果，不手写。
# 用法（必须在 bash 下；输出目录放在 scratch）：
#   bash gen-reactor-vectors.sh <snap 可执行文件> <research/pc-v3 目录> <输出目录> > ReactorMockVectors.cs
set -euo pipefail
SNAP="$1"; MOCK="$2"; OUT="$3"
HERE="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$OUT"
: > "$OUT/index.tsv"
probe() { # kind probe w h route name [pre]
  local js
  js="${7:-}
$(cat "$HERE/$2")"
  "$SNAP" "$MOCK/index.html" "$OUT/$6.png" "$3" "$4" "$5" 1300 "$js" 2>&1 | grep -E 'PROBE' | sed 's/^\[page\] warn: PROBE //' > "$OUT/$6.json"
  printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$1" "$6" "$3" "$4" "$5" "${7:-}" >> "$OUT/index.tsv"
}
B="bare=1&freeze=1"
# 1920：每种程序状态 / 步骤数 / 没有程序
for kn in 1/1 1/3 1/4 2/1 2/3 2/4 3/11 3/12 3/13 3/15; do
  probe flow probe-reactor.js 1920 1080 "/reactor/$kn?$B&sel=$kn" "rv-1920-${kn/\//-}"
done
for sz in 1600x900 1366x768; do
  w=${sz%x*}; h=${sz#*x}
  for kn in 1/1 2/1 3/11 3/13; do
    probe flow probe-reactor.js "$w" "$h" "/reactor/$kn?$B&sel=$kn" "rv-$w-${kn/\//-}"
  done
done
# 收起资源管理器 / 时间轴（画布更宽 / 更高）
for p in "explorer=0" "tl=0" "explorer=0&tl=0"; do
  for kn in 1/1 2/1; do
    probe flow probe-reactor.js 1920 1080 "/reactor/$kn?$B&$p" "rv-1920-${kn/\//-}-${p//[=&]/_}"
  done
done
probe flow probe-reactor.js 1366 768 "/reactor/1/1?$B&explorer=0" "rv-1366-1-1-explorer_0"
# 放大 115 % / 130 %（探针先点「放大画布」）
for z in 1 2; do
  for kn in 1/1 2/1 3/11; do
    probe flow probe-reactor.js 1920 1080 "/reactor/$kn?$B&sel=$kn" "rv-1920-${kn/\//-}-z$z" "window.__zoom = $z;"
  done
  probe flow probe-reactor.js 1600 900 "/reactor/1/1?$B" "rv-1600-1-1-z$z" "window.__zoom = $z;"
  probe flow probe-reactor.js 1366 768 "/reactor/2/1?$B" "rv-1366-2-1-z$z" "window.__zoom = $z;"
  probe flow probe-reactor.js 1920 1080 "/reactor/1/1?$B&explorer=0" "rv-1920-1-1-explorer_0-z$z" "window.__zoom = $z;"
done
# 断线（画布去色）/ 压力测试数据（最坏值、长别名）
probe flow probe-reactor.js 1920 1080 "/reactor/4/1?$B&devs=4" "rv-1920-4-1-devs4"
for st in 1 2; do
  for sz in 1920x1080 1366x768; do
    w=${sz%x*}; h=${sz#*x}
    probe flow probe-reactor.js "$w" "$h" "/reactor/1/4?$B&stress=$st" "rv-$w-1-4-stress$st"
    probe flow probe-reactor.js "$w" "$h" "/reactor/2/1?$B&stress=$st" "rv-$w-2-1-stress$st"
  done
done
probe flow probe-reactor.js 1600 900 "/reactor/3/16?$B&stress=2" "rv-1600-3-16-stress2"
# 面板网格（宽版式要工作区 ≥ 1800：2200 / 2400 宽的窗口收起资源管理器；关掉面板时其余面板补位）
probe grid probe-grid.js 1920 1080 "/reactor/1/1?$B" "gr-1920"
probe grid probe-grid.js 1600 900 "/reactor/1/1?$B" "gr-1600"
probe grid probe-grid.js 1366 768 "/reactor/1/1?$B" "gr-1366"
probe grid probe-grid.js 1920 1080 "/reactor/1/1?$B&explorer=0" "gr-1920-explorer_0"
probe grid probe-grid.js 1920 1080 "/reactor/3/11?$B&sel=3/11" "gr-1920-alarm"
probe grid probe-grid.js 2200 1100 "/reactor/1/1?$B&explorer=0" "gr-2200-wide"
probe grid probe-grid.js 2400 1200 "/reactor/1/1?$B&explorer=0" "gr-2400-wide"
probe grid probe-grid.js 3000 1400 "/reactor/1/1?$B&explorer=0" "gr-3000-wide"
for c in "rv-prog" "rv-vessel" "rv-curves" "rv-prog,rv-curves" "rv-prog,rv-vessel" "rv-vessel,rv-curves"; do
  js="window.__close = ['${c//,/','}'];"
  probe grid probe-grid.js 1920 1080 "/reactor/1/1?$B" "gr-1920-x-${c//,/-}" "$js"
  probe grid probe-grid.js 1366 768 "/reactor/1/1?$B" "gr-1366-x-${c//,/-}" "$js"
done
for c in "rv-prog" "rv-vessel" "rv-props" "rv-curves" "rv-vessel,rv-props" "rv-props,rv-curves" "rv-prog,rv-props" "rv-prog,rv-vessel"; do
  js="window.__close = ['${c//,/','}'];"
  probe grid probe-grid.js 2400 1200 "/reactor/1/1?$B&explorer=0" "gr-2400-x-${c//,/-}" "$js"
done
# 温度趋势（总览：剩余高度 ≥ 200 px 时出现）
probe trend probe-trend.js 1920 1080 "/overview?$B&devs=1" "tr-1920-devs1"
probe trend probe-trend.js 1600 900 "/overview?$B&devs=1" "tr-1600-devs1"
probe trend probe-trend.js 1366 768 "/overview?$B&devs=1" "tr-1366-devs1"
probe trend probe-trend.js 1920 1080 "/overview?$B&devs=PR16,PR4&stress=2" "tr-1920-pr16pr4"
probe trend probe-trend.js 1920 1080 "/overview?$B&devs=PR16,PR4&stress=2&sel=2" "tr-1920-pr16pr4-sel2"
probe trend probe-trend.js 1920 1080 "/overview?$B&devs=PR16,PR4&sel=2" "tr-1920-pr16pr4-nostress-sel2"

python3 -I - "$OUT" <<'PY'
import json, sys, os
out = sys.argv[1]
rows = [l.rstrip('\n').split('\t') for l in open(os.path.join(out, 'index.tsv'), encoding='utf-8')]
data = {'source': 'research/pc-v3（WebKit，snap）· probe-reactor.js / probe-trend.js / probe-grid.js', 'flow': [], 'trend': [], 'grid': []}
for kind, name, w, h, route, pre in rows:
    txt = open(os.path.join(out, name + '.json'), encoding='utf-8').read().strip()
    if not txt: raise SystemExit('no probe output for ' + name)
    data[kind].append({'name': name, 'w': int(w), 'h': int(h), 'route': route, 'pre': pre, 'probe': json.loads(txt.splitlines()[0])})
raw = json.dumps(data, ensure_ascii=False, separators=(',', ':'))
if '"""""' in raw: raise SystemExit('raw string delimiter clash')
print('// <auto-generated>')
print('// 由 MockVectors/gen-reactor-vectors.sh 用样稿 research/pc-v3 在 WebKit（snap）里实际渲染、跑 probe-reactor.js / probe-trend.js 量出来，不要手改；')
print('// 样稿改了就重新生成（命令见脚本开头）。flow：反应釜页「程序」画布的版面；trend：总览温度趋势的图例与 SVG；grid：反应釜页的面板网格。')
print('// </auto-generated>')
print('namespace ParallelReactor.Control.Tests;')
print('')
print('internal static class ReactorMockVectors')
print('{')
print('    public const string Json = """""')
print(raw)
print('""""";')
print('}')
PY
