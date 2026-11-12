using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Views.Pages.Reactor;

/// <summary>
/// 「程序」面板的面包屑（样稿 reactor.js progPanel 的 .reactor-crumb + fitCrumb，css/reactor.css）：「1号机 › RV1 › 加氢-标准」。
/// <list type="bullet">
/// <item>别名与「RV1」400 t-3，分隔符 › t-4（左右各 7），程序名 600 t-1；字号继承面板标题（16）。</item>
/// <item>放不下时（CSS flex-shrink：别名段 1000 × 宽、程序名 1 × 宽，「RV1 ›」不缩）：「1号机 ›」先让位（别名先省略，再连 › 一起裁掉），
///   再不够才省略程序名（全文在面板标题的悬停提示里）；「RV16 ›」从不被切掉。</item>
/// <item>别名被挤得只剩不到 2.5 个字宽（40 px）时整段不显示（样稿 fitCrumb：「R…›」没有意义，别名在页签和检查器里都有）；
///   面板窄于等于 360（样稿 @container rvprog (max-width: 360px)，<see cref="NoDev"/>）时也不显示。</item>
/// </list>
/// 只看父元素给的宽度（R4）。各段是单独的 PrText（字重不混在一个 TextBlock 里，11.1 中文回退的字重缺陷碰不到）。
/// </summary>
public sealed class ReactorCrumb : Panel
{
    public static readonly StyledProperty<string?> AliasProperty = AvaloniaProperty.Register<ReactorCrumb, string?>(nameof(Alias));
    public static readonly StyledProperty<string?> RvProperty = AvaloniaProperty.Register<ReactorCrumb, string?>(nameof(Rv));
    public static readonly StyledProperty<string?> ProgNameProperty = AvaloniaProperty.Register<ReactorCrumb, string?>(nameof(ProgName));
    public static readonly StyledProperty<bool> NoDevProperty = AvaloniaProperty.Register<ReactorCrumb, bool>(nameof(NoDev));

    /// <summary>分隔符左右各 7（.reactor-crumb .pr-crumb-sep { margin: 0 7px }）；别名段只剩不到 2.5 个字宽时整段不显示。</summary>
    public const double SepMargin = 7, MinAliasEm = 2.5;

    private readonly PrText _alias = Part(false), _sep1 = Sep(), _rv = Part(false), _sep2 = Sep(), _name = Part(true);
    private double _aliasW, _sep1W, _nameW;
    private bool _devShown = true, _zeroed;

    static ReactorCrumb() => AffectsMeasure<ReactorCrumb>(AliasProperty, RvProperty, ProgNameProperty, NoDevProperty);

    public ReactorCrumb()
    {
        ClipToBounds = true;
        Children.Add(_alias);
        Children.Add(_sep1);
        Children.Add(_rv);
        Children.Add(_sep2);
        Children.Add(_name);
    }

    private static PrText Part(bool strong) => new()
    {
        TextTrimming = TextTrimming.CharacterEllipsis,
        FontWeight = strong ? FontWeight.SemiBold : FontWeight.Normal,
        Foreground = strong ? Palette.T1Brush : Palette.T3Brush,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
    };

    private static PrText Sep() => new() { Text = "›", FontWeight = FontWeight.Normal, Foreground = Palette.T4Brush, Margin = new Thickness(SepMargin, 0) };

    public string? Alias { get => GetValue(AliasProperty); set => SetValue(AliasProperty, value); }
    public string? Rv { get => GetValue(RvProperty); set => SetValue(RvProperty, value); }
    public string? ProgName { get => GetValue(ProgNameProperty); set => SetValue(ProgNameProperty, value); }
    /// <summary>面板窄于等于 360：别名段不显示（样稿 @container rvprog (max-width: 360px)）。</summary>
    public bool NoDev { get => GetValue(NoDevProperty); set => SetValue(NoDevProperty, value); }

    /// <summary>别名段（「1号机 ›」）此刻显示着（核对用）。</summary>
    public bool DevShown => _devShown;

