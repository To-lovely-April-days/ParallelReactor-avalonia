#!/usr/bin/env python3
"""gen-fluent-icons.py — 从融合版样稿 research/main-fusion.html 的内嵌 <symbol id="i-…">（Microsoft Fluent UI System Icons 1.1.346，MIT）
生成 Control/ParallelReactor.Control/Styles/FluentIcons.axaml（融合版 F2）。

键 = Icon.f-名字（样稿的 i-名字）；viewBox 0 0 20 20、填充（currentColor）。Controls/IconView 认「f-」开头的名字：按 Size 缩放、只填充不描边。
StreamGeometry 默认是奇偶填充，前面加 F1（非零，同 SVG 默认）。
用法：python3 Control/tools/gen-fluent-icons.py [--check]
"""
import re, sys, pathlib
HERE = pathlib.Path(__file__).resolve().parent
ROOT = HERE.parents[1]
MOCK = ROOT.parent / "research" / "main-fusion.html"
OUT = HERE.parent / "ParallelReactor.Control" / "Styles" / "FluentIcons.axaml"

def main():
    html = MOCK.read_text(encoding="utf-8")
    syms = re.findall(r'<symbol id="i-([\w-]+)" viewBox="0 0 20 20" fill="currentColor">(.*?)</symbol>', html, re.S)
    lines = []
    for name, body in syms:
        paths = re.findall(r'<path d="([^"]+)"', body)
        if not paths: continue
        d = " ".join(paths)
        lines.append(f'  <StreamGeometry x:Key="Icon.f-{name}">F1 {d}</StreamGeometry>')
    text = ('<ResourceDictionary xmlns="https://github.com/avaloniaui"\n'
            '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">\n'
            '  <!-- 生成文件：Control/tools/gen-fluent-icons.py ← research/main-fusion.html 的内嵌图标（Microsoft Fluent UI System Icons 1.1.346，MIT）。不要手改。\n'
            f'       融合版图标 {len(lines)} 个：20 网格、填充，键 Icon.f-名字；Controls/IconView 认「f-」开头的名字（按 Size 缩放、只填充）。 -->\n'
            + "\n".join(lines) + "\n</ResourceDictionary>\n")
    if "--check" in sys.argv:
        ok = OUT.exists() and OUT.read_text(encoding="utf-8") == text
        print("FluentIcons.axaml " + ("一致" if ok else "与样稿不一致，重新生成"))
        sys.exit(0 if ok else 1)
    OUT.write_text(text, encoding="utf-8")
    print(f"写入 {OUT}（{len(lines)} 个图标）")

if __name__ == "__main__":
    main()
