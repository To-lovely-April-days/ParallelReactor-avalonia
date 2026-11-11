/* 曲线探针（WebKit）：反应釜页「曲线」面板（.reactor-curves）、总览「温度趋势」（.overview-trend）、组件陈列「曲线」（.gallery-chart）——
   面板矩形、图例里显示着的项、分段的当前值、曲线容器尺寸、画出来的 SVG（长路径只记摘要 #fnv:长度），以及参考线 / 标记文字的量宽（ui.textWidth 14/600）。 */
(function () {
  function fnv(s) { let h = 0x811c9dc5; s += '\n'; for (let j = 0; j < s.length; j++) { h ^= s.charCodeAt(j); h = Math.imul(h, 0x01000193) >>> 0; } return ('00000000' + h.toString(16)).slice(-8); }
  const vis = el => !!el && el.offsetParent !== null && getComputedStyle(el).display !== 'none';
  const rect = el => { const r = el.getBoundingClientRect(); return [+r.left.toFixed(2), +r.top.toFixed(2), +r.width.toFixed(2), +r.height.toFixed(2)]; };
  function chart(c) {
    const svg = c.querySelector('svg');
    const labels = {};
    if (svg) svg.querySelectorAll('.pr-chart-ref-label, .pr-chart-marker-label').forEach(t => { labels[t.textContent] = PR.ui.textWidth(t.textContent, 14, 600); });
    return { rect: rect(c), size: [c.clientWidth, c.clientHeight], labels,
      svg: svg ? svg.outerHTML.replace(/ d="([^"]*)"/g, (m, d) => d.length > 400 ? ' d="#' + fnv(d) + ':' + d.length + '"' : m) : '' };
  }
  const out = {};
  const p = document.querySelector('.reactor-curves');
  if (p) {
    out.curves = { rect: rect(p), legend: Array.prototype.filter.call(p.querySelectorAll('.reactor-lg'), vis).map(x => x.textContent.trim()),
      legendShown: vis(p.querySelector('.reactor-legend')), range: (p.querySelector('.pr-seg-b.is-on') || {}).textContent,
      chart: chart(p.querySelector('.reactor-chart')) };
  }
  const t = document.querySelector('.overview-trend');
  if (t) out.trend = { rect: rect(t), chart: chart(t.querySelector('.overview-chart')) };
  const g = document.querySelectorAll('.gallery-chart');
  if (g.length) out.gallery = Array.prototype.map.call(g, chart);
  console.warn('PROBE ' + JSON.stringify(out));
})();
