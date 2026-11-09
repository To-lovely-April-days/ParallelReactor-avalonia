/* gen-core-vectors.js — 用样稿 research/pc-v3 的 js/core.js + models.js + sim.js 在 JavaScriptCore 里算出期望值，
   生成 CoreMockVectors.cs（Control.Core 的 Fmt / Keep / 型号表 / 状态字对照测试读取它）。只用样稿自己的函数，不手写期望值。
   运行（macOS 自带 jsc；在 bash 下；jsc 打不开含中文的脚本路径，所以先 cd 到本目录、用相对路径）：
     JSC=/System/Library/Frameworks/JavaScriptCore.framework/Versions/A/Helpers/jsc
     cd <本目录> && $JSC gen-core-vectors.js -- /Users/huanhuanremote/Desktop/Project/八通道/research/pc-v3 > CoreMockVectors.cs
   日期按本机时区的「本地日历字段」生成（new Date(y, m, d, h, mi, s)），与时区无关。随机数用固定种子，重跑结果不变。 */
var window = this;
// jsc 把命令行参数按 Latin-1 解码：还原成 UTF-8（路径里有「八通道」）
var MOCK = arguments[0] && decodeURIComponent(escape(arguments[0]));
if (!MOCK) throw new Error('usage: jsc gen-core-vectors.js -- <research/pc-v3 dir>');
load(MOCK + '/js/core.js');
load(MOCK + '/js/models.js');
load(MOCK + '/js/sim.js');

/** 码位 → 字符（源文件里不写 \\u 转义，免得被编辑工具解码成不可见字符） */
function u(n) { return String.fromCharCode(n); }

