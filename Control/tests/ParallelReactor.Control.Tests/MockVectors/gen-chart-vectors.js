/* gen-chart-vectors.js — 用样稿 research/pc-v3 的 js/core.js + models.js + sim.js + ui.js 在 JavaScriptCore 里算出曲线的期望值，
   生成 ChartMockVectors.cs（Core.Chart 的 ChartTicks / ChartBands / ChartLayout / ReactorChartOptions 的对照测试 ChartTicksTests 读取它）。
   只用样稿自己的函数（niceTicks / fitTicks / chartBands / reactorChartOpts / lineChart 的 draw() 与 onMove），不手写期望值：
   lineChart 画进一个假的容器（只有 clientWidth / clientHeight / innerHTML），输出的 SVG 原样记下，C# 按同样的写法拼出来逐项比较；
   悬停提示用假的 SVG / 提示框元素接住 onMove 写进去的内容。文字宽度（参考线 / 标记文字）换成固定的估计（ASCII 8.4 px、其它 14 px），
   C# 测试用同一个估计（ChartLayout.EstimateWidth）。
   运行（macOS 自带 jsc；在 bash 下；jsc 打不开含中文的脚本路径，所以先 cd 到本目录、用相对路径）：
     JSC=/System/Library/Frameworks/JavaScriptCore.framework/Versions/A/Helpers/jsc
     cd <本目录> && $JSC gen-chart-vectors.js -- /Users/huanhuanremote/Desktop/Project/八通道/research/pc-v3 > ChartMockVectors.cs
   时间写成「相对 T0（2026-11-08 10:12:00 本地）的毫秒数」；横轴刻度文字（本地 HH:MM）在整小时时区里与时区无关。随机数用固定种子，重跑结果不变。 */
var window = this;
var setInterval = function () { return 1; }, clearInterval = function () { };
var MOCK = arguments[0] && decodeURIComponent(escape(arguments[0]));
if (!MOCK) throw new Error('usage: jsc gen-chart-vectors.js -- <research/pc-v3 dir>');
load(MOCK + '/js/core.js');
load(MOCK + '/js/models.js');
load(MOCK + '/js/sim.js');
load(MOCK + '/js/ui.js');

