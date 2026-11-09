/* 设备页版面探针（WebKit）：根尺寸、上下两行、卡片网格、每张卡片的容器尺寸与按容器查询 / fitSubs 得出的取舍。 */
(function () {
  function box(el) {
    const s = getComputedStyle(el);
    return [+(el.clientWidth - parseFloat(s.paddingLeft) - parseFloat(s.paddingRight)).toFixed(2), +(el.clientHeight - parseFloat(s.paddingTop) - parseFloat(s.paddingBottom)).toFixed(2)];
  }
  function rect(el) { const r = el.getBoundingClientRect(); return [+r.width.toFixed(2), +r.height.toFixed(2)]; }
  function vis(el) { return !!(el && el.getClientRects().length && getComputedStyle(el).display !== 'none'); }
  function emInfo(em, row) {
    if (getComputedStyle(em).display === 'none') return 'none';
    const e = em.getBoundingClientRect(), r = row.getBoundingClientRect();
    return e.top >= r.bottom - 1 ? 'clipped' : (e.top > r.top + 20 ? 'line' : 'inline');
  }
  function fs(el) { return el ? parseFloat(getComputedStyle(el).fontSize) : null; }
  function lh(el) { return el ? parseFloat(getComputedStyle(el).lineHeight) : null; }
  const out = {};
  const root = document.querySelector('.device');
  const k = +location.hash.match(/#\/device\/(\d+)/)[1];
  const d = PR.sim.dev(k);
  out.dev = { k: k, model: d.modelKey, inst: d.model.installed, ch: d.model.channelCount, online: PR.sim.online(d), ever: d.conn.everOnline, estop: d.estop.active };
  out.root = [root.clientWidth, root.clientHeight];
  out.cls = root.className;
  const grid = root.querySelector('.device-grid');
  out.gridStyle = grid ? grid.getAttribute('style') : null;
  const rv = root.querySelector('.device-rv');
  out.rvH = rv ? rv.offsetHeight : 0;
  out.bars = Array.prototype.map.call(root.querySelectorAll('.device-bar'), b => b.offsetHeight);
  out.never = !!root.querySelector('.device-never');
  const cards = root.querySelector('.device-cards');
  out.cardsCls = cards ? cards.className : null;
  out.cardsStyle = cards ? cards.getAttribute('style') : null;
  out.cardsBox = cards ? [cards.clientWidth, cards.clientHeight] : null;
  out.row = Array.prototype.map.call(root.querySelectorAll('.device-row > .pr-panel'), p => {
    const b = p.querySelector('.pr-panel-body');
    return [p.className.replace(/pr-panel ?/, '').split(' ')[0], +p.getBoundingClientRect().width.toFixed(2), p.offsetHeight, b ? box(b) : null];
  });
  const tabs = root.querySelector('.device-tabs');
  out.tab = tabs ? (tabs.className.indexOf('device-tabs--log') >= 0 ? 'log' : 'info') : null;
  out.items = cards ? Array.prototype.map.call(cards.children, function (c) {
    if (c.classList.contains('device-un')) return ['U', c.offsetLeft, c.offsetTop, +c.getBoundingClientRect().width.toFixed(2), +c.getBoundingClientRect().height.toFixed(2), c.getAttribute('data-key')];
    const body = c.querySelector('.device-body'), v = c.querySelector('.device-vessel'), reads = c.querySelector('.device-reads');
    const tb = c.querySelector('.device-t .device-v b'), pb = c.querySelector('.device-p .device-v b');
    const tem = c.querySelector('.device-t em'), pem = c.querySelector('.device-p em');
    const kv = cl => { const r = c.querySelector('.device-kvr.' + cl); return r ? (vis(r) ? [fs(r.querySelector('.device-v b')), vis(r.querySelector('.device-v small')), vis(r.querySelector('em')), r.getAttribute('data-len')] : [0, false, false, r.getAttribute('data-len')]) : null; };
    const tsm = c.querySelector('.device-t .device-v small'), psm = c.querySelector('.device-p .device-v small');
    const nameLine = c.querySelector('.device-pline.is-name'), barLine = c.querySelector('.device-pline.is-bar');
    const st = (c.className.match(/\bst-(\w+)/) || [])[1];
    const badge = c.querySelector('.device-chead .pr-badge--st'), rvn = c.querySelector('.device-rvn');
    return ['C', c.offsetLeft, c.offsetTop, +c.getBoundingClientRect().width.toFixed(2), +c.getBoundingClientRect().height.toFixed(2), box(c), c.getAttribute('data-key'), {
      st: st,
      dir: body ? getComputedStyle(body).flexDirection : null,
      v: vis(v) ? rect(v) : 0,
      reads: reads ? [+reads.getBoundingClientRect().width.toFixed(2), reads.scrollHeight] : null,
      body: body ? [+body.getBoundingClientRect().width.toFixed(2), +body.getBoundingClientRect().height.toFixed(2)] : null,
      head: c.querySelector('.device-chead') ? c.querySelector('.device-chead').offsetHeight : null,
      prog: c.querySelector('.device-prog') ? c.querySelector('.device-prog').offsetHeight : null,
      t: [fs(tb), lh(tb), vis(tsm)], p: [fs(pb), lh(pb), vis(psm)],
      tem: tem ? emInfo(tem, c.querySelector('.device-t')) : null,
      pem: pem ? emInfo(pem, c.querySelector('.device-p')) : null,
      sub: (c.classList.contains('is-notsub') ? 'T' : '') + (c.classList.contains('is-nopsub') ? 'P' : ''),
      stir: kv('is-stir'), heat: kv('is-heat'), gas: kv('is-gas'),
      name: vis(nameLine), kn: vis(c.querySelector('.device-kn')), rem: vis(c.querySelector('.device-rem')), bar: vis(c.querySelector('.pr-stepbar')),
      none: !!c.querySelector('.device-prog.is-none'),
      barfit: barLine ? barLine.classList.contains('is-barfit') : null,
      wrap: badge && rvn ? badge.offsetTop > rvn.offsetTop + 4 : null,
      len: [c.querySelector('.device-t') && c.querySelector('.device-t').getAttribute('data-len'), c.querySelector('.device-p') && c.querySelector('.device-p').getAttribute('data-len')]
    }];
  }) : [];
  console.warn('PROBE ' + JSON.stringify(out));
})();
