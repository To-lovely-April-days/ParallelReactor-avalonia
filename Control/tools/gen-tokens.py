#!/usr/bin/env python3
"""gen-tokens.py — 色板令牌：MOCK/css/base.css :root → Styles/Tokens.axaml + Theme/Palette.cs 的「生成区」。

只用 Python 标准库。规则（PORT-PLAN §4.1 / §4.2）：
  · 颜色令牌 = :root 里值为 #rrggbb 的变量，必须与 js/core.js 的 PR.PALETTE 一一对应（67 个，名字和值都相同），否则报错退出；
  · --demo-* 是原型工具（演示条）的颜色，不是产品，跳过；
  · 键名：-- 后的 kebab 名转 PascalCase + Color / Brush（--bg-app → BgAppColor / BgAppBrush，--a-d → ADColor，--t-1 → T1Brush）；
  · 值原样照抄 CSS 文本（小写 #rrggbb），杂项 rgba() 换成 #aarrggbb（alpha = round(a × 255)），阴影换成 Avalonia BoxShadow 文本。
只改两个文件里 BEGIN GENERATED / END GENERATED 标记之间的内容；标记外的手写部分不动。

用法：python3 Control/tools/gen-tokens.py [--mock <pc-v3 目录>] [--check]
  --check：只比较，不写文件（有差异时退出码 1）。
"""
import argparse
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
APP = os.path.normpath(os.path.join(HERE, '..', 'ParallelReactor.Control'))
DEFAULT_MOCK = os.path.normpath(os.path.join(HERE, '..', '..', '..', 'research', 'pc-v3'))

# 分组：与 MOCK js/pages/placeholder.js 的 #/gallery 色板分组一致（前缀匹配，按 PR.PALETTE 的顺序）
GROUPS = [('底色', ('--bg-',)), ('线', ('--line',)), ('文字', ('--t-',)), ('强调色', ('--a', '--side-')), ('运行（浅绿）', ('--g-',)),
          ('报警（暗红）', ('--r',)), ('中性状态', ('--n-',)), ('曲线', ('--ch-',)), ('釜体', ('--v-',))]
# 杂项（PR.PALETTE_MISC + 阴影 pop；brief §2 Misc）
MISC_COLORS = ['toast', 'overlay']
SHADOWS = [('shadow-card', 'ShadowCard'), ('shadow-pop', 'ShadowPop'), ('shadow-dlg', 'ShadowDlg')]


def pascal(name):
    return ''.join(p[:1].upper() + p[1:] for p in name.split('-'))


def group_of(name):
    # 与样稿相同的前缀规则：t.indexOf(prefix) === 0
    for title, prefixes in GROUPS:
        if any(('--' + name).startswith(p) for p in prefixes):
            return title
    raise SystemExit(f'令牌 --{name} 不属于任何分组')


def strip_comments(s):
    return re.sub(r'/\*.*?\*/', '', s, flags=re.S)


def parse_root(css):
    m = re.search(r':root\s*\{(.*?)\n\}', css, flags=re.S)
    if not m:
        raise SystemExit('base.css 里找不到 :root { … }')
    body = strip_comments(m.group(1))
    out = []
    for vm in re.finditer(r'--([a-z0-9-]+)\s*:\s*([^;]+);', body):
        out.append((vm.group(1), vm.group(2).strip()))
    return out


def parse_palette_js(js):
    m = re.search(r'PR\.PALETTE\s*=\s*\{(.*?)\};', js, flags=re.S)
    if not m:
        raise SystemExit('core.js 里找不到 PR.PALETTE')
    body = strip_comments(m.group(1))
    return [(k, v) for k, v in re.findall(r"'--([a-z0-9-]+)'\s*:\s*'(#[0-9a-fA-F]{6})'", body)]


def rgba_to_argb(v):
    m = re.fullmatch(r'rgba\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*([\d.]+)\s*\)', v)
    if not m:
        raise SystemExit(f'无法解析颜色 {v}')
    r, g, b = (int(m.group(i)) for i in (1, 2, 3))
    a = int(float(m.group(4)) * 255 + 0.5)
    return '#%02x%02x%02x%02x' % (a, r, g, b), (r, g, b)


