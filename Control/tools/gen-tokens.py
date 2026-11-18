#!/usr/bin/env python3
"""gen-tokens.py — 融合版设计令牌：tools/fusion-tokens.json → Styles/Tokens.axaml + Theme/Palette.cs 的「生成区」（F1）。

只用 Python 标准库。来源与规则（research/fusion-port/PORT-PLAN §4 F1、notes/F1-handover.md）：
  · fusion-tokens.json 是从样稿 research/main-fusion.html 抽出的最终值（10 层 :root 层叠之后 + 规则里写死的颜色）。
    colors = 封闭色板（每个颜色一个 Color + Brush）；aliases = 新概念（面板、竖条、工具条、运行表、胶囊、报警弹窗 …）的语义画刷，只指向色板里的颜色；
    misc = 色板外但合法的杂项（提示条、遮罩）；shadows = 阴影（CSS 写法）；focus = 焦点环；sizes = 字号 / 尺寸 / 圆角 / 过渡。
  · 键名：kebab 名转 PascalCase + Color / Brush（bg-app → BgAppColor / BgAppBrush，a-d → ADBrush，t-1 → T1Brush，ch-7 → Ch7Brush）。
  · 值原样照抄（小写 #rrggbb），rgba() 换成 #aarrggbb（alpha = round(a × 255)），阴影换成 Avalonia BoxShadows 文本（多层用逗号）。
  · --check-mock（默认开，样稿不在时跳过）：每个颜色的值必须出现在样稿里；出处写了「:root --x」的，还要等于 --x 层叠之后的最终值；
    misc / 阴影的 rgb 必须在样稿里出现过。核对不过就报错退出 —— 令牌不能凭空造。
只改两个文件里 BEGIN GENERATED … / END GENERATED … 标记之间的内容；标记外的手写部分不动。

用法：python3 Control/tools/gen-tokens.py [--json <fusion-tokens.json>] [--mock <main-fusion.html>] [--no-mock] [--check] [--dump-root]
  --check：只比较，不写文件（有差异时退出码 1）。--dump-root：打印样稿 :root 层叠之后的最终值（核对用）。
"""
import argparse
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
APP = os.path.normpath(os.path.join(HERE, '..', 'ParallelReactor.Control'))
DEFAULT_JSON = os.path.join(HERE, 'fusion-tokens.json')
DEFAULT_MOCK = os.path.normpath(os.path.join(HERE, '..', '..', '..', 'research', 'main-fusion.html'))

HEX6 = re.compile(r'#[0-9a-f]{6}')


def pascal(name):
    return ''.join(p[:1].upper() + p[1:] for p in name.split('-'))


def strip_comments(s):
    return re.sub(r'/\*.*?\*/', '', s, flags=re.S)


def norm_hex(v):
    """#abc / #aabbcc → #aabbcc（小写）；其它返回 None。"""
    v = v.strip().lower()
    if re.fullmatch(r'#[0-9a-f]{3}', v):
        return '#' + ''.join(c * 2 for c in v[1:])
    if re.fullmatch(r'#[0-9a-f]{6}', v):
        return v
    return None


def parse_rgba(v):
    m = re.fullmatch(r'rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*(?:,\s*([\d.]+)\s*)?\)', v.strip())
    if not m:
        raise SystemExit(f'无法解析颜色 {v}')
    r, g, b = (int(m.group(i)) for i in (1, 2, 3))
    a = float(m.group(4)) if m.group(4) is not None else 1.0
    return (r, g, b), a


def argb(rgb, a):
    return '#%02x%02x%02x%02x' % (int(a * 255 + 0.5), rgb[0], rgb[1], rgb[2])


def split_top(s):
    """按不在括号里的逗号切开（多层阴影）。"""
    out, depth, cur = [], 0, ''
    for ch in s:
        if ch == '(':
            depth += 1
        elif ch == ')':
            depth -= 1
        if ch == ',' and depth == 0:
            out.append(cur)
            cur = ''
        else:
            cur += ch
    out.append(cur)
    return [x.strip() for x in out if x.strip()]


