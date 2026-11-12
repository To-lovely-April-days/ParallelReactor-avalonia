/* gen-demo-vectors.js — 用样稿 research/pc-v3 的 js/core.js + models.js + sim.js + ui.js 在 JavaScriptCore 里跑演示数据，
   生成 DemoMockVectors.cs：每个场景 = 初始参数 + 一段操作脚本（推进 / 操作 / 添加 / 编辑 / 改参数），以及脚本跑完后整组设备的规范文字摘要
   （设备、连接、控制权、急停、会话、气路、程序、反应釜、历史的 FNV 摘要、报警、事件、模块通讯，以及 leaseInfo / connInfo / counts / stateText /
   progress / progText / segments / stepBar / 步骤卡标记 / 步骤文字 / why 守卫的结果）。C# 的 DemoFleetTests 用同样的参数和脚本跑 Core.Demo.DemoFleet，
   逐行比较。只用样稿自己的函数，不手写期望值；样稿每秒推进里的 Math.random 换成种子 20261109 的 mulberry32（C# 用同一个种子）。
   运行（macOS 自带 jsc；在 bash 下；jsc 打不开含中文的脚本路径，所以先 cd 到本目录、用相对路径）：
     JSC=/System/Library/Frameworks/JavaScriptCore.framework/Versions/A/Helpers/jsc
     cd <本目录> && $JSC gen-demo-vectors.js -- /Users/huanhuanremote/Desktop/Project/八通道/research/pc-v3 > DemoMockVectors.cs
   时间一律写成「相对 T0（2026-11-08 10:12:00 本地）的毫秒数」，与时区无关。 */
var window = this;
var setInterval = function () { return 1; }, clearInterval = function () { };   // sim.start()：jsc 没有定时器，推进由脚本调 tick()
var MOCK = arguments[0] && decodeURIComponent(escape(arguments[0]));
if (!MOCK) throw new Error('usage: jsc gen-demo-vectors.js -- <research/pc-v3 dir>');
load(MOCK + '/js/core.js');
load(MOCK + '/js/models.js');
load(MOCK + '/js/sim.js');
load(MOCK + '/js/ui.js');

