/* 反应釜页面板网格探针（WebKit）：版式（宽 / 页签、每行 2 张）、网格尺寸、各面板（程序 / 釜体（页签）/ 属性查看器 / 曲线）相对网格的矩形；
   探针前可以先关掉面板（window.__close = ['rv-prog', …]，点面板标题栏的「关闭面板」）。 */
(function () {
  (window.__close || []).forEach(id => { const b = document.querySelector('.reactor .pr-panel[data-panel="' + id + '"] [data-panel-close]'); if (b) b.click(); });
  const out = {};
  const root = document.querySelector('.reactor');
  const grid = root && root.querySelector('.reactor-grid');
  if (!grid) { console.warn('PROBE ' + JSON.stringify({ none: true })); return; }
  out.root = [root.clientWidth, root.clientHeight];
  out.grid = [grid.clientWidth, grid.clientHeight];
  out.cls = grid.className.replace('reactor-grid', '').trim();
  out.alerts = (root.querySelector('.reactor-alerts') || { offsetHeight: 0 }).offsetHeight;
  const g = grid.getBoundingClientRect();
  out.panels = {};
  ['rv-prog', 'rv-vessel', 'rv-props', 'rv-curves'].forEach(id => {
    const p = grid.querySelector('.pr-panel[data-panel="' + id + '"]');
    if (!p) return;
    const r = p.getBoundingClientRect();
    out.panels[id] = { closed: p.classList.contains('is-closed') || getComputedStyle(p).display === 'none',
      rect: [+(r.left - g.left).toFixed(2), +(r.top - g.top).toFixed(2), +r.width.toFixed(2), +r.height.toFixed(2)] };
  });
  console.warn('PROBE ' + JSON.stringify(out));
})();