def shadow_to_avalonia(css):
    """CSS「x y blur [spread] rgba(...)」（可多层）→ Avalonia「x y blur spread #aarrggbb, …」；同时返回各层的 rgb。"""
    parts, rgbs = [], []
    for one in split_top(css):
        m = re.fullmatch(r'(-?[\d.]+)(?:px)?\s+(-?[\d.]+)(?:px)?\s+([\d.]+)(?:px)?(?:\s+(-?[\d.]+)(?:px)?)?\s+(rgba?\([^)]*\))', one)
        if not m:
            raise SystemExit(f'无法解析阴影 {one}')
        rgb, a = parse_rgba(m.group(5))
        parts.append(f'{m.group(1)} {m.group(2)} {m.group(3)} {m.group(4) or "0"} {argb(rgb, a)}')
        rgbs.append(rgb)
    return ', '.join(parts), rgbs


# ———————————————————— 样稿核对 ————————————————————

def mock_root(html):
    """样稿里所有 :root{…} 按出现顺序层叠，返回 --name → 最终值（后写的覆盖先写的；同一块里也一样）。"""
    css = '\n'.join(re.findall(r'<style[^>]*>(.*?)</style>', html, flags=re.S))
    css = strip_comments(css)
    final = {}
    for m in re.finditer(r':root\s*\{([^}]*)\}', css):
        for vm in re.finditer(r'--([a-z0-9-]+)\s*:\s*([^;]+);?', m.group(1)):
            final[vm.group(1)] = vm.group(2).strip()
    return final


def resolve(final, v, depth=0):
    m = re.fullmatch(r'var\(--([a-z0-9-]+)\)', v.strip())
    if m and depth < 8:
        return resolve(final, final.get(m.group(1), ''), depth + 1)
    return v


def mock_colours(html):
    """样稿里出现过的全部颜色：#rgb / #rrggbb → #rrggbb，rgb(a)() → (r,g,b)。"""
    hexes = set()
    for m in re.finditer(r'#([0-9a-fA-F]{6}|[0-9a-fA-F]{3})\b', html):
        h = norm_hex('#' + m.group(1))
        if h:
            hexes.add(h)
    rgbs = set()
    for m in re.finditer(r'rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)', html):
        rgbs.add(tuple(int(m.group(i)) for i in (1, 2, 3)))
    return hexes, rgbs


def to_rgb(h):
    return tuple(int(h[i:i + 2], 16) for i in (1, 3, 5))


def check_mock(tok, html):
    errs = []
    final = mock_root(html)
    hexes, rgbs = mock_colours(html)
    for c in tok['colors']:
        h = c['hex']
        if h not in hexes and to_rgb(h) not in rgbs:
            errs.append(f'--{c["name"]} {h}：样稿里没有这个颜色')
        for var in re.findall(r':root\s+((?:--[a-z0-9-]+\s*/\s*)*--[a-z0-9-]+)', c.get('src', '')):
            for name in re.findall(r'--([a-z0-9-]+)', var):
                v = norm_hex(resolve(final, final.get(name, '')))
                if v != h:
                    errs.append(f'--{c["name"]} {h}：出处写的 :root --{name} 层叠之后是 {final.get(name, "（没有）")}')
    for m in tok['misc']:
        rgb, _ = parse_rgba(m['rgba'])
        if rgb not in rgbs and '#%02x%02x%02x' % rgb not in hexes:
            errs.append(f'misc {m["name"]} {m["rgba"]}：样稿里没有这个颜色')
    for s in tok['shadows']:
        _, srgbs = shadow_to_avalonia(s['css'])
        for rgb in srgbs:
            if rgb not in rgbs and '#%02x%02x%02x' % rgb not in hexes:
                errs.append(f'shadow {s["name"]} rgb{rgb}：样稿里没有这个颜色')
    return errs


# ———————————————————— 校验 + 生成 ————————————————————