var NOISE_SEED = 20261109;
function rng(seed) {
  var a = seed >>> 0;
  return function () {
    a = (a + 0x6D2B79F5) | 0;
    var t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

var T0ms = +PR.sim.T0;
function S(v) {
  if (v === null || v === undefined) return '-';
  if (typeof v === 'boolean') return v ? '1' : '0';
  return String(v);
}
function T(ms) { return ms == null || ms === 0 ? '-' : String(ms - T0ms); }
function fnv(lines) {
  var h = 0x811c9dc5;
  for (var i = 0; i < lines.length; i++) {
    var s = lines[i] + '\n';
    for (var j = 0; j < s.length; j++) { h ^= s.charCodeAt(j); h = Math.imul(h, 0x01000193) >>> 0; }
  }
  return ('00000000' + h.toString(16)).slice(-8);
}
function strip(html) { return String(html).replace(/<[^>]*>/g, '').replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#39;/g, "'"); }
function sampleLine(s) { return [T(s.t), S(s.tempC), S(s.pressureMPa), S(s.heaterPct), S(s.rpm), S(s.mmol), S(s.spC), S(s.pTargetMPa)].join('|'); }
function payloadText(p) {
  if (!p) return '{}';
  var parts = [];
  ['n', 'sp', 'rpm', 'id', 'alias', 'text'].forEach(function (k) { if (p[k] !== undefined) parts.push(k + '=' + S(p[k])); });
  return '{' + parts.join(',') + '}';
}

/** 连续相同的项合并成「项*次数」（守卫表里同一个原因常连着出现十几次） */
function rle(items) {
  var out = [];
  for (var i = 0; i < items.length;) {
    var j = i;
    while (j < items.length && items[j] === items[i]) j++;
    out.push(j - i > 1 ? items[i] + '*' + (j - i) : items[i]);
    i = j;
  }
  return out.join('|');
}

/* ---------- 规范文字摘要（C# DemoDump 逐行照写） ---------- */
var CH_ACTIONS = ['enable', 'disable', 'start', 'stop', 'heatStart', 'heatStop', 'setSp', 'progLoad', 'progRun', 'progAbort', 'chStirOn', 'chStirOff', 'chStirSet'];
function chWhy2(n) {
  return [['heatStart', { n: n, sp: 999 }], ['heatStart', { n: n }], ['setSp', { n: n, sp: 999 }], ['setSp', { n: n, sp: -1 }], ['setSp', { n: n }],
    ['chStirSet', { n: n, rpm: 99999 }], ['chStirSet', { n: n }], ['progLoad', { n: n, id: 'nope' }], ['progLoad', { n: n }], ['chStirOn', { n: n, rpm: 900 }]];
}
function devWhy(d) {
  var list = [['stirOn', {}], ['stirOff', {}], ['stirSet', { rpm: 600 }], ['stirSet', { rpm: 99999 }], ['stirSet', {}], ['runAll', {}], ['stopAll', {}], ['estop', {}],
    ['sessionStart', {}], ['sessionMark', { text: 'x' }], ['sessionEnd', {}], ['leaseRequest', {}], ['leaseCancel', {}], ['leaseRelease', {}], ['disconnect', {}], ['reconnect', {}],
    ['rename', { alias: d.alias }], ['rename', { alias: '' }], ['rename', { alias: '   ' }], ['rename', { alias: ' 新名字 ' }], ['rename', { alias: '一二三四五六七八九' }],
    ['rename', { alias: '一二三四五六七八' }], ['rename', { alias: 'ABCDEFGHIJKLMNOPQ' }], ['rename', { alias: 'ABCDEFGHIJKLMNOP' }], ['rename', { alias: '1号机' }], ['rename', { alias: '2号机' }],
    ['remove', {}], ['enable', {}], ['enable', { n: 99 }], ['foo', {}], ['alarmAck', {}], ['alarmAck', { id: 'nope' }]];
  d.alarms.forEach(function (a) { list.push(['alarmAck', { id: a.id }]); });
  return list;
}
function dumpDevice(d, L, brief) {
  var c = d.conn, l = d.lease;
  L.push('dev ' + d.k + ' ' + d.id + ' ' + d.alias + ' ' + d.modelKey + ' ' + d.host + ':' + d.port + ' ' + d.token + ' ' + d.sn + ' ' + d.hmiVersion + ' ' + d.apiVersion + ' auto=' + S(d.autoConnect));
  L.push(' conn ' + c.state + ' since=' + T(c.since) + ' lost=' + T(c.lostAt) + ' retry=' + c.retryS + ' connS=' + c.connectS + ' forced=' + S(c.forced) + ' att=' + c.attempts + ' ever=' + S(c.everOnline));
  L.push(' lease ' + l.state + ' holder=' + l.holder + ' remain=' + l.remainS + ' grace=' + l.graceS + ' auto=' + T(l.autoAt) + ' since=' + T(l.since));
  L.push(' estop ' + S(d.estop.active) + ' at=' + T(d.estop.at) + ' by=' + S(d.estop.by));
  L.push(' session ' + (d.session ? S(d.session.active) + ' ' + d.session.id + ' start=' + T(d.session.startAt) + ' end=' + T(d.session.endAt) + ' ' +
    d.session.markers.map(function (m) { return T(m.at) + '|' + S(m.n) + '|' + m.text; }).join(';') : '-'));
  L.push(' sessions ' + d.sessions.map(function (s) { return s.id + '@' + T(s.startAt) + '~' + T(s.endAt) + '#' + s.markers.length; }).join(','));
  L.push(' stir ' + (d.stir ? S(d.stir.on) + ' ' + S(d.stir.rpm) : '-'));
  L.push(' gas ' + Object.keys(d.gas).map(function (k) { return k + '=' + S(d.gas[k]); }).join(','));
  L.push(' programs ' + d.programs.map(function (p) { return p.id + ':' + p.name + ':' + p.steps.length; }).join(','));
  L.push(' dataAt ' + T(d.dataAt));
  d.channels.forEach(function (ch) {
    L.push(' ch ' + ch.n + ' inst=' + S(ch.installed) + ' st=' + ch.state + ' t=' + S(ch.tempC) + ' sp=' + S(ch.spC) + ' p=' + S(ch.pressureMPa) + ' pt=' + S(ch.pTargetMPa) +
      ' h=' + S(ch.heaterPct) + ' heating=' + S(ch.heating) + ' cooling=' + S(ch.cooling) + ' mmol=' + S(ch.mmol) + ' vol=' + S(ch.volumeMl) + ' sv=' + S(ch.sv) +
      ' stirOn=' + S(ch.stirOn) + ' rpm=' + S(ch.rpm) + ' hr=' + S(ch.heatRate) + ' pr=' + S(ch.pRate) + ' alarm=' + S(ch.alarm));
    var p = ch.program;
    if (p) L.push(' prog ' + ch.n + ' ' + p.id + ' ' + p.name + ' note=' + S(p.note) + ' idx=' + p.stepIndex + ' ' + p.status + ' start=' + T(p.startedAt) + ' stepStart=' + T(p.stepStartedAt) +
      ' dur=' + S(p.stepDurS) + ' end=' + T(p.endedAt) + ' log=' + p.log.map(function (x) { return x.i + ':' + T(x.t0) + ':' + T(x.t1); }).join(','));
    var h = d.history[ch.n] || [];
    L.push(' hist ' + ch.n + ' ' + h.length + ' ' + fnv(h.map(sampleLine)) + (h.length ? ' first=' + sampleLine(h[0]) + ' last=' + sampleLine(h[h.length - 1]) : ''));
  });
  d.alarms.forEach(function (a) {
    L.push(' alarm ' + a.id + ' ' + a.level + ' ch=' + S(a.ch) + ' ' + a.code + ' at=' + T(a.at) + ' acked=' + S(a.acked) + ' by=' + S(a.ackBy) + ' demo=' + S(!!a.demo) + ' ' + a.text);
  });
  d.events.forEach(function (e) { L.push(' event ' + e.id + ' ' + T(e.at) + ' ' + e.level + ' n=' + S(e.n) + ' ' + e.text); });
  d.comm.forEach(function (m) { L.push(' comm ' + m.key + ' ' + m.name + ' ' + m.count + ' online=' + S(m.online) + ' last=' + T(m.lastOk)); });

  // ---- 读取帮助（PR.sim.* / PR.ui.*） ----
  var LI = PR.sim.leaseInfo(d), CI = PR.sim.connInfo(d);
  L.push(' lease-info ' + [LI.state, LI.text, LI.short, LI.tone, LI.holder, strip(PR.ui.leaseBadge(d, { short: true }))].join('|'));
  L.push(' conn-info ' + [CI.state, CI.text, CI.short, CI.tone, S(!!CI.first)].join('|'));
  L.push(' health ' + PR.sim.health(d) + ' data=' + PR.sim.dataText(d) + ' estop=' + PR.sim.estopText(d));
  var k = PR.sim.counts(d), A = PR.ui.alarmCounts(d), AA = PR.ui.alarmCounts(d, { all: true });
  L.push(' counts ' + [k.installed, k.running, k.enabled, k.alarms, k.unacked, k.errAlarms].join(' ') + ' tally=' + A.err + '/' + A.warn + '/' + A.total +
    ' all=' + AA.err + '/' + AA.warn + '/' + AA.total + ' text=' + PR.ui.alarmText(d) + ' allText=' + PR.ui.alarmText(d, { all: true, none: '无' }));
  ['none', 'conn', 'safe', 'control'].forEach(function (g) { L.push(' guard ' + g + ' ' + PR.sim.guard(d, g)); });
  d.channels.forEach(function (ch) {
    L.push(' chd ' + ch.n + ' key=' + PR.sim.stateKey(d, ch) + ' text=' + PR.sim.stateText(d, ch) + ' word=' + strip(PR.ui.stateWord(d, ch)) + ' run=' + S(PR.sim.isRunning(ch)) +
      ' rpmOf=' + S(PR.sim.rpmOf(d, ch)) + ' read=' + ['tempC', 'pressureMPa', 'heaterPct', 'rpm', 'mmol', 'volumeMl', 'spC', 'pTargetMPa'].map(function (key) { return S(PR.sim.reading(d, ch, key)); }).join('|') +
      ' series=' + PR.sim.series(d, ch.n, 'tempC', 10).length + '/' + PR.sim.series(d, ch.n, 'pressureMPa').length);
    var pr = PR.sim.progress(d, ch);
    if (pr) {
      L.push(' progress ' + ch.n + ' ' + [pr.name, pr.status, pr.statusText, S(pr.offline), S(pr.waitGas), pr.gasName, pr.k, pr.n, pr.stepName, S(pr.overdue), pr.remainText, pr.remainShort,
        S(pr.stepRemainS), S(pr.stepRemainMin), S(pr.totalRemainMin), S(pr.within), S(pr.frac)].join('|'));
      L.push(' progtext ' + ch.n + ' ' + PR.ui.progText(d, ch) + ' / ' + PR.ui.progText(d, ch, { short: true }));
      L.push(' seg ' + ch.n + ' ' + PR.sim.segments(d, ch).map(function (s) { return s.i + ':' + s.state + ':' + T(s.t0) + ':' + T(s.t1) + ':' + S(s.waitGas) + ':' + s.name; }).join(';'));
      var bar = [], re = /<span class="pr-stepbar-seg ([^"]*)"><span class="pr-stepbar-track"><i style="width:(\d+)%"><\/i><\/span><\/span>/g, m;
      var html = PR.ui.stepBar(d, ch);
      while ((m = re.exec(html))) {
        var cls = m[1].split(' '), st = cls.indexOf('is-done') >= 0 ? 'done' : cls.indexOf('is-cur') >= 0 ? 'cur' : cls.indexOf('is-held') >= 0 ? 'held' : cls.indexOf('is-abort') >= 0 ? 'abort' : 'pending';
        bar.push(st + ':' + m[2] + ':' + S(cls.indexOf('is-run') >= 0));
      }
      L.push(' bar ' + ch.n + ' ' + bar.join(','));
      var prog = ch.program, marks = [];
      for (var i = 0; i < prog.steps.length; i++) {
        var card = PR.ui.stepCard(d, ch, prog, i), mm = /<span class="pr-step-mark[^"]*">([\s\S]*?)<\/span>/.exec(card);
        marks.push(PR.ui.stepState(prog, i) + ':' + (mm ? strip(mm[1]) : ''));
      }
      L.push(' marks ' + ch.n + ' ' + marks.join(';'));
    } else {
      L.push(' progtext ' + ch.n + ' ' + PR.ui.progText(d, ch) + ' / ' + PR.ui.progText(d, ch, { short: true }));
    }
    if (brief) return;
    var P = { n: ch.n, sp: 150, rpm: 600, id: 'h2-std' };
    L.push(' why ' + ch.n + ' ' + rle(CH_ACTIONS.map(function (a) { return PR.sim.why(d, a, P); })));
    L.push(' why2 ' + ch.n + ' ' + rle(chWhy2(ch.n).map(function (x) { return PR.sim.why(d, x[0], x[1]); })));
  });
  if (brief) return;
  var nx = d.channels.length + 1;
  L.push(' why ' + nx + ' ' + rle(CH_ACTIONS.map(function (a) { return PR.sim.why(d, a, { n: nx, sp: 150, rpm: 600 }); })));
  L.push(' whyd ' + rle(devWhy(d).map(function (x) { return x[0] + payloadText(x[1]) + '=' + PR.sim.why(d, x[0], x[1]); })));
}
/** 程序库的步骤文字（只与型号和程序库有关，单独成表）：stepName / stepText / condText（带型号、不带型号）/ stepSections（RV1、没有反应釜） */
function dumpSteps(d, L) {
  var first = d.channels.filter(function (c) { return c.installed; })[0];
  d.programs.forEach(function (p) {
    p.steps.forEach(function (s, i) {
      var c1 = PR.sim.condText(s, d), c0 = PR.sim.condText(s);
      var secs = function (ch) { return PR.ui.stepSections(d, ch, s).map(function (x) { return [x.dev, x.mode, x.label, x.value, S(x.tip)].join('/'); }).join(';'); };
      L.push('step ' + d.modelKey + ' ' + p.id + '/' + i + ' name=' + PR.sim.stepName(s) + ' text=' + PR.sim.stepText(d, s) + ' cond=' + c1.label + '|' + c1.value + ' cond0=' + c0.label + '|' + c0.value +
        ' secs=' + secs(first) + ' secs0=' + secs(null));
    });
  });
}
function dumpFleet(L, brief) {
  var st = PR.state;
  L.push('now ' + T(+st.now) + ' tick ' + st.tickN + ' frozen ' + S(st.frozen) + ' stress ' + st.stress);
  st.devices.forEach(function (d) { dumpDevice(d, L, brief); });
  var t = PR.sim.totals();
  L.push('totals ' + [t.devices, t.connected, t.remote, t.grace, t.alarms].join(' ') + ' ' + t.sessions.map(function (s) { return s.dev.k + ':' + s.id; }).join(','));
}

