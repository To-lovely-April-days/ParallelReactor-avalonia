#!/usr/bin/env python3
"""gen-icons.py — 图标：MOCK/js/ui.js 的 ICONS → Styles/Icons.axaml（StreamGeometry）+ Controls/IconSet.g.cs（名字 / viewBox / 填充子图形）。

只用 Python 标准库。转换规则（PORT-PLAN §4.1 / M1）：
  · 每个图标一个 StreamGeometry，键 Icon.<名字>（描边部分）；带 fill="currentColor" 的子图形（录制点）另放 Icon.<名字>.Fill；
  · <path d> 全部换算成绝对坐标（M L H V C Q A Z；S / T 展开成 C / Q），多个 <path> 拼在一起时不会互相影响；
  · <circle> 拆成两段圆弧；<rect>（带 rx 时）转成圆弧路径；<line> / <polyline> / <polygon> / <ellipse> 同理；
  · logo 的 viewBox 是 24×28（写进 IconSet.ViewBoxes），其它都是 24×24；
  · 旧界面还在用的 9 个键（Ico*，pc-control.html 的图标；程序编辑 / 性能测试的旧视图引用）原样保留在文件末尾：几何不变，旧视图外观不变；M7 删除。
    M2 删掉了只剩核对表在用的 14 个（旧 MainView 等删除之后没人引用了）。
用法：python3 Control/tools/gen-icons.py [--mock <pc-v3 目录>] [--check]
"""
import argparse
import math
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
APP = os.path.normpath(os.path.join(HERE, '..', 'ParallelReactor.Control'))
DEFAULT_MOCK = os.path.normpath(os.path.join(HERE, '..', '..', '..', 'research', 'pc-v3'))

# 旧界面（research/pc-control.html）还在用的图标：原样照抄 M1 之前的 Styles/Icons.axaml，不做任何换算（只留旧视图引用的键；Lint/IconSetTests 核对）
LEGACY = [
    ('IcoGauge', 'M4 17a8 8 0 1 1 16 0 M12 13 17 8M7 17h10M5 14l-2-1M19 14l2-1M12 5V3 M11 13a1 1 0 1 0 2 0a1 1 0 1 0 -2 0'),
    ('IcoClock', 'M4 12a8 8 0 1 0 16 0a8 8 0 1 0 -16 0 M12 7v5l3 2'),
    ('IcoList', 'M9 6h11M9 12h11M9 18h11M4 6h.01M4 12h.01M4 18h.01'),
    ('IcoClose', 'm6 6 12 12M18 6 6 18'),
    ('IcoExpand', 'M8 3H3v5M16 3h5v5M8 21H3v-5M16 21h5v-5M3 8l6-6M21 8l-6-6M3 16l6 6M21 16l-6 6'),
    ('IcoMove', 'M12 2v20M2 12h20M12 2l-3 3M12 2l3 3M22 12l-3-3M22 12l-3 3M12 22l-3-3M12 22l3-3M2 12l3-3M2 12l3 3'),
    ('IcoSearch', 'M4 10a6 6 0 1 0 12 0a6 6 0 1 0 -12 0 m11 5 6 6M7 10h6M10 7v6'),
    ('IcoZoomOut', 'M4 10a6 6 0 1 0 12 0a6 6 0 1 0 -12 0 m11 5 6 6M7 10h6'),
    ('IcoTree', 'M12 3v5M5 13v5M19 13v5M5 13h14M12 8v5M5 8h14 M10 4a2 2 0 1 0 4 0a2 2 0 1 0 -4 0 M3 20a2 2 0 1 0 4 0a2 2 0 1 0 -4 0 M17 20a2 2 0 1 0 4 0a2 2 0 1 0 -4 0'),
]


def fmt(v):
    v = round(v, 4)
    if v == 0:
        v = 0.0
    s = ('%.4f' % v).rstrip('0').rstrip('.')
    return '0' if s in ('-0', '') else s


# ---------------- ICONS 解析 ----------------

def parse_icons(js):
    m = re.search(r'const ICONS = \{(.*?)\n  \};', js, flags=re.S)
    if not m:
        raise SystemExit('ui.js 里找不到 const ICONS = { … };')
    body = m.group(1)
    out = []
    # name: '…'  或  name: ['viewBox', '…']
    pat = re.compile(r"([A-Za-z_][A-Za-z0-9_]*)\s*:\s*(?:\[\s*'([^']*)'\s*,\s*'([^']*)'\s*\]|'([^']*)')")
    pos = 0
    while True:
        mm = pat.search(body, pos)
        if not mm:
            break
        name = mm.group(1)
        if mm.group(4) is not None:
            vb, svg = '0 0 24 24', mm.group(4)
        else:
            vb, svg = mm.group(2), mm.group(3)
        out.append((name, vb, svg))
        pos = mm.end()
    return out