def validate(tok):
    names = [c['name'] for c in tok['colors']]
    dup = sorted({n for n in names if names.count(n) > 1})
    if dup:
        raise SystemExit(f'颜色令牌重名：{dup}')
    for c in tok['colors']:
        if not re.fullmatch(r'[a-z][a-z0-9]*(-[a-z0-9]+)*', c['name']):
            raise SystemExit(f'令牌名不合规则（kebab）：{c["name"]}')
        if norm_hex(c['hex']) != c['hex']:
            raise SystemExit(f'--{c["name"]} 的值必须是小写 #rrggbb：{c["hex"]}')
    keys = {pascal(n) for n in names}
    if len(keys) != len(names):
        raise SystemExit('有两个令牌转成 PascalCase 后同名')
    for a in tok['aliases']:
        if a['to'] not in names:
            raise SystemExit(f'别名 {a["name"]} 指向不存在的令牌 {a["to"]}')
        if pascal(a['name']) in keys:
            raise SystemExit(f'别名 {a["name"]} 与令牌同名')
    if tok['focus']['brush'] not in names:
        raise SystemExit('focus.brush 不是色板里的令牌')
    skeys = [s['key'] for s in tok['sizes']]
    dup = sorted({k for k in skeys if skeys.count(k) > 1})
    if dup:
        raise SystemExit(f'尺寸键重名：{dup}')


def misc_list(tok):
    """杂项颜色（审计时也算合法）：misc（Toast、Overlay）+ 阴影里的颜色（ShadowOnly，去重；与 misc 同色的不重复列）。"""
    out = []
    seen = set()
    for m in tok['misc']:
        rgb, a = parse_rgba(m['rgba'])
        out.append((m['name'], pascal(m['name']), rgb, a, False))
        seen.add(rgb)
    for s in tok['shadows']:
        _, rgbs = shadow_to_avalonia(s['css'])
        for rgb in rgbs:
            if rgb in seen:
                continue
            seen.add(rgb)
            out.append((s['name'], pascal(s['name']), rgb, None, True))
    return out


def fmt_num(v):
    if isinstance(v, float) and v.is_integer():
        v = int(v)
    return str(v)


GEN_NOTE = 'Control/tools/gen-tokens.py ← tools/fusion-tokens.json（样稿 research/main-fusion.html）；不要手改，改 JSON 后重新生成'


def gen_axaml_colours(tok):
    L = [f'  <!-- BEGIN GENERATED COLORS（{GEN_NOTE}） -->']
    cur = None
    for c in tok['colors']:
        if c['group'] != cur:
            cur = c['group']
            L.append(f'  <!-- {cur} -->')
        P = pascal(c['name'])
        L.append(f'  <Color x:Key="{P}Color">{c["hex"]}</Color>')
        L.append(f'  <SolidColorBrush x:Key="{P}Brush" Color="{{StaticResource {P}Color}}" />')
    L.append('  <!-- 杂项（rgba → #aarrggbb） -->')
    for m in tok['misc']:
        rgb, a = parse_rgba(m['rgba'])
        P = pascal(m['name'])
        L.append(f'  <Color x:Key="{P}Color">{argb(rgb, a)}</Color>')
        L.append(f'  <SolidColorBrush x:Key="{P}Brush" Color="{{StaticResource {P}Color}}" />')
    f = tok['focus']
    FP = pascal(f['brush'])
    L.append(f'  <!-- 焦点：边框 = {f["brush"]}；焦点环 = {f["brush"]} × {f["ringAlpha"]}（{f["src"].split("：")[0]}） -->')
    L.append(f'  <SolidColorBrush x:Key="FocusBrush" Color="{{StaticResource {FP}Color}}" />')
    L.append(f'  <SolidColorBrush x:Key="FocusRingBrush" Color="{{StaticResource {FP}Color}}" Opacity="{f["ringAlpha"]}" />')
    L.append('  <!-- 语义画刷（新概念：面板、竖条、工具条、菜单栏、运行表、报警弹窗、开关、滚动条）：只指向色板里的颜色 -->')
    cur = None
    for a in tok['aliases']:
        if a['group'] != cur:
            cur = a['group']
            L.append(f'  <!-- {cur} -->')
        L.append(f'  <SolidColorBrush x:Key="{pascal(a["name"])}Brush" Color="{{StaticResource {pascal(a["to"])}Color}}" />')
    L.append('  <!-- 阴影（CSS「x y blur rgba」→ Avalonia BoxShadows「x y blur spread #aarrggbb」，多层用逗号） -->')
    for s in tok['shadows']:
        txt, _ = shadow_to_avalonia(s['css'])
        L.append(f'  <BoxShadows x:Key="{s["key"]}">{txt}</BoxShadows>')
    ring = argb(to_rgb(next(c['hex'] for c in tok['colors'] if c['name'] == f['brush'])), f['ringAlpha'])
    L.append(f'  <BoxShadows x:Key="FocusRingShadow">0 0 0 3 {ring}</BoxShadows>')
    L.append('  <!-- END GENERATED COLORS -->')
    return '\n'.join(L)