/* ---------- 固定种子随机数（mulberry32） ---------- */
function rng(seed) {
  var a = seed >>> 0;
  return function () {
    a = (a + 0x6D2B79F5) | 0;
    var t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
var R = rng(20261109);

/* 数值在 JSON 里的写法：特殊值用字符串（JSON 表示不了 NaN / ±∞ / -0 / undefined） */
function enc(v) {
  if (v === null) return 'null';
  if (v === undefined) return 'undefined';
  if (typeof v !== 'number') return String(v);
  if (isNaN(v)) return 'NaN';
  if (v === Infinity) return 'Infinity';
  if (v === -Infinity) return '-Infinity';
  if (v === 0 && 1 / v < 0) return '-0';
  return JSON.stringify(v);   // 最短往返表示，C# double.Parse 得到同一个 double
}

/* ---------- 数值格式化 ---------- */
var vals = [null, NaN, Infinity, -Infinity, -0, 0,
  0.05, 0.15, 0.25, 0.35, 0.45, 0.5, 1.5, 2.5, -0.5, -1.5, -2.5, -0.04, -0.0004, -0.6, 0.0005, 0.00049999999999999, 0.49999999999999994,
  1.005, 1.045, 1.0049999999999999, 1.45, 2.675, 8.345, 119.85, 119.849, 119.5, 119.95, 0.3495, 0.3485, 0.349, 0.35, 3.12, 3.003, 2.997,
  4.1365, 4.137, 4.14, 5.2, 0.2, 9.995, 9.9995, 9.99949, 9.999, 10, -10, -9.9995, 10.005, 99.95, 145.0377, 199.95, 199.9, 249.95, 249.9,
  1e21, -1e21, 1.5e21, 9.999999999999999e20, 1e-7, 5e-7, 1e-6, 123456.789, 1234.5, 1500, 300.4, 300.5, 72, 71.5, 59.5, 59.49, 60, 119.4, 3599.5,
  3600, 3600.5, 86399.5, 86400, 4999, 5000, 59999, 60000, 3599999, 3600000, 7199999, 1e15, 1e16, 9007199254740992, -59.5, 0.1, 0.2 + 0.1,
  1 / 3, 2 / 3, 100 / 7, 1e300, -1e-300, 5e-324];
for (var i = 0; i < 60; i++) vals.push(Math.floor(R() * 3e6) / 1e4);                       // 0–300，4 位小数（像读数）
for (i = 0; i < 30; i++) vals.push((Math.floor(R() * 2000) - 1000) / 100);                // −10–10，2 位
for (i = 0; i < 40; i++) vals.push((Math.floor(R() * 100000) + 0.5) / Math.pow(10, 1 + Math.floor(R() * 4)));   // 正好在「…5」上（四舍五入的分界）
for (i = 0; i < 30; i++) vals.push(Math.pow(10, R() * 14 - 7) * (R() < 0.2 ? -1 : 1));    // 1e-7 – 1e7 对数均匀
for (i = 0; i < 20; i++) vals.push(Math.floor(R() * 8e6) / 1000 - 1000);                    // 秒 / 毫秒范围

var fmt = [];
vals.forEach(function (v) {
  [undefined, 0, 1, 2, 3, 4].forEach(function (d) { fmt.push(['num', enc(v), d === undefined ? -1 : d, PR.fmt.num(v, d)]); });
  fmt.push(['mpa', enc(v), -1, PR.fmt.mpa(v)]);
  fmt.push(['psi', enc(v), -1, PR.fmt.psi(v)]);
  fmt.push(['mmol', enc(v), -1, PR.fmt.mmol(v)]);
  fmt.push(['rpm', enc(v), -1, PR.fmt.rpm(v)]);
  fmt.push(['minutes', enc(v), -1, PR.fmt.minutes(v)]);
  fmt.push(['dur', enc(v), -1, PR.fmt.dur(v)]);
  fmt.push(['ago', enc(v), -1, PR.fmt.ago(v)]);
  if (v !== null) fmt.push(['str', enc(v), -1, String(v)]);   // Number → String（型号表量程文字等直接拼接数字的地方）
});

/* ---------- 日期（本地日历字段） ---------- */
var dates = [[2026, 11, 8, 10, 12, 0], [2026, 1, 1, 0, 0, 0], [2026, 12, 31, 23, 59, 59], [1970, 1, 1, 0, 0, 1], [2000, 2, 29, 12, 5, 9],
  [2099, 7, 4, 7, 7, 7], [2024, 3, 10, 9, 0, 30]];
for (i = 0; i < 25; i++) dates.push([1990 + Math.floor(R() * 80), 1 + Math.floor(R() * 12), 1 + Math.floor(R() * 28), Math.floor(R() * 24), Math.floor(R() * 60), Math.floor(R() * 60)]);
var dateRows = dates.map(function (a) {
  var d = new Date(2000, 0, 1, 0, 0, 0);
  d.setFullYear(a[0], a[1] - 1, a[2]); d.setHours(a[3], a[4], a[5], 0);
  return { y: d.getFullYear(), mo: d.getMonth() + 1, d: d.getDate(), h: d.getHours(), mi: d.getMinutes(), s: d.getSeconds(),
    time: PR.fmt.time(d), hm: PR.fmt.hm(d), date: PR.fmt.date(d), datetime: PR.fmt.datetime(d), weekday: PR.fmt.weekday(d) };
});

/* ---------- PR.nbsp / PR.keep：样稿自己产生的文字 + 正则边界 ---------- */
var corpus = [];
function add(s) { if (s != null && typeof s === 'string' && corpus.indexOf(s) < 0) corpus.push(s); }
function harvest() {
  PR.sim.devices().forEach(function (d) {
    d.alarms.forEach(function (a) { add(a.text); });
    d.events.forEach(function (e) { add(e.text); });
    d.programs.forEach(function (p) {
      add(p.name); add(p.note);
      p.steps.forEach(function (s) { try { add(PR.sim.stepText(d, s)); var c = PR.sim.condText(s, d); add(c.value); add(c.label + ' ' + c.value); } catch (e) { } });
    });
    try { var L = PR.sim.leaseInfo(d); add(L.text); add(L.short); } catch (e) { }
    try { var C = PR.sim.connInfo(d); add(C.text); add(C.short); } catch (e) { }
    try { add(PR.sim.dataText(d)); add(PR.sim.estopText(d)); } catch (e) { }
    add(PR.models.rangeNote(d.model)); add(d.model.stirNote); add(d.model.vessel.note); add(d.model.vessel.kind);
    add('只急停' + d.alias + ' · 复位在触屏'); add('只作用于' + d.alias + ' · 停止不需要控制权'); add('各釜独立' + d.model.stirKind + ' · 在反应釜上设置');
    Object.keys(PR.sim.ACTIONS).forEach(function (name) {
      d.channels.forEach(function (ch) {
        try { add(PR.sim.why(d, name, { n: ch.n, sp: 999, rpm: 99999, id: 'nope', alias: '' })); } catch (e) { }
      });
    });
    d.channels.forEach(function (ch) {
      try { var p = PR.sim.progress(d, ch); if (p) { add(p.remainText); add(p.remainShort); add(p.statusText); } } catch (e) { }
    });
  });
}
PR.sim.init('', { frozen: true }); harvest();
PR.sim.init('4', { frozen: true }); harvest();
PR.sim.init('', { frozen: true, stress: 1 }); harvest();
PR.sim.init('4', { frozen: true, stress: 2 }); harvest();
PR.sim.init('', { frozen: true });
PR.sim.applyParams({ devs: '4', freeze: '1', alarm: '1:1,2:1,3:1', estop: '2', lease: '1:lost,2:requested', offline: '3,4' }); harvest();
[
  '已用 18 min', '18  min', '18\tmin', '18' + u(0xa0) + 'min', '18' + u(0x3000) + 'min', '18' + u(0x2009) + 'min', '18' + u(0xfeff) + 'min', '18' + u(0x85) + 'min', '18' + u(0x202f) + 'min', '18\nmin',
  '18 mins', '5 s', '5 sec', '5 h', '5 hPa', '3 ℃', '3 °C', '3 °F', '3 MPa', '3 MPas', '3 psi', '3 psig', '3 rpm', '50 %', '50 %s', '20 mL', '20 ml',
  'x 5 min', '1 2 3 min', u(0x663) + ' min', u(0x661) + ' min', 'A min', '5 min 5 s 5 h', '0.350 MPa', '-5 ℃', '+5 ℃', '5. min',
  'RV1 升温', '第 3 步', '超压 3.12', '已关闭 SV11', '反应 120 min', 'a > b', 'a ≥ b', 'a  ≥ b', 'a ≥  b', '· ≤ 200 ℃', 'x · y', ' · ', '·  ·', '· · ·',
  '温度 ≥ 119.5 ℃', '压力 < 0.2 MPa', '→ 50 ℃', '等温度 < 50 ℃', 'a = b = c', 'a → b → c', '1 > 2 > 3', '· = 3', 'x ·  ≥ 3',
  'RV3 升温较慢 · 已用 18 min', 'RV11 超压 3.12 MPa ≥ 3.00 MPa · 已关闭 SV11', '≤ 200 ℃ · ≤ 4.14 MPa', '120 ℃ · 0.350 MPa · 气体 A',
  '中文 English', '中文 123', '中文 ℃', '中文  ABC', '中 A 文 B', '中文 ', ' 中文', '中文 中文', '中文' + u(0xa0) + 'A', 'H₂ 5.2 MPa', '通入 H₂ 气',
  '1 h 05 min', '报警：RV11 超压', '（RV3）升温', '程序「加氢-标准」开始', '鿿 A', '一 A', '〇 A', '𠀀 A', '', '   ', 'abc',
  'RV16 超压 2.997 MPa ≥ 2.85 MPa · 已关闭 SV16', '高压加氢测试一号 · RV1 · 反应中', '数据停在 10:11:40 · 1号机', 'ReactorLab-PR8-A · RV16',
  'a\r\nb 5 min', '5\r\nmin', '温度 >= 50', '温度≥50 ℃', '压力 ≥3.00 MPa', '速度 1500 rpm · 共用', '剩 42 min · 预计 11:09 结束'
].forEach(add);
var keep = corpus.map(function (s) { return [s, PR.nbsp(s), PR.keep(s)]; });
keep.push([null, PR.nbsp(null), PR.keep(null)]);

/* ---------- 型号表 ---------- */
var models = PR.models.keys.map(function (k) {
  var p = PR.models.get(k);
  var gas = PR.models.gasKeys(p).concat(['nope', 'A', 'B', 'N2']).map(function (g) { return [g, PR.models.gasName(p, g)]; });
  return { key: k, profile: p, installedText: PR.models.installedText(p), uninstalledRuns: PR.models.uninstalledRuns(p),
    channels: PR.models.channels(p), stirLabel: PR.models.stirLabel(p), stirShort: PR.models.stirShort(p), gasKeys: PR.models.gasKeys(p),
    gasNames: gas, rangeNote: PR.models.rangeNote(p), isInstalled: [0, 1, 4, 5, 8, 9, 16, 17].map(function (n) { return [n, PR.models.isInstalled(p, n)]; }),
    pDec: PR.sim.pDec({ model: p }) };
});
var rangeText = [[[1, 2, 3, 4], null], [[1, 2, 5], null], [[], null], [[3, 1, 2, 2], null], [[1, 3, 5, 7], null], [[5, 6, 7, 8, 1], 'RV'],
  [[16], null], [[1, 2, 3, 4, 9, 10, 11, 16], null], [[2, 1], 'SV'], [[1, 2], '']].map(function (a) { return { list: a[0], prefix: a[1], out: PR.models.rangeText(a[0], a[1] == null ? undefined : a[1]) }; });

/* ---------- 状态键 ---------- */
var states = Object.keys(PR.ST).map(function (k) { return { key: k, zh: PR.ST[k].zh, kind: PR.ST[k].kind, weight: PR.ST[k].weight }; });

var out = { source: 'research/pc-v3 js/core.js + js/models.js + js/sim.js', fmt: fmt, dates: dateRows, keep: keep, models: models, rangeText: rangeText,
  states: states, running: PR.ST.RUNNING, sampleNote: PR.models.SAMPLE_NOTE, fmtPsi: PR.fmt.PSI, dash: PR.fmt.DASH };

/* 不可见 / 易混的字符一律写成 \uXXXX（源文件里看得见）；中文、℃、≥ 等照原样 */
function hidden(n) {
  return (n >= 0x7f && n <= 0xa0) || n === 0xad || (n >= 0x600 && n <= 0x6ff) || n === 0x1680 || n === 0x180e || (n >= 0x2000 && n <= 0x200f) ||
    (n >= 0x2028 && n <= 0x202f) || (n >= 0x205f && n <= 0x206f) || n === 0x3000 || n === 0xfeff || (n >= 0xfff0 && n <= 0xffff);
}
var raw = JSON.stringify(out), json = '';
for (var ci = 0; ci < raw.length; ci++) {
  var cc = raw.charCodeAt(ci);
  json += hidden(cc) ? String.fromCharCode(92) + 'u' + ('0000' + cc.toString(16)).slice(-4) : raw.charAt(ci);
}
if (json.indexOf('""""') >= 0) throw new Error('raw string delimiter clash');
print('// <auto-generated>');
print('// 由 MockVectors/gen-core-vectors.js 用样稿 research/pc-v3 的 js/core.js + js/models.js + js/sim.js（JavaScriptCore）生成，不要手改；');
print('// 样稿改了就重新生成（命令见脚本开头）。数值的特殊值写成字符串：null / undefined / NaN / Infinity / -Infinity / -0。');
print('// </auto-generated>');
print('namespace ParallelReactor.Control.Tests;');
print('');
print('internal static class CoreMockVectors');
print('{');
print('    public const string Json = """""');
print(json);
print('""""";');
print('}');