def attrs(s):
    return dict(re.findall(r'([a-zA-Z][\w-]*)="([^"]*)"', s))


# ---------------- 路径换算（绝对坐标） ----------------

NUM = re.compile(r'[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?')
CMD_ARGS = {'M': 2, 'L': 2, 'H': 1, 'V': 1, 'C': 6, 'S': 4, 'Q': 4, 'T': 2, 'A': 7, 'Z': 0}


def tokenize(d):
    i, n = 0, len(d)
    toks = []
    while i < n:
        c = d[i]
        if c in 'MmLlHhVvCcSsQqTtAaZz':
            toks.append(c); i += 1
        elif c in ' ,\t\r\n':
            i += 1
        else:
            m = NUM.match(d, i)
            if not m:
                raise SystemExit(f'无法解析路径：…{d[i:i + 20]}')
            toks.append(m.group()); i = m.end()
    return toks


def parse_path(d):
    """SVG 路径 → [(命令, [参数])]，每段都是显式命令；弧线的两个标志位按单个数字读（兼容「a1 1 0 011 1」）。"""
    toks = tokenize(d)
    segs = []
    i = 0
    cmd = None
    first = True
    while i < len(toks):
        t = toks[i]
        if t in 'MmLlHhVvCcSsQqTtAaZz' and len(t) == 1 and not t[0].isdigit():
            cmd = t; i += 1
            if cmd in 'Zz':
                segs.append((cmd, [])); continue
        elif cmd is None:
            raise SystemExit('路径不以命令开头：' + d)
        k = CMD_ARGS[cmd.upper()]
        args = []
        while len(args) < k:
            if i >= len(toks) or toks[i] in 'MmLlHhVvCcSsQqTtAaZz':
                raise SystemExit(f'命令 {cmd} 参数不足：{d}')
            tok = toks[i]
            if cmd in 'Aa' and len(args) in (3, 4):
                # 标志位只占一个字符，可能与后面的数字连写（「011」= 0、1、1；「01.5」= 0、1、.5）
                if tok[0] not in '01':
                    raise SystemExit(f'弧线标志位不是 0 / 1：{d}')
                args.append(float(tok[0]))
                if len(tok) > 1: toks[i] = tok[1:]
                else: i += 1
                continue
            args.append(float(tok)); i += 1
        segs.append((cmd, args))
        # moveto 之后的隐式坐标对是 lineto
        if cmd == 'M':
            cmd = 'L'
        elif cmd == 'm':
            cmd = 'l'
        first = False
    return segs


def to_absolute(segs):
    """→ 绝对坐标的段列表（M L H V C Q A Z），S / T 展开为 C / Q。每个 <path> 从 (0,0) 开始（SVG 语义）。"""
    out = []
    cx = cy = 0.0          # 当前点
    sx = sy = 0.0          # 子路径起点
    last_c = None          # 上一段三次曲线的第二控制点（S 用）
    last_q = None          # 上一段二次曲线的控制点（T 用）
    for cmd, a in segs:
        rel = cmd.islower()
        C = cmd.upper()
        if C == 'M':
            x, y = (cx + a[0], cy + a[1]) if rel else (a[0], a[1])
            out.append(('M', [x, y])); cx, cy = x, y; sx, sy = x, y; last_c = last_q = None
        elif C == 'L':
            x, y = (cx + a[0], cy + a[1]) if rel else (a[0], a[1])
            out.append(('L', [x, y])); cx, cy = x, y; last_c = last_q = None
        elif C == 'H':
            x = cx + a[0] if rel else a[0]
            out.append(('H', [x])); cx = x; last_c = last_q = None
        elif C == 'V':
            y = cy + a[0] if rel else a[0]
            out.append(('V', [y])); cy = y; last_c = last_q = None
        elif C == 'C':
            p = [cx + a[0], cy + a[1], cx + a[2], cy + a[3], cx + a[4], cy + a[5]] if rel else list(a)
            out.append(('C', p)); last_c = (p[2], p[3]); last_q = None; cx, cy = p[4], p[5]
        elif C == 'S':
            x1, y1 = (2 * cx - last_c[0], 2 * cy - last_c[1]) if last_c else (cx, cy)
            p2 = [cx + a[0], cy + a[1], cx + a[2], cy + a[3]] if rel else list(a)
            p = [x1, y1] + p2
            out.append(('C', p)); last_c = (p[2], p[3]); last_q = None; cx, cy = p[4], p[5]
        elif C == 'Q':
            p = [cx + a[0], cy + a[1], cx + a[2], cy + a[3]] if rel else list(a)
            out.append(('Q', p)); last_q = (p[0], p[1]); last_c = None; cx, cy = p[2], p[3]
        elif C == 'T':
            x1, y1 = (2 * cx - last_q[0], 2 * cy - last_q[1]) if last_q else (cx, cy)
            x, y = (cx + a[0], cy + a[1]) if rel else (a[0], a[1])
            out.append(('Q', [x1, y1, x, y])); last_q = (x1, y1); last_c = None; cx, cy = x, y
        elif C == 'A':
            x, y = (cx + a[5], cy + a[6]) if rel else (a[5], a[6])
            out.append(('A', [a[0], a[1], a[2], a[3], a[4], x, y])); cx, cy = x, y; last_c = last_q = None
        elif C == 'Z':
            out.append(('Z', [])); cx, cy = sx, sy; last_c = last_q = None
    return out