def gen_axaml_sizes(tok):
    L = [f'  <!-- BEGIN GENERATED SIZES（{GEN_NOTE}） -->']
    cur = None
    for s in tok['sizes']:
        if s['group'] != cur:
            cur = s['group']
            L.append(f'  <!-- {cur} -->')
        k, v, t = s['key'], s['value'], s['type']
        tag = {'double': 'x:Double', 'thickness': 'Thickness', 'corner': 'CornerRadius', 'timespan': 'x:TimeSpan'}[t]
        line = f'  <{tag} x:Key="{k}">{fmt_num(v)}</{tag}>'
        if s.get('src'):
            # XML 注释里不能有两个连续的短横
            line += f'  <!-- {s["src"].replace("--", "")} -->'
        L.append(line)
    L.append('  <!-- END GENERATED SIZES -->')
    return '\n'.join(L)


def gen_cs(tok):
    I = '    '
    L = [I + f'// BEGIN GENERATED COLORS（{GEN_NOTE}）']
    cur = None
    for c in tok['colors']:
        if c['group'] != cur:
            cur = c['group']
            L.append(I + f'// {cur}')
        L.append(I + f'public static readonly Color {pascal(c["name"])}Color = Rgb(0x{c["hex"][1:]});   // --{c["name"]}')
    L.append(I + '// 杂项（rgba → #aarrggbb）')
    for m in tok['misc']:
        rgb, a = parse_rgba(m['rgba'])
        L.append(I + f'public static readonly Color {pascal(m["name"])}Color = Argb(0x{argb(rgb, a)[1:]});   // --{m["name"]}')
    L.append('')
    for c in tok['colors']:
        P = pascal(c['name'])
        L.append(I + f'public static readonly IImmutableSolidColorBrush {P}Brush = new ImmutableSolidColorBrush({P}Color);')
    for m in tok['misc']:
        P = pascal(m['name'])
        L.append(I + f'public static readonly IImmutableSolidColorBrush {P}Brush = new ImmutableSolidColorBrush({P}Color);')
    f = tok['focus']
    FP = pascal(f['brush'])
    L.append(I + f'/// <summary>焦点：边框 = --{f["brush"]}；焦点环 = --{f["brush"]} × {f["ringAlpha"]}。</summary>')
    L.append(I + f'public static readonly IImmutableSolidColorBrush FocusBrush = {FP}Brush;')
    L.append(I + f'public static readonly IImmutableSolidColorBrush FocusRingBrush = new ImmutableSolidColorBrush({FP}Color, {f["ringAlpha"]});')
    L.append('')
    L.append(I + '// 语义画刷（新概念）：只指向色板里的颜色')
    for a in tok['aliases']:
        P, T = pascal(a['name']), pascal(a['to'])
        L.append(I + f'public static readonly Color {P}Color = {T}Color;   // {a["group"]} · {a["src"]}')
        L.append(I + f'public static readonly IImmutableSolidColorBrush {P}Brush = {T}Brush;')
    L.append('')
    for s in tok['shadows']:
        txt, _ = shadow_to_avalonia(s['css'])
        L.append(I + f'public static readonly BoxShadows {s["key"]} = BoxShadows.Parse("{txt}");   // --{s["name"]}')
    ring = argb(to_rgb(next(c['hex'] for c in tok['colors'] if c['name'] == f['brush'])), f['ringAlpha'])
    L.append(I + f'public static readonly BoxShadows FocusRingShadow = BoxShadows.Parse("0 0 0 3 {ring}");')
    L.append('')
    L.append(I + f'/// <summary>{len(tok["colors"])} 个颜色令牌（封闭色板，顺序同 fusion-tokens.json），分组同组件陈列页的色板。</summary>')
    L.append(I + 'public static readonly IReadOnlyList<Token> Tokens = new Token[]')
    L.append(I + '{')
    for c in tok['colors']:
        P = pascal(c['name'])
        L.append(I + f'    new("--{c["name"]}", "{P}", "{c["group"]}", {P}Color),')
    L.append(I + '};')
    L.append('')
    L.append(I + '/// <summary>杂项：审计时也算合法颜色；ShadowOnly = 只许出现在阴影里。</summary>')
    L.append(I + 'public static readonly IReadOnlyList<Token> Misc = new Token[]')
    L.append(I + '{')
    for name, P, rgb, _, shadow in misc_list(tok):
        L.append(I + f'    new("--{name}", "{P}", "杂项", Rgb(0x{rgb[0]:02x}{rgb[1]:02x}{rgb[2]:02x}), ShadowOnly: {"true" if shadow else "false"}),')
    L.append(I + '};')
    L.append(I + '// END GENERATED COLORS')
    return '\n'.join(L)


