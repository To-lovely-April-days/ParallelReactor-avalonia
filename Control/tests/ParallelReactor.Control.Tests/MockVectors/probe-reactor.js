/* 反应釜页「程序」画布探针（WebKit）：版式（宽 / 页签、每行 2 张）、画布滚动区、每行卡片数与卡片宽、放大倍数，以及画布里每个元素的位置
   （步骤列 / 卡片 / 条件框 / 箭头 / 行分隔线 / 竖向连线 / 安全联锁框，单位 = 设计 px：相对画布内容的左上角、按放大倍数折回），
   条件框的文字取舍（「结束条件」→「条件」→ 小字 → 只留值）、自动滚动后的 scrollTop、状态行与画布工具。
   探针前可以先点放大（window.__zoom = 1 / 2）。 */
(function () {
  const zoomClicks = window.__zoom || 0;
  for (let i = 0; i < zoomClicks; i++) { const b = document.querySelector('.reactor [data-rv-zoom="1"]'); if (b && !b.hasAttribute('aria-disabled')) b.click(); }
  const out = {};
  const root = document.querySelector('.reactor');
  if (!root) { console.warn('PROBE ' + JSON.stringify({ none: true })); return; }
  out.root = [root.clientWidth, root.clientHeight];
  const grid = root.querySelector('.reactor-grid');
  out.grid = grid ? grid.className.replace('reactor-grid', '').trim() : null;
  const cv = root.querySelector('.reactor-scroll');
  const prog = root.querySelector('.reactor-prog');
  out.prog = prog ? [prog.getBoundingClientRect().width, prog.getBoundingClientRect().height] : null;
  if (!cv) { console.warn('PROBE ' + JSON.stringify(out)); return; }
  out.cv = { ow: cv.offsetWidth, cw: cv.clientWidth, ch: cv.clientHeight, oh: cv.offsetHeight, st: cv.scrollTop, sl: cv.scrollLeft, sh: cv.scrollHeight, sw: cv.scrollWidth };
  const flow = cv.querySelector('.reactor-flow');
  if (!flow) { out.noflow = true; console.warn('PROBE ' + JSON.stringify(out)); return; }
  const cs = getComputedStyle(flow);
  const z = parseFloat(cs.zoom) || 1;
  out.flow = { per: +cs.getPropertyValue('--per'), stepW: parseFloat(cs.getPropertyValue('--step-w')), zoom: z, col: flow.classList.contains('is-col'),
    stale: flow.classList.contains('is-stale'), preview: flow.classList.contains('is-preview') };
  const fr = flow.getBoundingClientRect(), vr = cv.getBoundingClientRect();
  // 画布内容坐标（未滚动）里 flow 的位置，再把子元素换算成相对 flow 边框盒的设计 px（除以放大倍数）
  out.flowBox = [+(((fr.left - vr.left) + cv.scrollLeft)).toFixed(2), +(((fr.top - vr.top) + cv.scrollTop)).toFixed(2), +fr.width.toFixed(2), +fr.height.toFixed(2)];
  const rel = el => { const r = el.getBoundingClientRect(); return [+((r.left - fr.left) / z).toFixed(2), +((r.top - fr.top) / z).toFixed(2), +(r.width / z).toFixed(2), +(r.height / z).toFixed(2)]; };
  out.cols = Array.prototype.map.call(flow.querySelectorAll('.pr-step-col'), c => {
    const card = c.querySelector('.pr-step'), cond = c.querySelector('.pr-step-cond');
    const lab = cond && cond.querySelector('span'), val = cond && cond.querySelector('b');
    return { col: rel(c), card: rel(card), cond: rel(cond), state: (card.className.match(/\bis-(done|cur|next|abort)\b/) || [])[1] || '', held: card.classList.contains('is-held'),
      label: lab ? lab.textContent : null, value: val ? val.textContent : null, sm: !!(val && val.classList.contains('is-sm')),
      // 样稿 condHTML 量字用的宽度（ui.textWidth，页面字体）：三种标签 14 px、值 18 / 16 px 600 —— C# 的 CondFit 用同样的宽度判断
      fw: val ? [PR.ui.textWidth('结束条件', 14), PR.ui.textWidth('条件', 14), PR.ui.textWidth('步骤时间', 14), PR.ui.textWidth(val.textContent, 18, 600), PR.ui.textWidth(val.textContent, 16, 600)] : null,
      mark: (card.querySelector('.pr-step-mark') || {}).textContent || '', title: (card.querySelector('.pr-step-title b') || {}).textContent || '' };
  });
  out.arrows = Array.prototype.map.call(flow.querySelectorAll('.pr-step-arrow'), rel);
  out.dividers = Array.prototype.map.call(flow.querySelectorAll('.pr-step-divider'), rel);
  out.vlinks = Array.prototype.map.call(flow.querySelectorAll('.reactor-vlink'), rel);
  const safe = flow.querySelector('.reactor-safe');
  out.safe = safe ? { box: rel(safe), cls: safe.className, v: (safe.querySelector('.reactor-safe-row b') || {}).textContent, note: (safe.querySelector('.reactor-safe-note') || {}).textContent } : null;
  out.rows = Array.prototype.map.call(flow.querySelectorAll('.pr-step-row-line'), rel);
  const ps = root.querySelector('.reactor-ps');
  out.status = ps ? { title: ps.getAttribute('title'), l: (ps.querySelector('.reactor-ps-l') || {}).textContent, s: (ps.querySelector('.reactor-ps-s') || {}).textContent,
    lVis: getComputedStyle(ps.querySelector('.reactor-ps-l')).display !== 'none', key: ((ps.querySelector('.pr-dot') || { className: '' }).className.match(/st-(\w+)/) || [])[1] } : null;
  out.tools = Array.prototype.map.call(root.querySelectorAll('.reactor-ctools button'), b => [b.getAttribute('aria-label'), b.hasAttribute('aria-disabled'), b.getAttribute('data-tip') || '']);
  const zv = root.querySelector('.reactor-zoomv');
  out.zoomText = zv ? zv.textContent : null;
  const crumb = root.querySelector('.reactor-crumb');
  out.crumb = crumb ? { title: crumb.getAttribute('title'), nodev: crumb.classList.contains('is-nodev'), dev: getComputedStyle(crumb.querySelector('.reactor-crumb-dev')).display !== 'none' } : null;
  console.warn('PROBE ' + JSON.stringify(out));
})();
