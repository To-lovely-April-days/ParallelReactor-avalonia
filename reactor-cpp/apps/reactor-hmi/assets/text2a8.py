#!/usr/bin/env python3
"""把随包字体里没有的几段文字预先渲染成 A8 图（LVGL 运行时按 image_recolor 上色）。

现在只有语言弹窗里的「한국어」：Inter 和 Noto Sans SC（简体子集）都没有韩文字形。
用法（只在改了下面的表时运行一次，生成的 ui/kit/text_gen.{hpp,cpp} 直接提交）：
    python3 text2a8.py [--font /System/Library/Fonts/AppleSDGothicNeo.ttc]
需要 Pillow（只在开发机上用；板子上不需要运行这个脚本）。
"""
import argparse
import os

from PIL import Image, ImageDraw, ImageFont

# (C++ 名字, 文字, 字号 px, 字重序号（ttc 里的第几个字体）)
TEXTS = [
    ('txt_korean18', '한국어', 18, 6),  # AppleSDGothicNeo.ttc 第 6 个是 SemiBold
]


def render(text, px, font_path, index):
    f = ImageFont.truetype(font_path, px * 4, index=index)
    l, t, r, b = f.getbbox(text)
    asc, desc = f.getmetrics()
    w4, h4 = r + 8, asc + desc + 8
    im = Image.new('L', (w4, h4), 0)
    ImageDraw.Draw(im).text((4, 4), text, font=f, fill=255)
    w, h = (w4 + 3) // 4, (h4 + 3) // 4
    im = im.resize((w, h), Image.LANCZOS)
    return w, h, im.tobytes()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--font', default='/System/Library/Fonts/AppleSDGothicNeo.ttc')
    a = ap.parse_args()
    here = os.path.dirname(os.path.abspath(__file__))
    out = os.path.join(here, '..', 'ui', 'kit')
    hpp = ['// 生成文件（assets/text2a8.py），不要手改。随包字体里没有字形的几段文字，预渲染成 A8 图。',
           '#pragma once', '#include "lvgl.h"', '', 'namespace reactor::hmi::icons {']
    cpp = ['// 生成文件（assets/text2a8.py），不要手改。', '#include "text_gen.hpp"', '', 'namespace reactor::hmi::icons {', '']
    for name, text, px, idx in TEXTS:
        w, h, data = render(text, px, a.font, idx)
        hpp.append(f'extern const lv_image_dsc_t {name};  // {text}')
        rows = [', '.join(f'0x{x:02x}' for x in data[i:i + 16]) for i in range(0, len(data), 16)]
        cpp.append(f'static const uint8_t {name}_data[{len(data)}] = {{\n    ' + ',\n    '.join(rows) + '\n};')
        cpp.append(f'const lv_image_dsc_t {name} = [] {{\n    lv_image_dsc_t d{{}};\n'
                   f'    d.header.magic = LV_IMAGE_HEADER_MAGIC;\n    d.header.cf = LV_COLOR_FORMAT_A8;\n'
                   f'    d.header.w = {w};\n    d.header.h = {h};\n    d.header.stride = {w};\n'
                   f'    d.data_size = {len(data)};\n    d.data = {name}_data;\n    return d;\n}}();\n')
    hpp.append('}  // namespace reactor::hmi::icons\n')
    cpp.append('}  // namespace reactor::hmi::icons\n')
    open(os.path.join(out, 'text_gen.hpp'), 'w', encoding='utf-8').write('\n'.join(hpp))
    open(os.path.join(out, 'text_gen.cpp'), 'w', encoding='utf-8').write('\n'.join(cpp))
    print('ok', [t[0] for t in TEXTS])


if __name__ == '__main__':
    main()