def splice(path, new_block, begin_marker, end_marker, check):
    text = open(path, encoding='utf-8').read()
    b = text.find(begin_marker)
    e = text.find(end_marker)
    if b < 0 or e < 0:
        raise SystemExit(f'{path}：找不到生成区标记 {begin_marker}')
    b = text.rfind('\n', 0, b) + 1
    e = text.find('\n', e)
    e = len(text) if e < 0 else e
    out = text[:b] + new_block + text[e:]
    rel = os.path.relpath(path, APP)
    if out == text:
        print(f'unchanged {rel} [{begin_marker}]')
        return False
    if check:
        print(f'DIFFERS   {rel} [{begin_marker}]')
        return True
    with open(path, 'w', encoding='utf-8', newline='\n') as f:
        f.write(out)
    print(f'wrote     {rel} [{begin_marker}]')
    return True


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--json', default=DEFAULT_JSON)
    ap.add_argument('--mock', default=DEFAULT_MOCK)
    ap.add_argument('--no-mock', action='store_true')
    ap.add_argument('--check', action='store_true')
    ap.add_argument('--dump-root', action='store_true')
    a = ap.parse_args()
    tok = json.load(open(a.json, encoding='utf-8'))
    validate(tok)
    if not a.no_mock:
        if os.path.exists(a.mock):
            html = open(a.mock, encoding='utf-8').read()
            if a.dump_root:
                for k, v in sorted(mock_root(html).items()):
                    print(f'--{k}: {v}')
                return
            errs = check_mock(tok, html)
            if errs:
                raise SystemExit('令牌与样稿对不上：\n  ' + '\n  '.join(errs))
            print(f'mock ok   {os.path.relpath(a.mock, os.path.join(HERE, "..", "..", ".."))}')
        else:
            print(f'(样稿不在 {a.mock}，跳过核对)')
    tokens = os.path.join(APP, 'Styles', 'Tokens.axaml')
    d = [splice(tokens, gen_axaml_colours(tok), 'BEGIN GENERATED COLORS', 'END GENERATED COLORS', a.check),
         splice(tokens, gen_axaml_sizes(tok), 'BEGIN GENERATED SIZES', 'END GENERATED SIZES', a.check),
         splice(os.path.join(APP, 'Theme', 'Palette.cs'), gen_cs(tok), 'BEGIN GENERATED COLORS', 'END GENERATED COLORS', a.check)]
    print(f'{len(tok["colors"])} colour tokens + {len(tok["aliases"])} aliases + {len(tok["misc"])} misc + {len(tok["shadows"])} shadows + {len(tok["sizes"])} sizes')
    if a.check and any(d):
        sys.exit(1)


if __name__ == '__main__':
    main()
