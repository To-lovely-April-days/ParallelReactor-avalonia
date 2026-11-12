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
    // 样稿 placeholder.js galleryHTML 的 type 表：（资源键, 字重, 说明, 样例, 等宽数字）
    private static readonly (string Key, int Weight, string Label, string Sample, bool Numeric)[] Spec =
    {
        ("FsNum", 400, "数字 / 单位", "119.8 ℃ · 0.349 MPa · 192.168.1.20", true),
        ("Fs2", 400, "次要文字", "本地 · 只读 · 第 3/4 步 · 剩 42 min", false),
        ("FsBody", 500, "正文 / 按钮", "启动加热 · 下发程序 · 1号机 · RV1", false),
        ("FsTitle", 600, "面板标题", "属性查看器", false),
        ("FsHead", 600, "分区头 / 对话框标题", "1号机 HT-PR8 · 现场", false),
        ("FsRead", 600, "主要读数", "119.8 ℃", true),
    };

    private static IReadOnlyList<TypeSample>? _rows;

    /// <summary>字号来自 Application 资源（Tokens.axaml），所以样例永远等于令牌的实际值。</summary>
    public static IReadOnlyList<TypeSample> Rows => _rows ??= Spec.Select((s, i) =>
    {
        var size = Token(s.Key);
        var caption = $"{size.ToString(CultureInfo.InvariantCulture)} px{(s.Weight >= 600 ? " " + s.Weight : "")} · {s.Label}";
        return new TypeSample(caption, Token("Fs2"), size, (FontWeight)s.Weight, s.Sample, s.Numeric, new Thickness(0, 0, 0, i == Spec.Length - 1 ? 0 : 1));
    }).ToList();

    private static double Token(string key) => Application.Current!.TryFindResource(key, out var v) && v is double d ? d : double.NaN;

    public TypeScaleSection() => InitializeComponent();
}
