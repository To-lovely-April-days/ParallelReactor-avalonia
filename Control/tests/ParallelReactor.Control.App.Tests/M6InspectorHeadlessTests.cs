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
        shell.ApplyDemoParams(p, Route.Overview);
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

    /// <summary>反应釜检查器搅拌说明条里的「设置」：先滚进视野（1366 下检查器矮，它在下面），同用户滚过去再点。</summary>
    private static Button SetLink(Window win, InspectorView insp)
    {
        var b = insp.GetVisualDescendants().OfType<Button>().First(x => x.Content as string == "设置" && x.IsEffectivelyVisible);
        b.BringIntoView();
        Settle(win);
        return b;
    }

    [Fact]
    public Task SetLink_ScrollsFocusesSelectsAndFlashes() => M5ReviewHeadless.RunAsync(() =>
    {
        var (win, shell) = Open(1366, 768);
        try
        {
            var insp = win.GetVisualDescendants().OfType<InspectorView>().First();
            var scroll = insp.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "Scroll");
            shell.Select(1, 1);
            Settle(win);
            Click(win, SetLink(win, insp));
            Settle(win);
            var box = insp.GetVisualDescendants().OfType<NumBox>().First(n => n.Name == "SharedRpm" && n.IsEffectivelyVisible);
            var tb = box.GetVisualDescendants().OfType<TextBox>().First();
            var sec = box.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("sec"));
            Assert.Equal((1, (int?)null), (shell.SelectedDevice!.K, shell.SelectedRv));
            Assert.Same(tb, win.FocusManager!.GetFocusedElement());
            Assert.Equal((0, tb.Text!.Length), (tb.SelectionStart, tb.SelectionEnd));
            Assert.Contains("flash", sec.Classes);
            // 1366：检查器矮，「共用搅拌」原来不全在视野里 → 分区顶部在正文顶部下面 8 px
            Assert.True(scroll.Offset.Y > 0);
            Assert.Equal(8, sec.TranslatePoint(default, scroll)!.Value.Y, 0.6);

            // 带原因（3号机本地只读）：不聚焦，分区照样提亮
            win.FocusManager.ClearFocus();
            shell.Select(3, 11);
            Settle(win);
            Click(win, SetLink(win, insp));
            Settle(win);
            var box3 = insp.GetVisualDescendants().OfType<NumBox>().First(n => n.Name == "SharedRpm" && n.IsEffectivelyVisible);
            var tb3 = box3.GetVisualDescendants().OfType<TextBox>().First();
            Assert.Equal(3, shell.SelectedDevice!.K);
            Assert.True(tb3.IsReadOnly);
            Assert.NotSame(tb3, win.FocusManager.GetFocusedElement());
            Assert.Contains("flash", box3.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("sec")).Classes);
        }
        finally { win.Close(); }
    });

    /// <summary>
    /// 不冻结时（交互窗口）提亮有 400 ms 的进出（样稿 transition: background-color 400ms ease, box-shadow 400ms ease）：
    /// 底色与外圈一起从透明 / 0 px 变到 a-tint / 8 px，熄灭时一起退回去。复核：以前外圈从「没有阴影」开始，Avalonia 不插值 ——
    /// 进来时 400 ms 末尾才整圈出现，熄灭时整圈留到最后；分区平时的外圈是看不见的 0 px 透明圈（InspectorView.NoRing）之后才有中间值。
    /// 无头会话的动画时钟按真的时间走、渲染一帧才送一次（ForceRenderTimerTick）：每 8 ms 渲染一帧，一帧一帧地看。
    /// </summary>
    [Fact]
    public Task Flash_FadesTheBackgroundAndTheRingInAndOutTogether() => M5ReviewHeadless.RunAsync(() =>
    {
        var (win, shell) = Open(1920, 1080);
        try
        {
            Frozen.Apply(win, false);
            var insp = win.GetVisualDescendants().OfType<InspectorView>().First();
            shell.Select(1, 1);
            Settle(win);
            var link = SetLink(win, insp);
            Click(win, link);
            Dispatcher.UIThread.RunJobs();            // 只跑排着的活（OnFocus → 提亮开始），不推进时钟
            var sec = insp.GetVisualDescendants().OfType<NumBox>().First(n => n.Name == "SharedRpm" && n.IsEffectivelyVisible)
                .GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("sec"));
            Assert.Contains("flash", sec.Classes);
            Assert.NotNull(sec.Transitions);
            // 别的分区平时：透明底、0 px 透明外圈（什么都不画）
            var other = insp.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("sec") && !ReferenceEquals(b, sec) && b.IsEffectivelyVisible);
            Assert.Equal((1, 0d, (byte)0), (other.BoxShadow.Count, other.BoxShadow[0].Spread, other.BoxShadow[0].Color.A));

            (byte Bg, double Spread, byte Ring) Now()
            {
                var bg = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(sec.Background).Color;
                Assert.Equal(1, sec.BoxShadow.Count);
                return (bg.A, sec.BoxShadow[0].Spread, sec.BoxShadow[0].Color.A);
            }
            // 一帧一帧推进，记下每一帧的（底色 α、外圈 px、外圈 α），到终值为止（最多 3 s）
            List<(byte Bg, double Spread, byte Ring)> Run((byte, double, byte) end)
            {
                var seen = new List<(byte Bg, double Spread, byte Ring)> { Now() };
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (seen[^1] != end && sw.ElapsedMilliseconds < 3000)
                {
                    Thread.Sleep(8);
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Dispatcher.UIThread.RunJobs();
                    seen.Add(Now());
                }
                return seen;
            }
            static bool Mid((byte Bg, double Spread, byte Ring) v) => v.Bg is > 0 and < 255 && v.Spread is > 0.5 and < 7.5 && v.Ring is > 0 and < 255;
            static string Show(List<(byte Bg, double Spread, byte Ring)> s) => string.Join(" ", s.Select(v => $"{v.Bg}/{v.Spread:0.#}/{v.Ring}"));

            var fadeIn = Run((255, 8, 255));
            Assert.True(fadeIn[0] == (0, 0, 0) && fadeIn[^1] == (255, 8, 255) && fadeIn.Count(Mid) >= 3, "进来：" + Show(fadeIn));
            Assert.Equal(Palette.ATintColor, sec.BoxShadow[0].Color);

            // 熄灭（1.2 s 的计时器做的事：去掉类 flash；过渡还在分区上）：一起退回去
            sec.Classes.Remove("flash");
            Dispatcher.UIThread.RunJobs();
            var fadeOut = Run((0, 0, 0));
            Assert.True(fadeOut[0] == (255, 8, 255) && fadeOut[^1] == (0, 0, 0) && fadeOut.Count(Mid) >= 3, "熄灭：" + Show(fadeOut));
        }
        finally { win.Close(); }
    });

    /// <summary>
    /// 控制权卡第一行（WrapEnd + FirstFit）：完整徽标在「控制权」后面放不下就换行（同样稿 flex-wrap）；一整行都放不下时换短的「其他客户端 X」，
    /// 不按省略号切掉持有者的名字；按钮在它那一行靠右。
    /// </summary>
    [Theory]
    [InlineData("远程控制中（其他客户端 PC-2）", 271, false, true)]     // 完整的放得下一整行：换到第二行，照样完整
    [InlineData("远程控制中（其他客户端 PC-工位2）", 271, true, false)]  // 一整行都放不下：短的，跟在「控制权」后面
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

    [Theory]
    [InlineData(1920, 1080, 2)]
    [InlineData(1366, 768, 3)]
    public Task OtherBanner_WrapsLikeTheMock(int w, int h, int rows) => M5ReviewHeadless.RunAsync(() =>
    {
        var (win, shell) = Open(w, h);
        try
        {
            shell.Navigate(Route.Reactor(1, 1));
            shell.Select(3, 11);
            Settle(win);
            var insp = win.GetVisualDescendants().OfType<InspectorView>().First();
            var wrap = insp.GetVisualDescendants().OfType<WrapEnd>().First(x => x.IsEffectivelyVisible && x.Children.OfType<SegText>().Any());
            var seg = wrap.Children.OfType<SegText>().Single();
            var link = wrap.Children.OfType<Button>().Single();
            Assert.Equal("检查器：3号机 · RV11（不是当前页签）", seg.Inlines!.Text);
            // 行数：按上下位置分组（一行里的元素纵向居中，上边不一样）
            int n = 0;
            double bottom = double.NegativeInfinity;
            foreach (var c in wrap.Children.Where(c => c.IsVisible).OrderBy(c => c.Bounds.Top))
            {
                if (c.Bounds.Top >= bottom - 0.5) n++;
                bottom = Math.Max(c.Bounds.Top >= bottom - 0.5 ? c.Bounds.Bottom : bottom, c.Bounds.Bottom);
            }
            Assert.Equal(rows, n);
            Assert.Equal(wrap.Bounds.Width, link.Bounds.Right, 0.6);
            if (rows == 3) Assert.True(seg.Bounds.Height > 30);   // 句子在里面折成两行
        }
        finally { win.Close(); }
    });
}
#endif
