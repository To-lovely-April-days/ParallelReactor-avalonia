using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ParallelReactor.Control.Views.Pages.Gallery;

/// <summary>字号表的一行：字号来自资源键（Fs*），说明里的「13 px」也由令牌值生成。</summary>
public sealed record TypeSample(string Caption, double CaptionSize, double Size, FontWeight Weight, string Sample, bool Numeric, Thickness Border)
{
    /// <summary>行高 1.45（样稿 body line-height），同 WebKit 截成整数 px（参照图的行高）。</summary>
    public double LineHeight => Math.Floor(Size * 1.45);
    public double CaptionLineHeight => Math.Floor(CaptionSize * 1.45);
}

public partial class TypeScaleSection : UserControl
{
    // 融合版样稿 main-fusion.html 的字号（tools/fusion-tokens.json 的语义字号）：（资源键, 字重, 说明, 样例, 等宽数字）
    private static readonly (string Key, int Weight, string Label, string Sample, bool Numeric)[] Spec =
    {
        ("FsBase", 400, "正文 / 标签 / 按钮 / 工具条", "设备 · 控制权 · 打标记 · 停止1号机", false),
        ("FsCell", 400, "运行表 / 表格 / 输入框", "恒温反应 3/4 · 本步剩余 27 min", false),
        ("FsValue", 600, "读数（运行表）", "145.2 °C · 2.35 MPa · 18.4 mmol", true),
        ("FsPaneTitle", 600, "面板标题", "程序库 · 报警与事件", false),
        ("FsDlgTitle", 300, "对话框标题（细体）", "急停 · 远程停机", false),
        ("FsDevName", 300, "设备名（细体）", "1号机  HT-PR8", false),
        ("FsMenu", 300, "菜单栏（当前页 400）", "主页 程序 曲线 报警", false),
        ("FsPopValue", 600, "报警弹窗读数", "3.55 MPa", true),
    };

    private static IReadOnlyList<TypeSample>? _rows;

    /// <summary>字号来自 Application 资源（Tokens.axaml），所以样例永远等于令牌的实际值。</summary>
    public static IReadOnlyList<TypeSample> Rows => _rows ??= Spec.Select((s, i) =>
    {
        var size = Token(s.Key);
        var caption = $"{size.ToString(CultureInfo.InvariantCulture)} px{(s.Weight >= 600 ? " " + s.Weight : "")} · {s.Label}";
        return new TypeSample(caption, Token("FsBase"), size, (FontWeight)s.Weight, s.Sample, s.Numeric, new Thickness(0, 0, 0, i == Spec.Length - 1 ? 0 : 1));
    }).ToList();

    private static double Token(string key) => Application.Current!.TryFindResource(key, out var v) && v is double d ? d : double.NaN;

    public TypeScaleSection() => InitializeComponent();
}
