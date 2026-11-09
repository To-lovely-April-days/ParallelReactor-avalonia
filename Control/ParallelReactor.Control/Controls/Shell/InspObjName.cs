using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Controls.Shell;

/// <summary>
/// 检查器对象标题的名称（样稿 .pr-insp-obj-n + shell.js fitInspName）：600 t-1，放不下时<b>不省略</b>（别名一样前缀的两台设备
/// 「高压加氢测试一号 / 二号」省略后就分不清了），按实际宽度逐级：18 → 16 → 15 px；反应釜再分两行「别名 / RV16」（去掉中间的「 · 」，
/// 「RV16」从不被省略），设备名最后折行（16 px，行高 1.3）。只看父元素给的宽度（R4，不来回振荡）。
/// 行高同 WebKit 取整：18 → 26、16 → 23、15 → 21（正文 1.45）；两行 / 折行时 1.3：16 → 20、15 → 19。
/// 两行时别名一行仍放不下才省略（悬停提示是全名）。
/// </summary>
public sealed class InspObjName : Panel
{
    public static readonly StyledProperty<string?> AliasProperty = AvaloniaProperty.Register<InspObjName, string?>(nameof(Alias));
    public static readonly StyledProperty<string?> RvProperty = AvaloniaProperty.Register<InspObjName, string?>(nameof(Rv));

    /// <summary>当前用的写法（测试 / 截图核对用）：「18」「16」「15」「2l-16」「2l-15」「wrap」。</summary>
    public static readonly DirectProperty<InspObjName, string> ModeProperty =
        AvaloniaProperty.RegisterDirect<InspObjName, string>(nameof(Mode), o => o.Mode);

    static InspObjName()
    {
        AffectsMeasure<InspObjName>(AliasProperty, RvProperty);
    }

    public string? Alias { get => GetValue(AliasProperty); set => SetValue(AliasProperty, value); }
    /// <summary>「RV1」；设备标题为 null。</summary>
    public string? Rv { get => GetValue(RvProperty); set => SetValue(RvProperty, value); }

    private string _mode = "18";
    public string Mode { get => _mode; private set => SetAndRaise(ModeProperty, ref _mode, value); }

    private readonly TextBlock _one = Make(), _alias = Make(), _rv = Make();

    private static TextBlock Make() => new()
    {
        FontWeight = FontWeight.SemiBold,
        Foreground = Palette.T1Brush,
        TextWrapping = TextWrapping.NoWrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public InspObjName()
    {
        _alias.IsVisible = _rv.IsVisible = false;
        Children.Add(_one);
        Children.Add(_alias);
        Children.Add(_rv);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AliasProperty || change.Property == RvProperty)
            ToolTip.SetTip(this, string.IsNullOrEmpty(Rv) ? Alias : Alias + " · " + Rv);
    }

    private static double Lh(double fs) => Math.Floor(fs * 1.45);
    private static double Lh2(double fs) => Math.Floor(fs * 1.3);

    private static void Style(TextBlock t, double fs, double lh, bool wrap, bool trim)
    {
        if (t.FontSize != fs) t.FontSize = fs;
        if (t.LineHeight != lh) t.LineHeight = lh;
        var w = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        if (t.TextWrapping != w) t.TextWrapping = w;
        var tt = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        if (t.TextTrimming != tt) t.TextTrimming = tt;
    }

    // 不在可视树上的量字用 TextBlock（隐藏的子元素量出来是 0）：字体取在树上的那个（继承来的 UiFont）
    private readonly TextBlock _probe = Make();

    private Size Probe(string text, double fs)
    {
        _probe.FontFamily = _one.FontFamily;
        _probe.FontSize = fs;
        _probe.LineHeight = Lh(fs);
        _probe.Text = text;
        _probe.Measure(Size.Infinity);
        return _probe.DesiredSize;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var alias = Alias ?? "";
        var rv = Rv;
        double avail = availableSize.Width;
        string full = string.IsNullOrEmpty(rv) ? alias : alias + "\u00a0·\u00a0" + rv;
        if (_one.Text != full) _one.Text = full;
        if (_alias.Text != alias) _alias.Text = alias;
        if (_rv.Text != rv) _rv.Text = rv;

        // 一行：18 → 16 → 15
        foreach (var fs in new[] { 18.0, 16.0, 15.0 })
        {
            if (Probe(full, fs).Width > avail + 0.5) continue;
            Style(_one, fs, Lh(fs), false, false);
            Show(one: true);
            Mode = fs.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _one.Measure(new Size(avail, double.PositiveInfinity));
            return new Size(Math.Min(_one.DesiredSize.Width, avail), _one.DesiredSize.Height);
        }
        if (!string.IsNullOrEmpty(rv))
        {
            // 反应釜：两行「别名 / RV16」，16 → 15；还放不下时别名一行省略（RV 行不省略）
            foreach (var fs in new[] { 16.0, 15.0 })
            {
                if (fs == 16 && (Probe(alias, fs).Width > avail + 0.5 || Probe(rv!, fs).Width > avail + 0.5)) continue;
                Style(_alias, fs, Lh2(fs), false, true);
                Style(_rv, fs, Lh2(fs), false, false);
                Show(one: false);
                Mode = "2l-" + fs.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _alias.Measure(new Size(avail, double.PositiveInfinity));
                _rv.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                return new Size(Math.Min(Math.Max(_alias.DesiredSize.Width, _rv.DesiredSize.Width), avail), _alias.DesiredSize.Height + _rv.DesiredSize.Height);
            }
        }
        // 设备名：16 px 折行（行高 1.3）
        Style(_one, 16, Lh2(16), true, false);
        Show(one: true);
        Mode = "wrap";
        _one.Measure(new Size(avail, double.PositiveInfinity));
        return new Size(Math.Min(_one.DesiredSize.Width, avail), _one.DesiredSize.Height);
    }

    private void Show(bool one)
    {
        if (_one.IsVisible != one) _one.IsVisible = one;
        if (_alias.IsVisible == one) _alias.IsVisible = !one;
        if (_rv.IsVisible == one) _rv.IsVisible = !one;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (_one.IsVisible)
        {
            _one.Arrange(new Rect(0, 0, finalSize.Width, Math.Max(finalSize.Height, _one.DesiredSize.Height)));
        }
        else
        {
            double h1 = _alias.DesiredSize.Height;
            _alias.Arrange(new Rect(0, 0, finalSize.Width, h1));
            _rv.Arrange(new Rect(0, h1, finalSize.Width, _rv.DesiredSize.Height));
        }
        return finalSize;
    }
}