/* ---------- 场景：初始参数（有序键值对）+ 脚本 ---------- */
var LOG = null;
PR.toast = function (msg, kind) { if (LOG) LOG.push('toast ' + (kind || 'ok') + ' ' + msg); };
function obj(pairs) { var o = {}; pairs.forEach(function (kv) { o[kv[0]] = kv[1]; }); return o; }
function run(name, pairs, script) {
  var L = [];
  LOG = L;
  var p = obj(pairs);
  PR.sim.init(p.devs || '', { frozen: p.freeze === '1', stress: p.stress });
  PR.sim.applyParams(p);
  Math.random = rng(NOISE_SEED);
  (script || []).forEach(function (step) {
    var op = step[0];
    if (op === 'tick') { for (var i = 0; i < step[1]; i++) PR.sim.tick(); L.push('ticked ' + step[1]); }
    else if (op === 'act') {
      var d = PR.sim.dev(step[1]);
      var ok = PR.sim.act(d, step[2], step[3]);
      L.push('act ' + step[1] + ' ' + step[2] + ' ' + payloadText(step[3]) + ' = ' + S(ok));
    } else if (op === 'actMany') {
      var n = PR.sim.actMany(step[1], step[2], step[3]);
      L.push('actMany ' + step[1].join(',') + ' ' + step[2] + ' = ' + n);
    } else if (op === 'add') {
      var nd = PR.sim.addDevice(step[1]);
      L.push('add ' + nd.k + ' ' + nd.alias);
    } else if (op === 'edit') {
      var r = PR.sim.editDevice(PR.sim.dev(step[1]), step[2]);
      L.push('edit ' + step[1] + ' = ' + r);
    } else if (op === 'apply') {
      PR.sim.applyParams(obj(step[1]));
      L.push('applied');
    } else if (op === 'dump') dumpFleet(L, step[1] === 'brief');
    else throw new Error('unknown op ' + op);
  });
  dumpFleet(L);
  LOG = null;
  return { name: name, params: pairs, script: script || [], lines: L, digest: fnv(L) };
}