def shadow_to_avalonia(v):
    # CSS「x y blur [spread] rgba(...)」→ Avalonia「x y blur spread #aarrggbb」
    m = re.fullmatch(r'(-?[\d.]+)(?:px)?\s+(-?[\d.]+)(?:px)?\s+([\d.]+)(?:px)?(?:\s+(-?[\d.]+)(?:px)?)?\s+(rgba\([^)]*\))', v.strip())
    if not m:
        raise SystemExit(f'无法解析阴影 {v}')
    argb, rgb = rgba_to_argb(m.group(5))
    spread = m.group(4) or '0'
    return f'{m.group(1)} {m.group(2)} {m.group(3)} {spread} {argb}', rgb


def build(mock):
    css = open(os.path.join(mock, 'css', 'base.css'), encoding='utf-8').read()
    js = open(os.path.join(mock, 'js', 'core.js'), encoding='utf-8').read()
    root = parse_root(css)
    rootd = dict(root)
    colors = [(k, v) for k, v in root if re.fullmatch(r'#[0-9a-fA-F]{6}', v) and not k.startswith('demo-')]
    pal = parse_palette_js(js)
    if [k for k, _ in colors] != [k for k, _ in pal]:
        raise SystemExit('base.css :root 的颜色令牌与 core.js PR.PALETTE 不一致（名字 / 顺序）：\n  css=%s\n  js =%s' % ([k for k, _ in colors], [k for k, _ in pal]))
    for (k, v), (_, jv) in zip(colors, pal):
        if v.lower() != jv.lower():
            raise SystemExit(f'--{k}: base.css {v} ≠ PR.PALETTE {jv}')
    if len(colors) != 67:
        raise SystemExit(f'颜色令牌应为 67 个，实际 {len(colors)}')
    focus = rootd.get('focus', '')
    fm = re.fullmatch(r'var\(--([a-z0-9-]+)\)', focus)
    if not fm:
        raise SystemExit(f'--focus 不是 var(--…)：{focus}')
    misc = [(k, rgba_to_argb(rootd[k])) for k in MISC_COLORS]
    shadows = [(k, key, shadow_to_avalonia(rootd[k])) for k, key in SHADOWS]
    return colors, misc, shadows, fm.group(1)


def gen_axaml(colors, misc, shadows, focus):
    L = ['  <!-- BEGIN GENERATED COLORS（Control/tools/gen-tokens.py ← MOCK/css/base.css :root；不要手改，改样稿后重新生成） -->']
    cur = None
    for k, v in colors:
        g = group_of(k)
        if g != cur:
            L.append(f'  <!-- {g} -->')
            cur = g
        P = pascal(k)
        L.append(f'  <Color x:Key="{P}Color">{v}</Color>')
        L.append(f'  <SolidColorBrush x:Key="{P}Brush" Color="{{StaticResource {P}Color}}" />')
    L.append('  <!-- 杂项（§2 Misc：rgba → #aarrggbb） -->')
    for k, (argb, _) in misc:
        P = pascal(k)
        L.append(f'  <Color x:Key="{P}Color">{argb}</Color>')
        L.append(f'  <SolidColorBrush x:Key="{P}Brush" Color="{{StaticResource {P}Color}}" />')
    L.append(f'  <!-- 焦点环 = focus = var({focus})：2 px，画在控件外侧（行类元素画在内侧） -->')
    L.append(f'  <SolidColorBrush x:Key="FocusBrush" Color="{{StaticResource {pascal(focus)}Color}}" />')
    L.append('  <!-- 阴影（CSS「x y blur rgba」→ Avalonia BoxShadow「x y blur spread #aarrggbb」） -->')
    for k, key, (txt, _) in shadows:
        L.append(f'  <BoxShadows x:Key="{key}">{txt}</BoxShadows>')
    L.append('  <!-- END GENERATED COLORS -->')
    return '\n'.join(L)