    /// <summary>全文（「1号机 › RV1 › 加氢-标准」）。</summary>
    public string Text => (Alias ?? "") + " › " + (Rv ?? "") + " › " + (ProgName ?? "");

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AliasProperty) _alias.Text = Alias;
        else if (change.Property == RvProperty) _rv.Text = Rv;
        else if (change.Property == ProgNameProperty) _name.Text = ProgName;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var c in Children) c.Measure(Size.Infinity);
        double a = _alias.DesiredSize.Width, s1 = _sep1.DesiredSize.Width, r = _rv.DesiredSize.Width, s2 = _sep2.DesiredSize.Width, n = _name.DesiredSize.Width;
        double h = 0;
        foreach (var c in Children) h = Math.Max(h, c.DesiredSize.Height);
        double avail = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width;
        double em = _alias.FontSize > 0 ? _alias.FontSize : 16;

        // 先带别名段排；别名被挤到 < 2.5 个字宽（或面板太窄）就不要别名段再排一次
        _devShown = !NoDev && !string.IsNullOrEmpty(Alias);
        if (_devShown)
        {
            var (devW, nameW) = Shrink(a + s1, r + s2, n, avail);
            double aliasW = Math.Max(0, devW - s1);
            if (aliasW < a - 0.5 && aliasW < MinAliasEm * em) _devShown = false;
            else
            {
                _aliasW = aliasW;
                _sep1W = Math.Max(0, Math.Min(s1, devW - aliasW));
                _nameW = nameW;
            }
        }
        if (!_devShown)
        {
            var (_, nameW) = Shrink(0, r + s2, n, avail);
            _aliasW = 0;
            _sep1W = 0;
            _nameW = nameW;
        }
        double width = _aliasW + _sep1W + r + s2 + _nameW;
        if (_nameW < n - 0.01) _name.Measure(new Size(_nameW, double.PositiveInfinity));
        if (_devShown && _aliasW < a - 0.01) _alias.Measure(new Size(_aliasW, double.PositiveInfinity));
        return new Size(double.IsInfinity(avail) ? width : Math.Min(width, avail), h);
    }

    /// <summary>CSS flex-shrink：缺的宽度按 1000 × 别名段宽 : 1 × 程序名宽分摊，各自最少 0（先缩到 0 的冻结，余下的给另一个）。</summary>
    private static (double Dev, double Name) Shrink(double dev, double fixedW, double name, double avail)
    {
        double total = dev + fixedW + name;
        if (double.IsInfinity(avail) || total <= avail) return (dev, name);
        double deficit = total - avail;
        double wd = 1000 * dev, wn = name, sum = wd + wn;
        if (sum <= 0) return (dev, name);
        double cutD = deficit * wd / sum, cutN = deficit * wn / sum;
        if (cutD > dev) { cutN += cutD - dev; cutD = dev; }
        if (cutN > name) { cutD = Math.Min(dev, cutD + cutN - name); cutN = name; }
        return (dev - cutD, name - cutN);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double h = finalSize.Height;
        void Place(Avalonia.Controls.Control c, double x, double w)
        {
            double ch = c.DesiredSize.Height;
            c.Arrange(new Rect(x, (h - ch) / 2, Math.Max(0, w), ch));
        }
        double x = 0;
        // 别名段让出去时不改 IsVisible（改了会在排版中途再排一遍，来回振荡）：不透明度 0（不画、审计不看）、排成 0 宽
        _alias.Opacity = _devShown && _aliasW > 0.5 ? 1 : 0;
        _sep1.Opacity = _devShown && _sep1W > 0.5 ? 1 : 0;
        if (_devShown)
        {
            // 排成 0 宽藏过的文字再显示时要重新排（11.1 的缺陷，Controls/TextRelayout）
            if (_zeroed) { _zeroed = false; Controls.TextRelayout.Revive(_alias); Controls.TextRelayout.Revive(_sep1); }
            Place(_alias, x, _aliasW);
            x += _aliasW;
            Place(_sep1, x, _sep1W);
            x += _sep1W;
        }
        else
        {
            _alias.Arrange(default);
            _sep1.Arrange(default);
            _zeroed = true;
        }
        Place(_rv, x, _rv.DesiredSize.Width);
        x += _rv.DesiredSize.Width;
        Place(_sep2, x, _sep2.DesiredSize.Width);
        x += _sep2.DesiredSize.Width;
        Place(_name, x, Math.Min(_nameW, Math.Max(0, finalSize.Width - x)));
        return finalSize;
    }
}
