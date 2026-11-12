#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Theme;

namespace ParallelReactor.Control.Views.Demo;

/// <summary>
/// 「演示控制」工具窗口（M6，仅 Debug；样稿的演示条，shell.js renderDemo）：演示的交互走查（PR_CONTROL_DEMO=1、没有 PR_CONTROL_SHOT）时
/// 开在主窗口旁边，截图时没有（同样稿 bare=1）。用独立的窗口而不是样稿那条 40 px 的条：主窗口的外框不让出高度，1366 × 768 照样 1:1、不缩放
/// （FrameScaler 在矮于 768 时才缩放）。样稿的「画面 1920 / 1600 / 1366」不需要：直接改主窗口的大小。
/// 只在 Debug 里，所以用代码搭（不写 .axaml：发布版里不会有这个窗口）。按钮用普通的 ToggleButton（PrToggleButton：开 = a-soft 底 + a-line 边）。
/// </summary>
public sealed class DemoControlWindow : Window
{
    private readonly DemoControlVm _vm;
    private readonly WrapPanel _targets = new();
    private readonly List<(ToggleButton B, Func<bool> On)> _toggles = new();
    private ToggleButton? _freeze;
    private string _targetKey = "";

    public DemoControlWindow(DemoControlVm vm)
    {
        _vm = vm;
        Title = "演示控制";
        Width = 372;   // 控制权的四个按钮放得下一行（52 宽的标签 + 本地 / 申请中 / 远程 / 丢失）
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        Background = Palette.BgCardBrush;
        Foreground = Palette.T2Brush;
        FontSize = Res("FsBody", 15);
        if (Application.Current!.TryFindResource("UiFont", out var f) && f is FontFamily ff) FontFamily = ff;

        var body = new StackPanel { Margin = new Thickness(14, 12, 14, 14), Spacing = 10 };
        body.Children.Add(new TextBlock
        {
            Text = UiText.Keep(DemoControlVm.Caption), FontSize = Res("Fs2", 14), Foreground = Palette.T3Brush, TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(Row("设备", DemoControlVm.DevsChoices.Select(c =>
            Toggle(c.Label, () => _vm.Combo == c.Combo, () => _vm.SetDevs(c.Combo), c.Combo == "4" ? "加 4号机 HT-PR8（断线重连中）" : null))));
        body.Children.Add(Row("目标", _targets));
        body.Children.Add(Row("控制权", DemoControlVm.LeaseChoices.Select(c => Toggle(c.Label, () => _vm.LeaseCode == c.Code, () => _vm.SetLease(c.Code)))));
        body.Children.Add(Row("状态", new[]
        {
            Toggle("断线", () => _vm.Offline, _vm.ToggleOffline),
            Toggle("报警", () => _vm.Alarm, _vm.ToggleAlarm, "加一条超压报警 / 清除"),
            Toggle("急停", () => _vm.Estop, _vm.ToggleEstop, "模拟在设备上按下急停 / 在触屏复位"),
        }));
        _freeze = Toggle("冻结", () => _vm.Frozen, _vm.ToggleFreeze, "停止数值变化（截图用）");
        var reset = new Button { Content = "重置", Classes = { "sm" }, Margin = new Thickness(0, 0, 6, 6) };
        reset.Click += (_, _) => _vm.Reset();
        body.Children.Add(Row("演示", new Avalonia.Controls.Control[] { _freeze, reset }));
        Content = body;

        _vm.Changed += Sync;
        Closed += (_, _) => { _vm.Changed -= Sync; _vm.Dispose(); };
        Sync();
    }

    private static double Res(string key, double fallback) =>
        Application.Current!.TryFindResource(key, out var v) && v is double d ? d : fallback;

    /// <summary>一行：左边 52 宽的标签（样稿 .pr-demo-pop .pr-demo-k）+ 一排按钮（放不下换行）。</summary>
    private static Avalonia.Controls.Control Row(string label, IEnumerable<Avalonia.Controls.Control> items)
    {
        var wrap = new WrapPanel();
        foreach (var c in items) wrap.Children.Add(c);
        return Row(label, wrap);
    }

    private static Avalonia.Controls.Control Row(string label, Avalonia.Controls.Control content)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("52,*") };
        g.Children.Add(new TextBlock { Text = label, FontSize = Res("Fs2", 14), Foreground = Palette.T3Brush, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 0, 0) });
        Grid.SetColumn(content, 1);
        g.Children.Add(content);
        return g;
    }

    /// <summary>开关按钮：点了之后按演示数据的新状态重设勾选（不让 ToggleButton 自己翻）。</summary>
    private ToggleButton Toggle(string label, Func<bool> on, Action click, string? tip = null)
    {
        var b = new ToggleButton { Content = label, Classes = { "sm" }, Margin = new Thickness(0, 0, 6, 6) };
        if (tip != null) ToolTip.SetTip(b, tip);
        b.Click += (_, _) => { click(); b.IsChecked = on(); };
        _toggles.Add((b, on));
        return b;
    }

    /// <summary>按状态刷新：勾选、「冻结 / 已冻结」、目标设备那一排（设备组合变了就重建）。</summary>
    private void Sync()
    {
        var key = string.Join("|", _vm.Targets.Select(t => t.K + ":" + t.Alias));
        if (key != _targetKey)
        {
            _targetKey = key;
            _toggles.RemoveAll(t => _targets.Children.Contains(t.B));
            _targets.Children.Clear();
            foreach (var (k, alias) in _vm.Targets)
            {
                int kk = k;
                _targets.Children.Add(Toggle(alias, () => _vm.TargetK == kk, () => _vm.SelectTarget(kk)));
            }
        }
        foreach (var (b, on) in _toggles) b.IsChecked = on();
        if (_freeze != null) _freeze.Content = _vm.Frozen ? "已冻结" : "冻结";
    }

    /// <summary>
    /// 在主窗口旁边打开（右边放得下放右边，否则贴着屏幕右边、压在主窗口上），归主窗口所有：主窗口关了它跟着关。
    /// 不抢键盘（ShowActivated = false）：主窗口照样是活动窗口，键盘走查的 Tab、方向键、空格不会落到演示按钮上。
    /// 冻结时 UiTicker 不推进、主窗口带 frozen（没有动画和过渡）；toast 照常消失。
    /// </summary>
    public static DemoControlWindow OpenBeside(Window main, ShellVm shell, UiTicker ticker)
    {
        var vm = new DemoControlVm(shell, on =>
        {
            ticker.Frozen = on;
            Frozen.Apply(main, on);
        });
        var w = new DemoControlWindow(vm) { WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
        var screen = main.Screens.ScreenFromWindow(main) ?? main.Screens.Primary;
        w.Position = BesidePosition(main.Position, main.FrameSize?.Width ?? main.Bounds.Width, w.Width, main.DesktopScaling, screen?.WorkingArea);
        w.Show(main);
        return w;
    }

    /// <summary>与主窗口之间的空隙（DIP）。</summary>
    public const double Gap = 12;

    /// <summary>
    /// 工具窗口的位置：主窗口右边隔 <see cref="Gap"/>，放不下时贴着工作区右边；不高过工作区顶。宽度是 DIP，位置与工作区是桌面坐标，
    /// 用 <paramref name="desktopScaling"/>（WindowBase.DesktopScaling）换算 —— Windows 是物理像素（= 渲染缩放），macOS 是点（= 1）。
    /// 不能用 Screen.Scaling：macOS 视网膜屏上它是 2，算出来的位置翻倍、被夹回屏幕里时压在主窗口中间（复核）。
    /// </summary>
    public static PixelPoint BesidePosition(PixelPoint main, double mainWidth, double width, double desktopScaling, PixelRect? workingArea)
    {
        int x = main.X + (int)Math.Round((mainWidth + Gap) * desktopScaling), y = main.Y;
        if (workingArea is { } wa)
        {
            int w = (int)Math.Round(width * desktopScaling);
            if (x + w > wa.Right) x = Math.Max(wa.X, wa.Right - w);
            y = Math.Max(wa.Y, y);
        }
        return new PixelPoint(x, y);
    }
}
#endif
