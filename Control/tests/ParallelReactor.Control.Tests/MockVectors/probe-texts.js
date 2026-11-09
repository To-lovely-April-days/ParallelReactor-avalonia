/* 总览 / 设备页的报警与事件文字探针（WebKit）：总览分区头的报警小块与顶部报警条；设备页「报警与事件」的当前报警、事件筛选（逐个点 全部 / 报警提醒 / 操作）、窄工作区的页签。 */
(function () {
  const T = el => el ? el.textContent.replace(/\s+/g, ' ').trim() : null;
  const A = (el, a) => el ? el.getAttribute(a) : null;
  const out = { route: location.hash };
  const ov = document.querySelector('.overview');
  if (ov) {
    out.chips = Array.prototype.map.call(ov.querySelectorAll('.overview-dev'), dev => {
      const c = dev.querySelector('.overview-alarms');
      return { k: A(dev, 'data-ov-dev'), chip: c ? { text: T(c), tip: A(c, 'data-tip'), sel: A(c, 'data-sel'), err: c.classList.contains('is-err') } : null,
        run: T(dev.querySelector('.overview-run')), estop: T(dev.querySelector('.overview-estop')) };
    });
    const bar = ov.querySelector('.overview-alarmbar');
    out.bar = bar ? { who: T(bar.querySelector('.overview-ab-who')), text: T(bar.querySelector('.overview-ab-text')), title: A(bar.querySelector('.overview-ab-text'), 'title'),
      time: T(bar.querySelector('.overview-ab-time')), more: T(bar.querySelector('.overview-ab-more')), loc: A(bar.querySelector('[data-ov-locate]'), 'data-ov-locate'),
      ack: A(bar.querySelector('[data-act="alarmAck"]'), 'data-id') } : null;
  }
  const dv = document.querySelector('.device');
  if (dv) {
    // 页签面板：先读页签，再切到「报警与事件」
    const tabs = dv.querySelectorAll('[data-ptabs="dv-tab"] .pr-ptab');
    out.tabs = Array.prototype.map.call(tabs, t => ({ v: A(t, 'data-v'), on: t.classList.contains('is-on'), text: T(t), dot: !!t.querySelector('.pr-ptab-dot'), n: T(t.querySelector('.pr-ptab-n')), tip: A(t, 'data-tip') || A(t, 'title') }));
    const logTab = dv.querySelector('[data-ptabs="dv-tab"] .pr-ptab[data-v="log"]');
    if (logTab && !logTab.classList.contains('is-on')) logTab.click();
    const panel = dv.querySelector('.device-log, .device-tabs--log');
    out.unackSub = panel ? T(panel.querySelector('.device-unacked')) : null;
    out.unackDot = panel ? !!(panel.querySelector('.device-unacked .device-rdot')) : null;
    const al = dv.querySelector('.device-alarms');
    if (al) {
      const h = al.querySelector('.device-sub-h');
      out.alarmHead = [T(h.querySelector('b')), T(h.querySelector('span'))];
      out.alarms = Array.prototype.map.call(al.querySelectorAll('.device-alarm'), a => ({ key: A(a, 'data-key'), cls: a.className, b: T(a.querySelector('.device-alarm-t b')), bTitle: A(a.querySelector('.device-alarm-t b'), 'title'),
        meta: T(a.querySelector('.device-alarm-t > span')), full: A(a.querySelector('.device-alarm-t > span'), 'title'), ack: !!a.querySelector('[data-act="alarmAck"]') }));
      out.noAlarm = T(al.querySelector('.device-noalarm'));
    }
    const read = () => {
      const segs = Array.prototype.map.call(dv.querySelectorAll('[data-seg="dv-evf"] .pr-seg-b'), s => [A(s, 'data-v'), T(s), s.classList.contains('is-on') || A(s, 'aria-checked') === 'true']);
      const evs = Array.prototype.map.call(dv.querySelectorAll('.device-ev'), e => [A(e, 'data-key'), e.className.replace('device-ev ', ''), T(e.querySelector('time')), T(e.querySelector('.device-ev-rv')),
        e.querySelector('.device-ev-rv').classList.contains('is-dev'), T(e.querySelector('.device-ev-t')), A(e.querySelector('.device-ev-t'), 'title')]);
      return { segs, evs, empty: T(dv.querySelector('.device-evempty')) };
    };
    out.ev = {};
    ['all', 'alarm', 'op'].forEach(v => {
      const b = dv.querySelector('[data-seg="dv-evf"] .pr-seg-b[data-v="' + v + '"]');
      if (b) b.click();
      out.ev[v] = read();
    });
    const all = dv.querySelector('[data-seg="dv-evf"] .pr-seg-b[data-v="all"]');
    if (all) all.click();
  }
  console.warn('PROBE ' + JSON.stringify(out));
})();