def serialize(segs):
    parts = []
    for c, a in segs:
        if c == 'A':
            parts.append('A%s %s %s %d %d %s %s' % (fmt(a[0]), fmt(a[1]), fmt(a[2]), int(a[3]), int(a[4]), fmt(a[5]), fmt(a[6])))
        elif c == 'Z':
            parts.append('Z')
        else:
            parts.append(c + ' '.join(fmt(v) for v in a))
    return ' '.join(parts)


def circle(cx, cy, r):
    return [('M', [cx - r, cy]), ('A', [r, r, 0, 1, 0, cx + r, cy]), ('A', [r, r, 0, 1, 0, cx - r, cy]), ('Z', [])]


def ellipse(cx, cy, rx, ry):
    return [('M', [cx - rx, cy]), ('A', [rx, ry, 0, 1, 0, cx + rx, cy]), ('A', [rx, ry, 0, 1, 0, cx - rx, cy]), ('Z', [])]


def rect(x, y, w, h, rx, ry):
    if rx <= 0 and ry <= 0:
        return [('M', [x, y]), ('H', [x + w]), ('V', [y + h]), ('H', [x]), ('Z', [])]
    rx = min(rx if rx > 0 else ry, w / 2); ry = min(ry if ry > 0 else rx, h / 2)
    return [('M', [x + rx, y]), ('H', [x + w - rx]), ('A', [rx, ry, 0, 0, 1, x + w, y + ry]),
            ('V', [y + h - ry]), ('A', [rx, ry, 0, 0, 1, x + w - rx, y + h]),
            ('H', [x + rx]), ('A', [rx, ry, 0, 0, 1, x, y + h - ry]),
            ('V', [y + ry]), ('A', [rx, ry, 0, 0, 1, x + rx, y]), ('Z', [])]


def element_segs(tag, a):
    f = lambda k, d=0.0: float(a.get(k, d))
    if tag == 'path':
        return to_absolute(parse_path(a['d']))
    if tag == 'circle':
        return circle(f('cx'), f('cy'), f('r'))
    if tag == 'ellipse':
        return ellipse(f('cx'), f('cy'), f('rx'), f('ry'))
    if tag == 'rect':
        rx = f('rx', -1); ry = f('ry', -1)
        if rx < 0 and ry < 0: rx = ry = 0
        elif rx < 0: rx = ry
        elif ry < 0: ry = rx
        return rect(f('x'), f('y'), f('width'), f('height'), rx, ry)
    if tag == 'line':
        return [('M', [f('x1'), f('y1')]), ('L', [f('x2'), f('y2')])]
    if tag in ('polyline', 'polygon'):
        nums = [float(v) for v in NUM.findall(a['points'])]
        pts = list(zip(nums[0::2], nums[1::2]))
        segs = [('M', list(pts[0]))] + [('L', list(p)) for p in pts[1:]]
        return segs + ([('Z', [])] if tag == 'polygon' else [])
    raise SystemExit('不支持的元素 ' + tag)


def convert(name, svg):
    stroke, fill = [], []
    for m in re.finditer(r'<(path|circle|rect|line|polyline|polygon|ellipse)\b([^>]*?)/?>', svg):
        tag, a = m.group(1), attrs(m.group(2))
        segs = element_segs(tag, a)
        f = a.get('fill', '').strip().lower()
        if f and f != 'none':
            if f != 'currentcolor':
                raise SystemExit(f'图标 {name}：只允许 fill="currentColor"（色板封闭），实际 {f}')
            fill.append(serialize(segs))
        else:
            stroke.append(serialize(segs))
        for bad in ('stroke', 'transform', 'opacity', 'stroke-width'):
            if bad in a:
                raise SystemExit(f'图标 {name}：不支持属性 {bad}')
    return ' '.join(stroke), ' '.join(fill)


# ---------------- 输出 ----------------