var F = ['freeze', '1'];
var scenarios = [
  run('devs1', [['devs', '1'], F]),
  run('devs3', [F]),
  run('devs4', [['devs', '4'], F]),
  run('list', [['devs', 'PR8,PR4,PR16'], F]),
  run('list-odd', [['devs', ' PR16 , nope,PR4'], F]),
  run('stress1', [['stress', '1'], F]),
  run('stress2', [['devs', '4'], ['stress', '2'], F]),
  run('overrides', [['devs', '4'], F, ['alarm', '1:1,2:1,3:1'], ['estop', '2'], ['lease', '1:lost,2:requested'], ['offline', '3,4']]),
  run('online4', [['devs', '4'], F, ['offline', 'none'], ['lease', '2:remote,3:remote,4:remote'], ['alarm', '3:0']]),
  run('estop-mix', [F, ['estop', '1,3'], ['lease', '3:remote,2:lost'], ['alarm', '1:0,3:9']]),
  run('stress-mix', [['devs', '4'], ['stress', '1'], F, ['alarm', '3:0,1:1'], ['offline', 'none'], ['estop', '4'], ['lease', '3:remote']]),
  run('apply-diff', [F], [
    ['apply', [F, ['lease', '2:remote'], ['alarm', '2:1'], ['estop', '3']]],
    ['apply', [F, ['lease', '2:remote,1:requested'], ['offline', '1'], ['estop', '']]],
    ['apply', [F, ['alarm', '2:0'], ['offline', 'none']]],
    ['apply', [F, ['devs', '4'], ['alarm', '4:1']]],
    ['apply', [F, ['devs', '4'], ['alarm', '4:2'], ['offline', '']]],
  ]),
  run('tick-live', [['devs', '4'], ['offline', 'none'], ['lease', '2:remote,3:remote,4:remote']], [
    ['tick', 1], ['dump', 'brief'], ['tick', 59], ['tick', 140],
  ]),
  run('tick-offline', [['devs', '4']], [
    ['act', 2, 'leaseRequest', {}], ['act', 4, 'leaseRequest', {}], ['tick', 6], ['act', 4, 'reconnect', {}], ['tick', 3], ['tick', 40],
  ]),
  run('lease-timeout', [['lease', '2:requested,1:lost']], [['tick', 20], ['dump', 'brief'], ['tick', 30]]),
  run('stress-tick', [['devs', '4'], ['stress', '2'], ['offline', 'none']], [
    ['tick', 9], ['act', 1, 'stop', { n: 1 }], ['act', 1, 'progAbort', { n: 2 }], ['act', 3, 'alarmAck', { id: 'a5' }], ['tick', 3], ['act', 2, 'estop', {}], ['tick', 3],
  ]),
  run('actions', [], [
    ['act', 1, 'stop', { n: 1 }], ['act', 1, 'stop', { n: 1 }], ['act', 1, 'heatStop', { n: 2 }], ['act', 1, 'heatStop', { n: 2 }], ['act', 1, 'progAbort', { n: 4 }],
    ['act', 1, 'disable', { n: 3 }], ['act', 1, 'disable', { n: 3 }], ['act', 1, 'enable', { n: 3 }], ['act', 1, 'start', { n: 3 }], ['act', 1, 'heatStart', { n: 4, sp: 135 }],
    ['act', 1, 'setSp', { n: 4, sp: 140 }], ['act', 1, 'setSp', { n: 4, sp: 999 }], ['act', 1, 'progLoad', { n: 1, id: 'purge-3' }], ['act', 1, 'progRun', { n: 1 }],
    ['act', 1, 'stirSet', { rpm: 650.4 }], ['act', 1, 'stirOff', {}], ['act', 1, 'stirOff', {}], ['act', 1, 'stirOn', {}], ['act', 1, 'chStirOn', { n: 1 }],
    ['act', 1, 'sessionMark', { n: 2 }], ['act', 1, 'sessionMark', { text: '手动标记' }], ['act', 1, 'sessionEnd', {}], ['act', 1, 'sessionStart', {}],
    ['act', 1, 'alarmAck', { id: 'a1' }], ['act', 1, 'alarmAck', { id: 'a1' }], ['tick', 12],
    ['act', 2, 'heatStart', { n: 1, sp: 150 }], ['act', 2, 'leaseRequest', {}], ['act', 2, 'leaseRequest', {}], ['tick', 5], ['act', 2, 'chStirSet', { n: 2, rpm: 1200 }],
    ['act', 2, 'chStirOff', { n: 2 }], ['act', 2, 'chStirOff', { n: 2 }], ['act', 2, 'chStirOn', { n: 2, rpm: 900 }], ['act', 2, 'stirOn', {}], ['act', 2, 'progLoad', { n: 4 }],
    ['act', 2, 'runAll', {}], ['act', 2, 'stopAll', {}], ['act', 2, 'stopAll', {}], ['act', 2, 'leaseRelease', {}], ['act', 2, 'leaseCancel', {}],
    ['act', 3, 'alarmAck', { id: 'a2' }], ['act', 3, 'estop', {}], ['act', 3, 'estop', {}], ['act', 3, 'stop', { n: 1 }], ['act', 3, 'disconnect', {}], ['act', 3, 'disconnect', {}],
    ['tick', 3], ['act', 3, 'reconnect', {}], ['tick', 3], ['act', 2, 'rename', { alias: ' 高压釜 ' }], ['act', 2, 'rename', { alias: '1号机' }],
    ['add', { host: '192.168.1.99', port: 9090, modelKey: 'PR16' }], ['add', { alias: '新设备', modelKey: 'PR4', autoConnect: false }], ['tick', 1], ['dump', 'brief'], ['tick', 2],
    ['edit', 4, { alias: '高压釜', host: '10.0.0.5' }], ['edit', 4, { alias: '十六路', host: '10.0.0.5', port: 8081 }], ['tick', 3],
    ['act', 1, 'leaseRequest', {}], ['act', 1, 'remove', {}], ['actMany', [2, 3, 4, 5], 'estop', {}], ['actMany', [2, 4, 5, 9], 'stopAll', {}], ['tick', 2],
  ]),
];

