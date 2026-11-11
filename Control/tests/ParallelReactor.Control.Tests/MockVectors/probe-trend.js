/* 总览「温度趋势」探针（WebKit）：面板在不在、图例（设备别名、每条线的 RV 与图例小样的虚线、「显示 n/m」及其提示）、
   曲线容器尺寸与画出来的 SVG（长路径只记摘要 #fnv:长度，C# 按同样的写法拼出来比较）。 */
(function () {
  function fnv(s) { let h = 0x811c9dc5; s += '\n'; for (let j = 0; j < s.length; j++) { h ^= s.charCodeAt(j); h = Math.imul(h, 0x01000193) >>> 0; } return ('00000000' + h.toString(16)).slice(-8); }
  const out = {};
  const p = document.querySelector('.overview-trend');
  out.shown = !!p;
  if (!p) { console.warn('PROBE ' + JSON.stringify(out)); return; }
  out.title = (p.querySelector('.pr-panel-title') || {}).textContent;
  out.sub = (p.querySelector('.pr-panel-sub') || {}).textContent;
  out.groups = Array.prototype.map.call(p.querySelectorAll('.overview-lgg'), g => ({
    alias: (g.querySelector('b') || {}).textContent,
    items: Array.prototype.map.call(g.querySelectorAll('.overview-lg'), it => { const path = it.querySelector('path'); return [it.textContent.trim(), path.getAttribute('stroke'), path.getAttribute('stroke-dasharray')]; })
  }));
  const more = p.querySelector('.overview-lgmore');
  out.more = more ? [more.textContent, more.getAttribute('title')] : null;
  const c = p.querySelector('.overview-chart');
  out.size = [c.clientWidth, c.clientHeight];
  const svg = c.querySelector('svg');
  out.svg = svg ? svg.outerHTML.replace(/ d="([^"]*)"/g, (m, d) => d.length > 400 ? ' d="#' + fnv(d) + ':' + d.length + '"' : m) : '';
  out.sel = PR.shell && PR.shell.sel ? PR.shell.sel.k : null;
  console.warn('PROBE ' + JSON.stringify(out));
})();