def gen_axaml(icons, converted):
    L = ['<ResourceDictionary xmlns="https://github.com/avaloniaui"',
         '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
         '  <!-- 生成文件：Control/tools/gen-icons.py ← MOCK/js/ui.js 的 ICONS。不要手改，改样稿后重新生成。',
         f'       pc-v3 图标 {len(icons)} 个：24 网格线描（logo 的 viewBox 24×28），键 Icon.名字；fill="currentColor" 的子图形另放 Icon.名字.Fill。',
         '       坐标全部换算成绝对值；circle 拆成两段圆弧，带 rx 的 rect 转成圆弧路径。',
         '       Controls/IconView 按 Size 缩放几何、描边恒为 1.6 px，颜色继承 Foreground（样稿的 currentColor）。 -->']
    for (name, vb, _), (stroke, fill) in zip(icons, converted):
        if stroke:
            L.append(f'  <StreamGeometry x:Key="Icon.{name}">{stroke}</StreamGeometry>')
        if fill:
            L.append(f'  <StreamGeometry x:Key="Icon.{name}.Fill">{fill}</StreamGeometry>')
    L.append('')
    L.append(f'  <!-- 旧界面（research/pc-control.html）还在用的 {len(LEGACY)} 个图标，键 Ico*：几何原样保留（旧视图外观不变，旧 IconView 用法按 viewBox 单位描边），M7 删除。 -->')
    for k, v in LEGACY:
        L.append(f'  <StreamGeometry x:Key="{k}">{v}</StreamGeometry>')
    L.append('</ResourceDictionary>')
    return '\n'.join(L) + '\n'


def gen_cs(icons, converted):
    names = [n for n, _, _ in icons]
    vbs = [(n, vb) for n, vb, _ in icons if vb.split() != ['0', '0', '24', '24']]
    fills = [n for (n, _, _), (_, f) in zip(icons, converted) if f]
    L = ['// <auto-generated>',
         '// 生成文件：Control/tools/gen-icons.py ← MOCK/js/ui.js 的 ICONS。不要手改，改样稿后重新生成。',
         '// </auto-generated>',
         '#nullable enable',
         'using System.Collections.Generic;',
         '',
         'namespace ParallelReactor.Control.Controls;',
         '',
         '/// <summary>pc-v3 图标表（与 Styles/Icons.axaml 同源）：名字、非 24×24 的 viewBox、带填充子图形的图标。</summary>',
         'public static partial class IconSet',
         '{',
         f'    /// <summary>全部 {len(names)} 个图标名（同 ui.ICON_NAMES 的顺序）；资源键 = Icon.名字。</summary>',
         '    public static readonly IReadOnlyList<string> Names = new[]',
         '    {']
    for i in range(0, len(names), 10):
        L.append('        ' + ' '.join(f'"{n}",' for n in names[i:i + 10]))
    L += ['    };',
          '',
          '    /// <summary>viewBox 不是 0 0 24 24 的图标（x, y, 宽, 高）。</summary>',
          '    public static readonly IReadOnlyDictionary<string, (double X, double Y, double W, double H)> ViewBoxes = new Dictionary<string, (double, double, double, double)>',
          '    {']
    for n, vb in vbs:
        x, y, w, h = vb.split()
        L.append(f'        ["{n}"] = ({x}, {y}, {w}, {h}),')
    L += ['    };',
          '',
          '    /// <summary>带填充子图形的图标（资源键 Icon.名字.Fill，始终实心）。</summary>',
          '    public static readonly IReadOnlyList<string> WithFill = new[] { ' + ', '.join(f'"{n}"' for n in fills) + ' };',
          '}',
          '']
    return '\n'.join(L)


def write(path, text, check):
    old = open(path, encoding='utf-8').read() if os.path.exists(path) else None
    rel = os.path.relpath(path, APP)
    if old == text:
        print('unchanged', rel); return False
    if check:
        print('DIFFERS  ', rel); return True
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write(text)
    print('wrote    ', rel)
    return True


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--mock', default=DEFAULT_MOCK)
    ap.add_argument('--check', action='store_true')
    a = ap.parse_args()
    js = open(os.path.join(a.mock, 'js', 'ui.js'), encoding='utf-8').read()
    icons = parse_icons(js)
    if len(icons) != len({n for n, _, _ in icons}):
        raise SystemExit('图标名重复')
    converted = [convert(n, svg) for n, _, svg in icons]
    for (n, _, _), (s, f) in zip(icons, converted):
        if not s and not f:
            raise SystemExit(f'图标 {n} 没有几何')
    d1 = write(os.path.join(APP, 'Styles', 'Icons.axaml'), gen_axaml(icons, converted), a.check)
    d2 = write(os.path.join(APP, 'Controls', 'IconSet.g.cs'), gen_cs(icons, converted), a.check)
    fills = sum(1 for _, f in converted if f)
    print(f'{len(icons)} icons ({fills} with fill sub-shape) + {len(LEGACY)} legacy keys')
    if a.check and (d1 or d2):
        sys.exit(1)


if __name__ == '__main__':
    main()