/* 步骤文字：每个型号的程序库（stress=1 时多一个最坏值程序） */
var steps = [];
['PR8-4,PR4,PR16,PR8'].forEach(function (devs) {
  [0, 1].forEach(function (stress) {
    PR.sim.init(devs, { frozen: true, stress: stress ? '1' : undefined });
    PR.sim.devices().forEach(function (d) { dumpSteps(d, steps); });
  });
});
/* 设备组合的解析（样稿 parseDevs）：每台的型号键 */
var devs = ['', '1', '3', '4', '2', '0', '9', '11', 'PR4', ' PR16 , nope,PR4', 'PR16,PR4,PR4,PR8-4,PR8,PR16,PR4,PR8,PR16', 'nope', ',', 'pr4', 'PR8-4, PR8 ,,PR4'].map(function (v) {
  PR.sim.init(v, { frozen: true });
  return [v, PR.sim.devices().map(function (d) { return d.modelKey; }).join(',')];
});

var out = { source: 'research/pc-v3 js/core.js + js/models.js + js/sim.js + js/ui.js', noiseSeed: NOISE_SEED, scenarios: scenarios, steps: steps, devs: devs };
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
print('// 由 MockVectors/gen-demo-vectors.js 用样稿 research/pc-v3 的 js/core.js + js/models.js + js/sim.js + js/ui.js（JavaScriptCore）生成，不要手改；');
print('// 样稿改了就重新生成（命令见脚本开头）。每个场景：参数 + 脚本 + 跑完后的规范文字摘要（C# DemoDump 逐行照写）。');
print('// </auto-generated>');
print('namespace ParallelReactor.Control.Tests;');
print('');
print('internal static class DemoMockVectors');
print('{');
print('    public const string Json = """""');
print(json);
print('""""";');
print('}');
