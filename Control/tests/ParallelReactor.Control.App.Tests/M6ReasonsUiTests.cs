#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Views.Shell;
using Xunit;
using PrB = ParallelReactor.Control.Behaviors.Pr;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M6 第 3 组在真的控件上（无头 Avalonia，共用 M5ReviewHeadless 的会话）：
/// <list type="bullet">
/// <item>悬停提示的位置（样稿 showTip）：控件正下方居中、间距 6；窗口下边放不下翻到上面、间距照样 6；左边贴窗口时留 4 px；提示不接收指针。
///   自绘的轨道（TimelineTrack）照旧跟着指针。</item>
/// <item>带原因的输入框、下拉、复选框、数值框离开 Tab 顺序（样稿 readonly + tabindex=-1 / disabled），按钮照样能 Tab 到；原因清掉后回来。</item>
/// <item>数值框超出量程（样稿 clampNum）：失焦 / 回车 / −+ 时限幅并提示「超出量程 · 已限制为 …」（warn）；量程里的数、坏值不提示。</item>
/// </list>
/// </summary>
[Collection(M5ReviewHeadless.Collection)]
public sealed class M6ReasonsUiTests
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

    private static Rect RectIn(Visual v, Visual root) => new(v.TranslatePoint(default, root) ?? default, v.Bounds.Size);

    private static void Key(Window w, Key k, PhysicalKey pk)
    {
        w.KeyPress(k, RawInputModifiers.None, pk, null);
        w.KeyRelease(k, RawInputModifiers.None, pk, null);
        Dispatcher.UIThread.RunJobs();
    }

    [Fact]
    public void TipDefaults_BelowTheControl_TrackKeepsPointer() => M5ReviewHeadless.Run(() =>
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(PrB).TypeHandle);
        foreach (var c in new Avalonia.Controls.Control[] { new Button(), new Border(), new TextBlock(), new StackPanel(), new ComboBox(), new Controls.IconView() })
        {
            Assert.Equal(PlacementMode.Bottom, ToolTip.GetPlacement(c));
            Assert.Equal(0, ToolTip.GetVerticalOffset(c));
        }
        Assert.Equal(PlacementMode.Pointer, ToolTip.GetPlacement(new TimelineTrack()));   // 按指针下的那一段给提示
        var tip = new ToolTip();
        Assert.Equal(default, tip.Margin);                                                // 外边距只给放在控件下面的提示（打开时，TipPlacement）
        Assert.False(tip.IsHitTestVisible);
    });

    /// <summary>样稿 showTip 的翻转门槛：提示底（控件底 + 6 + 高）不超过范围底 − 4 就放下面。</summary>
    [Fact]
    public void TipFits_SameThresholdAsTheMock()
    {
        Assert.True(PrB.TipFits(500, 35, 545));                                          // 500 + 6 + 35 = 541 = 545 − 4
        Assert.False(PrB.TipFits(500, 35, 544.5));
        Assert.Equal(new Thickness(PrB.TipInset, 0), PrB.TipMargin);
    }

    [Fact]
    public void ReasonTip_CentredBelow_FlipsAtTheBottom_SlidesAtTheLeft() => M5ReviewHeadless.Run(() =>
    {
        Button Btn(string text, double x, double y)
        {
            var b = new Button { Content = text, Width = 120, Height = 36 };
            Canvas.SetLeft(b, x);
            Canvas.SetTop(b, y);
            PrB.SetReason(b, "2号机为本地只读 · 先申请控制权");
            return b;
        }
        var mid = Btn("全部运行", 400, 200);
        var low = Btn("底部", 400, 560);
        var left = Btn("左边", 0, 200);
        var plain = new Button { Content = "提示", Width = 80, Height = 36 };
        Canvas.SetLeft(plain, 700);
        Canvas.SetTop(plain, 100);
        ToolTip.SetTip(plain, "选择设备后急停 · 复位只能在触屏上操作");
        // 自绘的那类控件（直接继承 Control）：提示跟着指针，不加外边距
        var track = new Avalonia.Controls.Control { Width = 200, Height = 30 };
        Canvas.SetLeft(track, 300);
        Canvas.SetTop(track, 400);
        ToolTip.SetTip(track, "RV1 · 升温 120 ℃");
        var win = new Window { Width = 900, Height = 600, Content = new Canvas { Children = { mid, low, left, plain, track } } };
        win.Show();
        Settle(win);
        try
        {
            // 提示、和装着它的弹出层（桌面上就是那个单独的窗口）：间距在弹出层外面，弹出层上下就是提示的上下
            (Rect Btn, Rect Tip, Rect Host) Open(Button b)
            {
                ToolTip.SetIsOpen(b, true);
                Settle(win);
                var t = ToolTip.GetTip(b) as ToolTip ?? throw new InvalidOperationException("没有原因提示");
                var host = t.GetVisualAncestors().OfType<Avalonia.Controls.Primitives.OverlayPopupHost>().First();
                Assert.Equal(PrB.TipMargin, t.Margin);
                var r = (RectIn(b, win), RectIn(t, win), RectIn(host, win));
                ToolTip.SetIsOpen(b, false);
                Settle(win);
                return r;
            }
            var (b1, t1, h1) = Open(mid);
            Assert.InRange(t1.Center.X - b1.Center.X, -1, 1);
            Assert.InRange(t1.Y - (b1.Bottom + PrB.TipGap), -1, 1);
            Assert.InRange(h1.Y - t1.Y, -0.5, 0.5);                                    // 弹出层从提示顶开始：控件与提示之间的 6 px 不属于它
            Assert.InRange(h1.Height - t1.Height, -0.5, 0.5);
            var (b2, t2, h2) = Open(low);
            Assert.InRange(t2.Bottom - (b2.Y - PrB.TipGap), -1, 1);                 // 翻到上面：间距照样 6，不压住按钮
            Assert.InRange(h2.Bottom - t2.Bottom, -0.5, 0.5);
            var (b3, t3, _) = Open(left);
            Assert.True(b3.Center.X - t3.Width / 2 < 0, "提示居中会伸出窗口左边");
            Assert.InRange(t3.X - PrB.TipInset, -1, 1);                               // 滑回来、离窗口边 4 px
            // 跟着指针的提示：不加外边距（不往右挪 4 px）
            ToolTip.SetIsOpen(track, true);
            Settle(win);
            var trackTip = track.GetValue(Avalonia.Controls.Diagnostics.ToolTipDiagnostics.ToolTipProperty) as ToolTip;
            Assert.NotNull(trackTip);
            Assert.Equal(default, trackTip!.Margin);
            ToolTip.SetIsOpen(track, false);
            Settle(win);
            // 普通字符串提示也一样（自动生成的 ToolTip）
            ToolTip.SetIsOpen(plain, true);
            Settle(win);
            var auto = win.GetVisualDescendants().OfType<ToolTip>().FirstOrDefault(t => t.IsEffectivelyVisible);
            Assert.NotNull(auto);
            var bp = RectIn(plain, win);
            var tp = RectIn(auto!, win);
            Assert.InRange(tp.Center.X - bp.Center.X, -1, 1);
            Assert.InRange(tp.Y - (bp.Bottom + PrB.TipGap), -1, 1);
            // 提示不接收指针：点提示盖住的地方，命中的是下面的东西
            var hit = win.InputHitTest(tp.Center) as Visual;
            Assert.True(hit == null || !hit.GetSelfAndVisualAncestors().OfType<ToolTip>().Any(), "提示不该被命中");
            ToolTip.SetIsOpen(plain, false);
        }
        finally { win.Close(); }
    });

    [Fact]
    public void LockedInputs_LeaveTheTabOrder_ButtonsStay() => M5ReviewHeadless.Run(() =>
    {
        var before = new Button { Content = "前" };
        var text = new TextBox { Width = 120, Text = "PT-1" };
        var num = new NumBox { Value = 120, Minimum = 0, Maximum = 200, Decimals = 1, Unit = "℃", Width = 160 };
        var combo = new ComboBox { Width = 160, Items = { "加氢-标准", "吹扫" }, SelectedIndex = 0 };
        var check = new PrCheckBox { Content = "自动连接" };
        var locked = new Button { Content = "设定" };
        var after = new Button { Content = "后" };
        const string why = "2号机为本地只读 · 先申请控制权";
        foreach (var c in new Avalonia.Controls.Control[] { text, num, combo, check, locked }) PrB.SetReason(c, why);
        var win = new Window { Width = 900, Height = 300, Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { before, text, num, combo, check, locked, after } } };
        win.Show();
        Settle(win);
        try
        {
            var numInput = num.GetVisualDescendants().OfType<TextBox>().First();
            Assert.False(text.IsTabStop);
            Assert.False(numInput.IsTabStop);
            Assert.False(combo.IsTabStop);
            Assert.False(check.IsTabStop);
            Assert.True(locked.IsTabStop);                                          // 按钮照样在 Tab 顺序里（Enter / Space 说明原因）
            Assert.Equal(why, AutomationProperties.GetHelpText(combo));
            before.Focus(NavigationMethod.Tab);
            Settle(win);
            var seen = new List<object?>();
            for (int i = 0; i < 3; i++)
            {
                Key(win, Avalonia.Input.Key.Tab, PhysicalKey.Tab);
                seen.Add(win.FocusManager?.GetFocusedElement());
            }
            Assert.Same(locked, seen[0]);                                           // 跳过锁着的输入框、数值框、下拉、复选框
            Assert.Same(after, seen[1]);
            // 原因清掉：回到 Tab 顺序
            foreach (var c in new Avalonia.Controls.Control[] { text, num, combo, check }) PrB.SetReason(c, null);
            Settle(win);
            Assert.True(text.IsTabStop && numInput.IsTabStop && combo.IsTabStop && check.IsTabStop);
            before.Focus(NavigationMethod.Tab);
            Key(win, Avalonia.Input.Key.Tab, PhysicalKey.Tab);
            Assert.Same(text, win.FocusManager?.GetFocusedElement());
        }
        finally { win.Close(); }
    });

    [Fact]
    public void NumBox_OutOfRange_ClampsAndToasts() => M5ReviewHeadless.Run(() =>
    {
        var toasts = new ToastService { Frozen = true };
        var num = new NumBox { Value = 120, Minimum = 0, Maximum = 200, Step = 1, Decimals = 1, Unit = "℃", Width = 200 };
        var other = new Button { Content = "设定" };
        var win = new Window { Width = 600, Height = 200, Content = new StackPanel { Children = { num, other } } };
        win.Show();
        Settle(win);
        try
        {
            var input = num.GetVisualDescendants().OfType<TextBox>().First();
            (double? Value, string? Text, ToastItem? Toast) Type(string s, Action leave)
            {
                toasts.Clear();
                input.Focus();
                input.Text = s;
                leave();
                Settle(win);
                return (num.Value, input.Text, toasts.Items.LastOrDefault());
            }
            void Blur() => other.Focus();
            void Enter() => Key(win, Avalonia.Input.Key.Enter, PhysicalKey.Enter);
            var r1 = Type("999", Blur);
            Assert.Equal((200d, "200.0"), (r1.Value!.Value, r1.Text));
            Assert.Equal((ToastKind.Warn, "超出量程 · 已限制为 200.0"), (r1.Toast!.Kind, r1.Toast.Text));
            var r2 = Type("-3", Enter);                                              // 回车提交（本机加的）同样提示
            Assert.Equal((0d, "超出量程 · 已限制为 0.0"), (r2.Value!.Value, r2.Toast!.Text));
            Assert.Null(Type("150", Blur).Toast);                                    // 量程里：不提示
            var bad = Type("abc", Blur);
            Assert.Equal((150d, "150.0"), (bad.Value!.Value, bad.Text));
            Assert.Null(bad.Toast);                                                  // 坏值：不改值、不提示
            // −/+ 从输入框里的数步进（样稿 stepNum）：打了 999 还没提交就点 +，同样限幅并提示
            var plus = num.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Plus");
            toasts.Clear();
            input.Focus();
            input.Text = "999";
            plus.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Settle(win);
            Assert.Equal(200d, num.Value);
            Assert.Equal("超出量程 · 已限制为 200.0", toasts.Items.LastOrDefault()?.Text);
            // 本来就在量程里、步进越过上限：只限幅、不提示（样稿 clampNum 看的是步进前的数）
            Blur();
            Settle(win);
            num.Value = 199.5;
            Settle(win);
            Assert.Equal("199.5", input.Text);
            toasts.Clear();
            plus.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Settle(win);
            Assert.Equal(200d, num.Value);
            Assert.Empty(toasts.Items);
            // 带原因：不动，只说明原因（点到的是带原因的数值框里的按钮），不提示超出量程
            PrB.SetReason(num, "先申请控制权");
            Settle(win);
            input.Text = "999";
            plus.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Settle(win);
            Assert.Equal(200d, num.Value);
            Assert.DoesNotContain(toasts.Items, t => t.Text.StartsWith("超出量程", StringComparison.Ordinal));
        }
        finally
        {
            win.Close();
            toasts.Dispose();
        }
    });

    /// <summary>
    /// 提示框（ToastHost）：短句一行、框跟着字宽；本机才有的长句（合成的硬件未确认、硬件执行失败）框最宽 820、换行（最多 3 行），整句在框里
    /// （审查 layout2.fsx：以前横向 StackPanel 给文字无限宽，超过 766 px 的字画到框外）。
    /// </summary>
    [Fact]
    public void Toast_LongText_WrapsInsideTheBox() => M5ReviewHeadless.Run(() =>
    {
        var s = new Shell.ShellVm(Fleet.FleetVm.CreateDemo(Core.Demo.DemoParams.Parse("freeze=1")));
        s.Toasts.Frozen = true;
        var win = new Window { Width = 1920, Height = 1080, Content = new ToastHost { DataContext = s } };
        win.Show();
        try
        {
            string merged = "1号机、2号机、3号机 · 全部停止 · " + Fleet.DeviceActions.HwUnconfirmedText;
            string failed = "1号机 · 全部停止 · " + LiveDeviceSource.HwFailedText + "：RV1 停止加热未确认（温控通讯失败）· 请按「急停」或到设备现场确认；"
                            + "RV3 停止加热未确认（温控通讯失败）· 请按「急停」或到设备现场确认（HARDWARE_WRITE_FAILED）";
            s.Toast("1号机 · RV1 设定温度 120 ℃", ToastKind.Ok);
            s.Toast(merged, ToastKind.Warn);
            s.Toast(failed, ToastKind.Err);
            Settle(win);
            var rows = win.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Where(c => c.DataContext is ToastItem).ToList();
            Assert.Equal(3, rows.Count);
            foreach (var cp in rows)
            {
                var item = (ToastItem)cp.DataContext!;
                var box = cp.GetVisualDescendants().OfType<Border>().First();
                var tb = box.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == item.Text);
                var b = RectIn(box, win);
                var t = RectIn(tb, win);
                var lines = tb.TextLayout.TextLines;
                string where = $"「{item.Text[..8]}」框 {b.X:0.###} … {b.Right:0.###}，字 {t.X:0.###} … {t.Right:0.###}（{tb.DesiredSize.Width:0.###}），{lines.Count} 行";
                Assert.True(b.Width <= 820.01, where);
                Assert.True(t.X >= b.X + 16 && t.Right <= b.Right - 20 + 0.01 && t.Bottom <= b.Bottom - 11 + 0.01, where);
                Assert.DoesNotContain(lines, l => l.HasCollapsed);                         // 句末没被省略号截掉
                if (item.Kind == ToastKind.Ok) Assert.Single(lines);                       // 短句一行
                else Assert.InRange(lines.Count, 2, 3);
            }
        }
        finally { win.Close(); s.Dispose(); }
    });
}
#endif
