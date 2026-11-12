/* 总览版面探针（WebKit）：根尺寸、报警条 / 温度趋势、每个设备分区的网格与卡片（容器宽度与按容器查询得出的取舍）。 */
(function () {
  function cw(el) { const s = getComputedStyle(el); return +(el.getBoundingClientRect().width - parseFloat(s.paddingLeft) - parseFloat(s.paddingRight) - parseFloat(s.borderLeftWidth) - parseFloat(s.borderRightWidth)).toFixed(3); }
  function vis(el) { return !!(el && el.getClientRects().length && getComputedStyle(el).display !== 'none'); }
  function fs(el) { return el ? parseFloat(getComputedStyle(el).fontSize) : null; }
  const out = {};
  const root = document.querySelector('.overview');
  out.root = [root.clientWidth, root.clientHeight];
  const sc = root.querySelector('.overview-scroll');
  out.scroll = sc ? [sc.clientWidth, sc.clientHeight, sc.scrollHeight] : null;
  out.bar = !!root.querySelector('.overview-alarmbar');
  out.trend = !!root.querySelector('.overview-trend');
  out.strip = PR.sim.devices().reduce((n, d) => n + d.alarms.filter(a => a.level === 'err' && !a.acked && a.code !== 'ESTOP').length, 0);
  out.devs = Array.prototype.map.call(root.querySelectorAll('.overview-dev'), function (dev) {
    const k = +dev.getAttribute('data-ov-dev'), d = PR.sim.dev(k);
    const grid = dev.querySelector('.overview-grid');
    const name = dev.querySelector('.overview-name');
    return {
      dev: { k: k, model: d.modelKey, inst: d.model.installed, ch: d.model.channelCount, online: PR.sim.online(d), ever: d.conn.everOnline, estop: d.estop.active },
      cls: dev.className.replace(/pr-panel ?/, ''),
      h: +dev.getBoundingClientRect().height.toFixed(2),
      model: vis(dev.querySelector('.overview-model')),
      nomodel: !!(name && name.classList.contains('is-nomodel')),
      sess: vis(dev.querySelector('.overview-sess')),
      note: dev.querySelector('.overview-note') ? dev.querySelector('.overview-note').offsetHeight : 0,
      gridStyle: grid ? grid.getAttribute('style') : null,
      gridH: grid ? +grid.getBoundingClientRect().height.toFixed(2) : null,
      items: grid ? Array.prototype.map.call(grid.children, function (c) {
        const r = c.getBoundingClientRect(), g = grid.getBoundingClientRect();
        const pos = [+(r.left - g.left).toFixed(2), +(r.top - g.top).toFixed(2), +r.width.toFixed(2), +r.height.toFixed(2)];
        if (c.classList.contains('overview-untile')) return ['U'].concat(pos, [c.getAttribute('data-key'), T(c.querySelector('b'))]);
        const v = c.querySelector('.overview-vessel');
        const tgt = c.querySelector('.overview-r--t .overview-tgt');
        const ptg = c.querySelector('.overview-r--p .overview-tgt');
        const tb = c.querySelector('.overview-r--t > b'), pb = c.querySelector('.overview-r--p > b');
        const tsm = c.querySelector('.overview-r--t > small'), psm = c.querySelector('.overview-r--p > small');
        const prog = c.querySelector('.overview-prog');
        const pn = c.querySelector('.overview-pname');
        const st = c.querySelector('.overview-st');
        const aux = c.querySelector('.overview-r--aux');
        const tgtText = el => el && el.textContent.trim() ? (getComputedStyle(el).display !== 'none' ? [true, el.querySelector('i') ? vis(el.querySelector('i')) : null] : [false, null]) : null;
        return ['C'].concat(pos, [cw(c), c.getAttribute('data-key'), {
          st: (c.className.match(/\bst-(\w+)/) || [])[1],
          v: vis(v) ? [v.offsetWidth, v.offsetHeight] : 0,
          tgt: tgtText(tgt), ptg: tgtText(ptg),
          t: [fs(tb), vis(tsm)], p: [fs(pb), vis(psm)],
          pname: vis(pn), none: !!(prog && prog.classList.contains('is-none')),
          barfit: prog ? prog.classList.contains('is-barfit') : null,
          prog: prog ? prog.offsetHeight : 0,
          stWrap: st ? st.offsetTop > 20 : null,
          aux: aux ? [fs(aux.querySelector('.overview-kv > b')), getComputedStyle(aux).columnGap, vis(aux.querySelector('.overview-kv > small')), aux.getAttribute('data-len')] : null,
          len: [c.querySelector('.overview-r--t') && c.querySelector('.overview-r--t').getAttribute('data-len'), c.querySelector('.overview-r--p') && c.querySelector('.overview-r--p').getAttribute('data-len')]
        }]);
      }) : []
    };
  });
  function T(el) { return el ? el.textContent.trim() : null; }
  console.warn('PROBE ' + JSON.stringify(out));
})();