function rng(seed) {
  var a = seed >>> 0;
  return function () {
    a = (a + 0x6D2B79F5) | 0;
    var t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
var R = rng(20261111);
function enc(v) {
  if (v === null) return 'null';
  if (v === undefined) return 'undefined';
  if (typeof v !== 'number') return String(v);
  if (isNaN(v)) return 'NaN';
  if (v === Infinity) return 'Infinity';
  if (v === -Infinity) return '-Infinity';
  if (v === 0 && 1 / v < 0) return '-0';
  return JSON.stringify(v);
}
function fnv(lines) {
  var h = 0x811c9dc5;
  for (var i = 0; i < lines.length; i++) {
    var s = lines[i] + '\n';
    for (var j = 0; j < s.length; j++) { h ^= s.charCodeAt(j); h = Math.imul(h, 0x01000193) >>> 0; }
  }
  return ('00000000' + h.toString(16)).slice(-8);
}
var T0ms = +PR.sim.T0;
/** 曲线点的摘要行：相对 T0 的毫秒 | 值（String(v)；null → -） */
function ptLine(p) { return String(p[0] - T0ms) + '|' + (p[1] == null ? '-' : String(p[1])); }

/* 文字宽度：固定估计（C# ChartLayout.EstimateWidth 同） */
PR.ui.textWidth = function (text) {
  var w = 0, s = String(text);
  for (var i = 0; i < s.length; i++) w += s.charCodeAt(i) < 0x80 ? 8.4 : 14;
  return w;
};

/* ---------- niceTicks / fitTicks ---------- */
var nice = [], fit = [];
var spans = [[0, 1], [0, 0], [5, 5], [-3, -3], [0, 1e-10], [119.4, 120.6], [24.8, 25.3], [0, 150], [20, 150], [0.005, 0.35], [0.33, 0.36], [3.1, 5.2],
  [0, 4.14], [-10, 10], [-0.004, 0.006], [1e-5, 3e-5], [100, 100.000001], [0, 1000], [1234.5, 1567.8], [0.1, 0.30000000000000004],
  [NaN, 5], [5, NaN], [-Infinity, 1], [0, Infinity], [7, 3], [0, 0.05], [0, 0.2], [2.9, 3.3], [46.7, 120.2], [0, 9.999], [99.5, 100.5]];
for (var i = 0; i < 60; i++) { var a = (R() - 0.3) * Math.pow(10, Math.floor(R() * 6) - 2); spans.push([a, a + R() * Math.pow(10, Math.floor(R() * 5) - 2)]); }
for (i = 0; i < 30; i++) { var b = Math.floor(R() * 3000) / 10; spans.push([b, b + Math.floor(R() * 600) / 10]); }
for (i = 0; i < 30; i++) { var c = Math.floor(R() * 5000) / 1000; spans.push([c, c + Math.floor(R() * 2000) / 1000]); }
function tk(t) { return { lo: enc(t.lo), hi: enc(t.hi), step: enc(t.step), ticks: t.ticks.map(enc), dec: t.dec }; }
spans.forEach(function (s) {
  [1, 2, 3, 4, 5, 6, 7, 0, 2.5].forEach(function (n) { nice.push([enc(s[0]), enc(s[1]), enc(n), tk(PR.ui.niceTicks(s[0], s[1], n))]); });
  [[200, 5, undefined], [120, 5, undefined], [60, 4, undefined], [30, 6, undefined], [264, 6, 30], [100, 3, 48], [10, 5, undefined], [0, 2, undefined], [-5, 4, undefined]].forEach(function (f) {
    fit.push([enc(s[0]), enc(s[1]), enc(f[0]), f[1], enc(f[2]), tk(PR.ui.fitTicks(s[0], s[1], f[0], f[1], f[2]))]);
  });
});

/* ---------- chartBands ---------- */
var bands = [];
function vals(n, base, amp) { var o = []; for (var i = 0; i < n; i++) o.push(R() < 0.06 ? null : base + (R() - 0.5) * 2 * amp); return o; }
var bandCases = [];
[[0, 0, 0], [1, 0, 0], [0, 1, 0]].forEach(function () { });
var tSets = [[], [null], [25], [25, 25.2], vals(40, 120, 0.3), vals(40, 120, 8), vals(40, 90, 30), vals(20, 30, 2), vals(30, 200, 0.2), vals(10, 0.5, 0.3), vals(25, 40, 15)];
var pSets = [[], [null], [0], [0.005, 0.006], vals(40, 0.35, 0.002), vals(40, 3.1, 0.05), vals(40, 1.2, 0.6), vals(20, 5.2, 0.01), vals(30, 0.01, 0.005), vals(30, 0.05, 0.04), vals(25, 2.997, 0.002), vals(25, 0.2, 0.15)];
tSets.forEach(function (tv, ti) {
  pSets.forEach(function (pv, pi) {
    [[4.14, null, 3], [3, 3, 3], [10, null, 2], [10, 10, 2], [0, null, undefined], [5.2, 5.2, 2]].forEach(function (o) {
      if ((ti + pi + o[0]) % 3 === 1 && !(ti < 2 || pi < 2)) return;   // 组合太多：稀疏抽样（边界组合全留）
      var opts = { series: [{ axis: 0, points: tv.map(function (v, i) { return [i, v]; }) }, { axis: 1, points: pv.map(function (v, i) { return [i, v]; }) }],
        yAxes: [{ label: 'T' }, o[2] === undefined ? {} : { dec: o[2] }] };
      PR.ui.chartBands(opts, { pMax: o[0], limit: o[1] });
      bands.push({ t: ti, p: pi, pMax: enc(o[0]), limit: enc(o[1]), dec: enc(o[2]),
        a0: [enc(opts.yAxes[0].min), enc(opts.yAxes[0].max), enc(opts.yAxes[0].maxTicks)], a1: [enc(opts.yAxes[1].min), enc(opts.yAxes[1].max), enc(opts.yAxes[1].maxTicks)] });
    });
  });
});
// 只有一条轴、没有压力系列、轴上已有 min / max
(function () {
  var o1 = { series: [{ points: [[0, 50], [1, 52]] }], yAxes: [{ min: 0, max: 10 }] };
  PR.ui.chartBands(o1, { pMax: 4 });
  bands.push({ t: -1, p: -1, pMax: '4', limit: 'null', dec: 'undefined', one: true, a0: [enc(o1.yAxes[0].min), enc(o1.yAxes[0].max), enc(o1.yAxes[0].maxTicks)], a1: null });
})();

/* ---------- lineChart：画进假容器，记下 SVG；再用假元素接住悬停 ---------- */
function fakeContainer(w, h) {
  var handlers = {};
  var tip = { style: {}, innerHTML: '', offsetWidth: 0, offsetHeight: 0 };
  var crossLine = { attrs: {}, setAttribute: function (k, v) { this.attrs[k] = v; } };
  var circles = [];
  var cross = { style: {}, querySelector: function () { return crossLine; }, querySelectorAll: function () { return circles.slice(); },
    appendChild: function (c) { circles.push(c); } };
  var svg = { getBoundingClientRect: function () { return { left: 0, top: 0, width: c.svgW || w, height: h }; }, querySelector: function () { return cross; } };
  var c = {
    clientWidth: w, clientHeight: h, classList: { add: function () { } }, innerHTML: '',
    addEventListener: function (k, f) { handlers[k] = f; }, removeEventListener: function () { },
    querySelector: function (sel) { return sel === '.pr-chart-svg' ? svg : sel === '.pr-chart-tip' ? tip : sel === '.pr-chart-cross' ? cross : null; },
    _h: handlers, _tip: tip, _line: crossLine, _circles: circles
  };
  return c;
}
// PR.$$('circle', cross) 用 querySelectorAll；document.createElementNS 给一个记属性的对象
var document = { createElementNS: function () { return { attrs: {}, setAttribute: function (k, v) { this.attrs[k] = v; }, remove: function () { } }; } };
window.document = document;
function svgOf(c) {
  var s = c.innerHTML;
  s = s.slice(0, s.indexOf('</svg>') + 6);
  // 长路径只记摘要：d="#<fnv>:<长度>"（C# 按同样的写法拼出路径再算摘要）
  return s.replace(/ d="([^"]*)"/g, function (m, d) { return d.length > 400 ? ' d="#' + fnv([d]) + ':' + d.length + '"' : m; });
}
function hoverAt(c, x, y) {
  c._circles.length = 0;
  c._tip.innerHTML = ''; c._tip.style = {}; c._line.attrs = {};
  c._tip.offsetWidth = 196; c._tip.offsetHeight = 104;   // 提示框的假尺寸（C# 用同样的数）
  c._h.mousemove({ clientX: x, clientY: y });
  var cr = c.querySelector('.pr-chart-svg').querySelector('.pr-chart-cross');
  return { x: x, y: y, shown: c._tip.style.display === 'block', cross: c._line.attrs.x1 || null, tip: c._tip.innerHTML,
    left: c._tip.style.left || null, top: c._tip.style.top || null,
    dots: c._circles.map(function (k) { return [k.attrs.cx, k.attrs.cy, k.attrs.stroke]; }) };
}
/** 选项 → 可存的写法（点的时刻相对 T0；颜色原样：十六进制，C# 把令牌名换成十六进制再比） */
function optsJson(o) {
  var out = {};
  Object.keys(o).forEach(function (k) {
    if (k === 'series') out.series = o.series.map(function (s) {
      var r = {};
      Object.keys(s).forEach(function (q) { if (q !== 'points') r[q] = s[q]; });
      r.points = (s.points || []).map(function (p) { return [p[0] - T0ms, enc(p[1])]; });
      return r;
    });
    else if (k === 'markers') out.markers = o.markers.map(function (m) { var r = {}; Object.keys(m).forEach(function (q) { r[q] = q === 't' ? m.t - T0ms : m[q]; }); return r; });
    else if (k === 'xMin' || k === 'xMax') out[k] = o[k] == null ? o[k] : o[k] - T0ms;
    else if (k === 'refLines') out.refLines = o.refLines.map(function (r) { var q = {}; Object.keys(r).forEach(function (z) { q[z] = z === 'v' ? enc(r.v) : r[z]; }); return q; });
    else out[k] = o[k];
  });
  return out;
}
var layouts = [];
function layoutCase(name, opts, w, h, hovers, ref) {
  var c = fakeContainer(w, h);
  PR.ui.lineChart(c, opts);
  var row = { name: name, w: w, h: h, opts: ref ? null : optsJson(opts), ref: ref || null, svg: svgOf(c), hover: [] };
  (hovers || []).forEach(function (pt) { row.hover.push(hoverAt(c, pt[0], pt[1])); });
  layouts.push(row);
}
function walk(n, t0, dt, v0, amp, nullEvery) {
  var pts = [], v = v0;
  for (var i = 0; i < n; i++) {
    v += (R() - 0.5) * amp;
    pts.push([t0 + i * dt, nullEvery && i % nullEvery === nullEvery - 1 ? null : +v.toFixed(4)]);
  }
  return pts;
}
var M = 60000;
// 合成的选项：各个分支
(function () {
  var base = T0ms;
  var s1 = walk(120, base - 60 * M, 30000, 80, 3), s2 = walk(120, base - 60 * M, 30000, 1.2, 0.05, 17), s3 = walk(60, base - 30 * M, 30000, 60, 1);
  layoutCase('auto-colors', { series: [{ name: 'A', points: s1, unit: '℃' }, { name: 'B', points: s3, dash: '7 4' }, { name: 'C', points: [[base, null]] }, { name: 'D', points: walk(40, base - 40 * M, 60000, 50, 2), width: 1.8, dash: '2 3' }],
    yAxes: [{ label: '温度 ℃', unit: '℃' }] }, 760, 300, [[400, 150], [60, 100], [740, 200], [10, 10], [700, 40]]);
  layoutCase('two-axes', { series: [{ name: '温度', points: s1, axis: 0, width: 2.5, unit: '℃', dec: 1 }, { name: '压力', points: s2, axis: 1, width: 2.5, unit: 'MPa', dec: 3 }],
    yAxes: [{ label: '温度 ℃', unit: '℃', minSpan: 20 }, { label: '压力 MPa', unit: 'MPa', dec: 3, minSpan: 0.5, color: PR.tok('--ch-p') }],
    refLines: [{ axis: 1, v: 1.3, color: PR.tok('--ch-limit'), labelColor: PR.tok('--r-ink'), label: '上限 1.300 MPa' }, { axis: 0, v: 85, label: 'SP 85', dash: false }, { axis: 0, v: 300, fit: false, label: '远处' }],
    markers: [{ t: base - 50 * M, label: 'RV1 开始反应' }, { t: base - 2 * M, label: '靠右的标记文字' }, { t: base - 20 * M, color: PR.tok('--ch-s2') }, { t: base + 5 * M, label: '窗口外' }],
    xMin: base - 60 * M, xMax: base, emptyText: '暂无数据' }, 900, 360, [[450, 200], [880, 120], [100, 300], [55, 40]]);
  layoutCase('fixed-axis', { series: [{ name: 'P', points: s2, unit: 'MPa' }], yAxes: [{ label: 'P', unit: 'MPa', min: 0, max: 2 }], xLabel: '时间', xMin: base - 60 * M, xMax: base }, 520, 260, [[260, 120]]);
  layoutCase('fixed-maxticks', { series: [{ name: 'P', points: s2 }], yAxes: [{ min: 0.9, max: 1.5, maxTicks: 3, dec: 2 }], xMin: base - 60 * M, xMax: base }, 520, 260);
  layoutCase('min-only', { series: [{ name: 'T', points: s1 }], yAxes: [{ min: 0, minSpan: 500, unit: '℃' }, { max: 0.01, unit: 'MPa' }], xMin: base - 60 * M, xMax: base }, 640, 240);
  layoutCase('max-span-clamp', { series: [{ name: 'T', points: s3 }], yAxes: [{ max: 61, minSpan: 30 }], xMin: base - 30 * M, xMax: base }, 640, 240);
  layoutCase('empty', { series: [{ name: 'X', points: [[base - M, null], [base, NaN]] }], yAxes: [{ label: '温度 ℃', unit: '℃' }, { label: '压力 MPa', unit: 'MPa', dec: 3 }], xMin: base - 30 * M, xMax: base, emptyText: '离线 · 暂无数据' }, 700, 300, [[300, 150]]);
  layoutCase('empty-default', { series: [], yAxes: [], xMin: base - 10 * M, xMax: base }, 300, 160);
  layoutCase('no-xrange', { series: [{ name: 'T', points: s3 }, { name: 'U', points: walk(30, base - 10 * M, 10000, 61, 0.5) }], yAxes: [{ unit: '℃' }] }, 600, 280, [[300, 140], [590, 100]]);
  layoutCase('x1-le-x0', { series: [{ name: 'T', points: [[base, 20]] }], yAxes: [{}], xMin: base, xMax: base - M }, 400, 200, [[200, 100]]);
  layoutCase('outside-window', { series: [{ name: 'T', points: walk(200, base - 120 * M, 36000, 40, 2) }], yAxes: [{ unit: '℃' }], xMin: base - 30 * M, xMax: base - 5 * M }, 820, 330, [[400, 160], [810, 100]]);
  layoutCase('tiny', { series: [{ name: 'T', points: s1 }], yAxes: [{}] }, 39, 200);
  layoutCase('short', { series: [{ name: 'T', points: s1 }], yAxes: [{}], xMin: base - 60 * M, xMax: base }, 300, 39);
  layoutCase('low-height', { series: [{ name: 'T', points: s1 }, { name: 'P', points: s2, axis: 1 }], yAxes: [{ unit: '℃' }, { unit: 'MPa' }], xMin: base - 60 * M, xMax: base }, 500, 120, [[250, 60]]);
  layoutCase('long-span', { series: [{ name: 'T', points: walk(200, base - 6 * 60 * M, 108000, 40, 2) }], yAxes: [{ unit: '℃' }], xMin: base - 6 * 60 * M, xMax: base }, 700, 260);
  layoutCase('narrow-time', { series: [{ name: 'T', points: s1 }], yAxes: [{ unit: '℃' }], xMin: base - 60 * M, xMax: base }, 180, 200);
  layoutCase('ref-crossed', { series: [{ name: 'A', points: [[base - 60 * M, 2.9], [base - 30 * M, 3.05], [base, 2.98]], axis: 1, width: 2.5 }, { name: 'B', points: [[base - 60 * M, 20], [base, 25]], width: 2.5 }],
    yAxes: [{ unit: '℃', min: 0, max: 30, maxTicks: 5 }, { unit: 'MPa', dec: 3, min: 0, max: 3.3, maxTicks: 5 }],
    refLines: [{ axis: 1, v: 3, color: PR.tok('--ch-limit'), label: '上限 3.000 MPa' }], xMin: base - 60 * M, xMax: base }, 640, 260);
  layoutCase('ref-out', { series: [{ name: 'A', points: s2, axis: 0 }], yAxes: [{ min: 0, max: 2 }], refLines: [{ v: 2.5, label: '上' }, { v: -1, label: '下' }, { v: NaN, label: 'NaN' }, { v: 1, label: '' }], xMin: base - 60 * M, xMax: base }, 640, 260);
})();

/* ---------- reactorChartOpts：演示数据（同 DemoFleet 的参数） ---------- */
var reactor = [], reactorMeta = null;
function sceneOpts(q) {
  var p = {};
  q.split('&').forEach(function (kv) { if (!kv) return; var i = kv.indexOf('='); p[kv.slice(0, i)] = kv.slice(i + 1); });
  return p;
}
var scenes = ['freeze=1', 'freeze=1&stress=1', 'freeze=1&devs=4&stress=2', 'freeze=1&alarm=1:1,2:1', 'freeze=1&offline=none', 'freeze=1&devs=PR8,PR4,PR16&offline=none', 'freeze=1&estop=1,3', 'tick'];
scenes.forEach(function (q) {
  var ticks = 0;
  if (q === 'tick') { q = 'devs=4'; ticks = 95; }
  var p = sceneOpts(q);
  PR.sim.init(p.devs || '', { frozen: p.freeze === '1', stress: p.stress });
  PR.sim.applyParams(p);
  Math.random = rng(20261109);
  for (var t = 0; t < ticks; t++) PR.sim.tick();
  PR.sim.devices().forEach(function (d) {
    d.channels.forEach(function (ch) {
      if (!ch.installed) return;
      [10, 30, 60].concat(q === 'freeze=1' ? [0] : []).forEach(function (min) {
        var o = PR.ui.reactorChartOpts(d, ch, { minutes: min || undefined });
        var raw = PR.ui.reactorChartOpts(d, ch, { minutes: min || undefined, bands: false });
        // 每行只记会变的：各系列的点数 / 摘要 / 首尾点、两条轴的 min / max / maxTicks（分带后与不分带）、上限线、标记、横轴范围、空文字；
        // 不变的（系列名称、颜色、线宽、虚线、单位、小数位，轴标题）只记一次（reactorMeta）
        if (!reactorMeta) reactorMeta = {
          series: o.series.map(function (s) { return { name: s.name, color: s.color, axis: s.axis, width: s.width, dash: s.dash, unit: s.unit, dec: s.dec, noEnd: !!s.noEnd }; }),
          yAxes: o.yAxes.map(function (a) { return { label: a.label, unit: a.unit }; })
        };
        reactor.push({
          q: q, ticks: ticks, k: d.k, n: ch.n, min: min,
          s: o.series.map(function (s) { return [s.points.length, fnv(s.points.map(ptLine)), s.points.length ? ptLine(s.points[0]) : null, s.points.length ? ptLine(s.points[s.points.length - 1]) : null]; }),
          a: o.yAxes.map(function (a) { return [enc(a.min), enc(a.max), enc(a.maxTicks), enc(a.dec), enc(a.minSpan)]; }),
          raw: raw.yAxes.map(function (a) { return [enc(a.min), enc(a.max), enc(a.maxTicks)]; }),
          ref: o.refLines.map(function (r) { return [r.axis, enc(r.v), r.color, r.labelColor, r.label]; }),
          mk: o.markers.map(function (m) { return [m.t - T0ms, m.label, m.color]; }),
          x: [o.xMin - T0ms, o.xMax - T0ms], e: o.emptyText
        });
      });
    });
  });
  // 完整的版面：几个代表性的反应釜 × 几个尺寸（分带 + 上限线 + 标记都在里面）
  if (q === 'freeze=1' || q === 'freeze=1&stress=1' || q === 'freeze=1&alarm=1:1,2:1' || q === 'devs=4') {
    [[1, 1], [1, 3], [1, 4], [2, 1], [2, 4], [3, 11], [3, 15], [3, 2]].forEach(function (kn, ki) {
      var d = PR.sim.dev(kn[0]), ch = d && PR.sim.ch(d, kn[1]);
      if (!d || !ch || !ch.installed) return;
      [[784, 330, 30], [1206, 389, 60], [560, 250, 10], [432, 214, 30]].forEach(function (sz, si) {
        if ((ki + si) % 2 === 1) return;   // 每个反应釜两种尺寸（交错），控制向量的大小
        layoutCase('rv ' + q + ' ' + kn.join('/') + ' ' + sz[2], PR.ui.reactorChartOpts(d, ch, { minutes: sz[2] }), sz[0], sz[1], [[sz[0] / 2, sz[1] / 2], [sz[0] - 30, 60], [70, sz[1] - 40]],
          { q: q, ticks: ticks, k: kn[0], n: kn[1], min: sz[2] });
      });
    });
  }
});
// 组件陈列「曲线」的第三张图（multi）：默认 3 台，冻结
(function () {
  PR.sim.init('', { frozen: true });
  PR.sim.applyParams({ freeze: '1' });
  var d1 = PR.sim.dev(1), ss = [];
  [[d1, 1], [d1, 2], [d1, 3]].forEach(function (v, i) { if (v[0] && PR.sim.ch(v[0], v[1])) ss.push({ name: v[0].alias + ' · RV' + v[1], points: PR.sim.series(v[0], v[1], 'tempC', 60), unit: '℃', dec: 1, width: 1.8, dash: ['', '7 4', '2 3'][i] || undefined }); });
  layoutCase('gallery-multi', { series: ss, yAxes: [{ label: '温度 ℃', unit: '℃' }], emptyText: '暂无数据' }, 588, 260, [[300, 120]]);
})();

var out = { source: 'research/pc-v3 js/core.js + js/models.js + js/sim.js + js/ui.js', palette: PR.PALETTE, nice: nice, fit: fit,
  bandSets: { t: tSets.map(function (a) { return a.map(enc); }), p: pSets.map(function (a) { return a.map(enc); }) }, bands: bands, layouts: layouts, reactorMeta: reactorMeta, reactor: reactor };
var raw = JSON.stringify(out);
if (raw.indexOf('"""""') >= 0) throw new Error('raw string delimiter clash');
print('// <auto-generated>');
print('// 由 MockVectors/gen-chart-vectors.js 用样稿 research/pc-v3 的 js/core.js + js/models.js + js/sim.js + js/ui.js（JavaScriptCore）生成，不要手改；');
print('// 样稿改了就重新生成（命令见脚本开头）。nice / fit：刻度；bands：分带；layouts：lineChart 画出的 SVG 与悬停；reactor：reactorChartOpts。');
print('// </auto-generated>');
print('namespace ParallelReactor.Control.Tests;');
print('');
print('internal static class ChartMockVectors');
print('{');
print('    public const string Json = """""');
print(raw);
print('""""";');
print('}');
