# 界面字体（随程序一起发布，不依赖系统字体）

| 文件 | 用途 | 来源 / 许可 |
|---|---|---|
| Inter-Regular / Medium / SemiBold / Bold.ttf | 数字、英文（主字体） | Inter 4.0（rsms/inter），SIL OFL 1.1，见 OFL-Inter.txt |
| NotoSansSC-Regular / Medium / Bold.otf | 中文（Inter 里没有的字自动回退到这里），简体字形 | Noto Sans CJK SC 子集版（notofonts/noto-cjk），SIL OFL 1.1，见 OFL-NotoSansCJK.txt |

和画布设计一致（设计里写的是 `Inter, Noto Sans SC`）。界面程序按这个顺序找字体目录：
`--fonts 参数` → 程序所在目录的 `fonts/` → `/opt/reactor/fonts` → 工程里的 `assets/fonts`。
