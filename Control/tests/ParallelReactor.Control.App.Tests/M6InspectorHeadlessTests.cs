#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Controls.Shell;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Theme;
using ParallelReactor.Control.Views;
using ParallelReactor.Control.Views.Shell;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M6 检查器（无头窗口，演示数据、冻结）：
/// <list type="bullet">
/// <item>「设置」→ 检查器（样稿 inspFocus）：分区不在视野里时滚到顶部下面 8 px、数值框聚焦并全选、分区提亮（类 flash）；数值框带原因时不聚焦；
///   不冻结时提亮的底色与外圈一起进出（步进时钟）。</item>
/// <item>「不是当前页签」提示条：一整句，300 宽时图标单独一行、句子折两行、「回到 …」再一行（同样稿 267×110 的三行）。</item>
/// </list>
/// </summary>
[Collection(M5ReviewHeadless.Collection)]
public sealed class M6InspectorHeadlessTests
{
    private static void Settle(TopLevel? w = null)
    {
        for (int i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            w?.UpdateLayout();
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static (MainWindow Win, ShellVm Shell) Open(double w, double h)
    {
        var p = DemoParams.Parse("freeze=1");
        var fleet = FleetVm.CreateDemo(p, a => Dispatcher.UIThread.Post(a));
        var shell = new ShellVm(fleet);
        shell.Toasts.Frozen = true;
        var win = new MainWindow { DataContext = shell, Width = w, Height = h };
        Frozen.Apply(win);
        win.Show();
        Settle(win);
        shell.ApplyDemoParams(p);
        shell.Layout.Open(DockPaneId.Inspector);   // 融合版：检查器默认收起（主页右侧竖条），展开再量
        Settle(win);
        return (win, shell);
    }

    private static void Click(Window win, Visual v)
    {
        var m = v.TransformToVisual(win) ?? throw new InvalidOperationException("不在窗口里");
        var pt = new Point(v.Bounds.Width / 2, v.Bounds.Height / 2).Transform(m);
        win.MouseMove(pt, RawInputModifiers.None);
        win.MouseDown(pt, MouseButton.Left, RawInputModifiers.None);
        win.MouseUp(pt, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    // 融合版 F3：主页检查器换成 ChannelCtlView（Views/Shell），外框里不再放 pc-v3 的 InspectorView；
    // 原来的「搅拌说明条 › 设置 → 滚到共用转速并闪一下」两个测试随之删除（新检查器的搅拌在共用资源面板调）。

    [Theory]
    [InlineData("远程控制中（其他客户端 PC-2）", 271, false, true)]     // 完整的放得下一整行：换到第二行，照样完整
    [InlineData("远程控制中（其他客户端 PC-工位2）", 251, true, false)]  // 一整行都放不下：短的，跟在「控制权」后面（融合版 F1 徽标 13 px，完整的 255 宽；pc-v3 时用 271）
    [InlineData("远程控制中（其他客户端 PC-工位2）", 241, true, false)]
    public Task LeaseRow_WrapsFirst_ThenFallsBackToTheShortBadge(string full, double width, bool shortShown, bool wrapped) => M5ReviewHeadless.RunAsync(() =>
    {
        var key = new TextBlock { Text = "控制权", FontSize = 14, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        var longB = new LeaseBadge { State = LeaseState.Other, Text = full };
        var shortB = new LeaseBadge { State = LeaseState.Other, Text = "其他客户端 " + full[(full.IndexOf("端 ", StringComparison.Ordinal) + 2)..^1] };
        var fit = new FirstFit { Children = { longB, shortB }, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        var row = new WrapEnd { RowGap = 8, Children = { key, fit }, Width = width };
        // 同截图的普通窗口（ShotRunner.PlainWindow：t-2 字、bg-card 底）：审计也查色板
        var win = new Window { Width = 400, Height = 200, FontSize = 15, Foreground = Palette.T2Brush, Background = Palette.BgCardBrush, Content = new StackPanel { Children = { row } } };
        if (Application.Current!.TryFindResource("UiFont", out var ff) && ff is Avalonia.Media.FontFamily family) win.FontFamily = family;
        win.Show();
        try
        {
            Settle(win);
            var shown = shortShown ? shortB : longB;
            var hidden = shortShown ? longB : shortB;
            Assert.True(shown.Bounds.Width > 0 && hidden.Bounds.Width == 0, $"显示的 {shown.Bounds} · 藏起来的 {hidden.Bounds}");
            // 藏起来的那个透明、不接受指针（排成 0 × 0 的徽标里横排的字照旧按自然宽度排：不透明的话审计报 outside、还挡住指针）
            Assert.Equal((1d, true, 0d, false), (shown.Opacity, shown.IsHitTestVisible, hidden.Opacity, hidden.IsHitTestVisible));
            Assert.Empty(ParallelReactor.Control.Shot.ShotAudit.Run(win));
            Assert.False(shown.GetVisualDescendants().OfType<TextBlock>().Any(t => t.TextLayout.TextLines.Any(l => l.HasCollapsed)), "徽标的字没被省略");
            Assert.True(fit.Bounds.Right <= width + 0.5, $"徽标在这一行里（{fit.Bounds}）");
            Assert.Equal(wrapped, fit.Bounds.Top >= key.Bounds.Bottom);
        }
        finally { win.Close(); }
    });

}
#endif