def gen_cs(colors, misc, shadows, focus):
    I = '    '
    L = [I + '// BEGIN GENERATED COLORS（Control/tools/gen-tokens.py ← MOCK/css/base.css :root；不要手改，改样稿后重新生成）']
    cur = None
    for k, v in colors:
        g = group_of(k)
        if g != cur:
            L.append(I + f'// {g}')
            cur = g
        P = pascal(k)
        L.append(I + f'public static readonly Color {P}Color = Rgb(0x{v[1:].lower()});   // --{k}')
    L.append(I + '// 杂项（§2 Misc：rgba → #aarrggbb）')
    for k, (argb, _) in misc:
        L.append(I + f'public static readonly Color {pascal(k)}Color = Argb(0x{argb[1:]});   // --{k}')
    L.append('')
    for k, v in colors:
        P = pascal(k)
        L.append(I + f'public static readonly IImmutableSolidColorBrush {P}Brush = new ImmutableSolidColorBrush({P}Color);')
    for k, _ in misc:
        P = pascal(k)
        L.append(I + f'public static readonly IImmutableSolidColorBrush {P}Brush = new ImmutableSolidColorBrush({P}Color);')
    L.append(I + f'/// <summary>焦点环 = --focus = var(--{focus})。</summary>')
    L.append(I + f'public static readonly IImmutableSolidColorBrush FocusBrush = {pascal(focus)}Brush;')
    L.append('')
    for k, key, (txt, _) in shadows:
        L.append(I + f'public static readonly BoxShadows {key} = BoxShadows.Parse("{txt}");   // --{k}')
    L.append('')
    L.append(I + '/// <summary>67 个颜色令牌（与 PR.PALETTE 同序），分组同 MOCK #/gallery 的色板。</summary>')
    L.append(I + 'public static readonly IReadOnlyList<Token> Tokens = new Token[]')
    L.append(I + '{')
    for k, v in colors:
        L.append(I + f'    new("--{k}", "{pascal(k)}", "{group_of(k)}", {pascal(k)}Color),')
    L.append(I + '};')
    L.append('')
    L.append(I + '/// <summary>杂项（PR.PALETTE_MISC）：审计时也算合法颜色；ShadowOnly = 只许出现在阴影里。</summary>')
    L.append(I + 'public static readonly IReadOnlyList<Token> Misc = new Token[]')
    L.append(I + '{')
    misc_js = [('toast', 'Toast', False), ('overlay', 'Overlay', False), ('shadow-card', 'ShadowCard', True), ('shadow-dlg', 'ShadowDlg', True)]
    md = {k: v for k, v in misc}
    sd = {k: v for k, _, v in shadows}
    for k, P, shadow in misc_js:
        rgb = md[k][1] if k in md else sd[k][1]
        L.append(I + f'    new("--{k}", "{P}", "杂项", Color.FromRgb({rgb[0]}, {rgb[1]}, {rgb[2]}), ShadowOnly: {"true" if shadow else "false"}),')
    L.append(I + '};')
    L.append(I + '// END GENERATED COLORS')
    return '\n'.join(L)


def splice(path, new_block, begin_marker, end_marker, check):
    text = open(path, encoding='utf-8').read()
    b = text.find(begin_marker)
    e = text.find(end_marker)
    if b < 0 or e < 0:
        raise SystemExit(f'{path}：找不到生成区标记')
    b = text.rfind('\n', 0, b) + 1
    e = text.find('\n', e)
    e = len(text) if e < 0 else e
    out = text[:b] + new_block + text[e:]
    if out == text:
        print(f'unchanged {os.path.relpath(path, APP)}')
        return False
    if check:
        print(f'DIFFERS   {os.path.relpath(path, APP)}')
        return True
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write(out)
    print(f'wrote     {os.path.relpath(path, APP)}')
    return True


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--mock', default=DEFAULT_MOCK)
    ap.add_argument('--check', action='store_true')
    a = ap.parse_args()
    colors, misc, shadows, focus = build(a.mock)
    d1 = splice(os.path.join(APP, 'Styles', 'Tokens.axaml'), gen_axaml(colors, misc, shadows, focus), 'BEGIN GENERATED COLORS', 'END GENERATED COLORS', a.check)
    d2 = splice(os.path.join(APP, 'Theme', 'Palette.cs'), gen_cs(colors, misc, shadows, focus), 'BEGIN GENERATED COLORS', 'END GENERATED COLORS', a.check)
    print(f'{len(colors)} colour tokens + {len(misc)} misc colours + {len(shadows)} shadows')
    if a.check and (d1 or d2):
        sys.exit(1)


if __name__ == '__main__':
    main()
