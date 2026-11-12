#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
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
using ParallelReactor.Control.Views.Shell;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M6 页签栏（纯函数与视图模型）：
/// <list type="bullet">
/// <item>收起哪些页签只看页签宽度、当前页签和「›」不写个数时的宽度（<see cref="TabOverflowPanel.Layout"/> = 样稿 layoutTabs 在新打开的页面上的结果）——
///   以前用「›」现在的宽度排，页签总宽落在「›」写不写个数之差（20 px）里时两种状态都自洽，结果看上一张截图（1600 上 rv-done24 等五张「全部曲线」时有时无）。</item>
/// <item>断线设备的反应釜页签照样标报警红点、提示写反应釜自己的状态（样稿 tabInfo：ch.state / stateText），「›」菜单跟着。</item>
/// </list>
/// </summary>
public class M6TabsTests
{
    /// <summary>「›」写上个数多出来的宽（App：一位数 20、两位数 29；同 TabOverflowPanel 量到的）。</summary>
    private static double Extra(int k) => k <= 0 ? 0 : k < 10 ? 20 : 29;

    /// <summary>
    /// 样稿 shell.js layoutTabs 的逐步模拟（新打开的页面：「›」先不写个数）：每遍按「›」当时的宽度排，隐藏集合变了就更新「›」，
    /// 「›」的宽度没变就停，最多三遍。
    /// </summary>
    private static bool[] MockFresh(IReadOnlyList<double> w, double baseAvail, int active)
    {
        int shown = 0;
        bool[] show = TabOverflowPanel.Visible(w, baseAvail, active);
        for (int pass = 0; pass < 3; pass++)
        {
            show = TabOverflowPanel.Visible(w, baseAvail - Extra(shown), active);
            int k = show.Count(s => !s);
            double before = Extra(shown);
            shown = k;
            if (Extra(k) == before) break;
        }
        return show;
    }

    /// <summary>rv-done24-1600 的页签（总览、1号机、1号机 · RV1、1号机 · RV3、2号机 · RV1、3号机、全部曲线、2号机 · RV4（当前））与页签区（936 − 「+」44 − 「›」44）。</summary>
    private static readonly double[] Done24 = { 89, 71, 111, 113, 113, 89, 119, 129 };
    private const double Base1600 = 848;

    [Fact]
    public void RvDone24At1600_AllFit_LikeTheFreshMockPage()
    {
        // 834 ≤ 848：样稿新打开的页面全显示。以前「›」写着个数时页签区只有 828，「全部曲线」一直收着（两种状态都自洽）
        Assert.Equal(834, Done24.Sum());
        Assert.All(TabOverflowPanel.Layout(Done24, Base1600, 7, Extra), Assert.True);
        Assert.False(TabOverflowPanel.Visible(Done24, Base1600 - Extra(1), 7)[6]);
    }

    [Fact]
    public void LongerTab_HidesOnlyAllCurves_WithTheCountOnMore()
    {
        var w = Done24.ToArray();
        w[6] = 150;   // 865 > 848：收起「全部曲线」，「›」写上 1 再排（828）仍然只收这一个
        Assert.Equal(new[] { true, true, true, true, true, true, false, true }, TabOverflowPanel.Layout(w, Base1600, 7, Extra));
    }

    [Fact]
    public void SecondPass_UsesTheNarrowerStrip()
    {
        // 不写个数时只收一个（「C」放不下），写上个数窄了 20：「B」也放不下了 —— 样稿第二遍的结果
        var w = new double[] { 80, 100, 100, 100 };   // 总宽 380
        Assert.Equal(new[] { true, true, true, false }, TabOverflowPanel.Visible(w, 290, -1));
        Assert.Equal(new[] { true, true, false, false }, TabOverflowPanel.Layout(w, 290, -1, Extra));
    }

    [Fact]
    public void ThirdPass_WhenTheCountGetsAnotherDigit()
    {
        // 第一遍收 9 个（一位数），「›」变宽 20 之后收 10 个（两位数，再宽 9）：样稿再排第三遍（按两位数的宽度）
        var w = Enumerable.Repeat(30.0, 14).ToArray();   // 总览 + 13 个，各 30
        double avail = 30 * 5 + 5;                        // 不写个数：放 5 个，收 9 个
        var show = TabOverflowPanel.Layout(w, avail, -1, Extra);
        Assert.Equal(MockFresh(w, avail, -1), show);
        Assert.Equal(10, show.Count(s => !s));
    }

    [Fact]
    public void SameAsTheMockLoop_ForRandomBars()
    {
        var rnd = new Random(20261112);
        for (int i = 0; i < 4000; i++)
        {
            int n = rnd.Next(1, 16);
            var w = Enumerable.Range(0, n).Select(_ => (double)rnd.Next(60, 260)).ToArray();
            int active = rnd.Next(-1, n);
            double avail = rnd.Next(150, 1300);
            Assert.Equal(MockFresh(w, avail, active), TabOverflowPanel.Layout(w, avail, active, Extra));
        }
    }

    [Fact]
    public void Layout_NothingHidden_WhenItFitsOrNoWidthLimit()
    {
        Assert.All(TabOverflowPanel.Layout(Done24, double.PositiveInfinity, 7, Extra), Assert.True);
        Assert.Empty(TabOverflowPanel.Layout(Array.Empty<double>(), 100, -1, Extra));
        Assert.All(TabOverflowPanel.Layout(Done24, 834, 7, _ => throw new InvalidOperationException("放得下时不该问「›」多宽")), Assert.True);
    }

    // ———————————————————— 断线设备的反应釜页签：报警红点与提示 ————————————————————

    private static ShellVm Shell(string query)
    {
        var s = new ShellVm(FleetVm.CreateDemo(DemoParams.Parse(query)));
        s.Toasts.Frozen = true;
        return s;
    }

    [Fact]
    public void OfflineDevice_AlarmedReactorTab_KeepsTheDot_AndTipSaysItsOwnState()
    {
        var s = Shell("offline=3&freeze=1");
        s.Navigate(Route.Reactor(3, 11));
        s.RefreshChrome();
        var d = s.Fleet.Device(3)!;
        var tab = s.Tabs.Tabs.Single(t => t.Id == "reactor/3/11");
        Assert.True(tab.Offline);
        Assert.True(tab.ErrAlarm);                                            // 样稿：wifi-off + 红点
        Assert.StartsWith("数据停在 ", d.DataText);
        Assert.Equal("3号机 · RV11 · 超压报警 · " + d.DataText, tab.Tip);       // 样稿「3号机 · RV11 · 超压报警 · 数据停在 10:12:00」
        Assert.True(MenuModel.TabItems(s).Single(e => e.Label == "3号机 · RV11").Alarm);   // 「›」菜单同样标红点

        s.Navigate(Route.Reactor(3, 1));
        s.RefreshChrome();
        var r1 = s.Tabs.Tabs.Single(t => t.Id == "reactor/3/1");
        var own = d.Reactor(1)!.OwnWord;
        Assert.False(r1.ErrAlarm);
        Assert.NotEqual("离线", own);
        Assert.Equal("3号机 · RV1 · " + own + " · " + d.DataText, r1.Tip);    // 断线前的状态，不是「离线」
    }

    [Fact]
    public void OnlineDevice_ReactorTab_Unchanged()
    {
        var s = Shell("freeze=1");
        s.Navigate(Route.Reactor(3, 11));
        s.RefreshChrome();
        var tab = s.Tabs.Tabs.Single(t => t.Id == "reactor/3/11");
        Assert.False(tab.Offline);
        Assert.True(tab.ErrAlarm);
        Assert.Equal("3号机 · RV11 · 超压报警", tab.Tip);
        var rv1 = s.Tabs.Tabs.Single(t => t.Id == "reactor/1/1");
        Assert.False(rv1.ErrAlarm);
        Assert.Equal("1号机 · RV1 · 反应中", rv1.Tip);
    }
}

/// <summary>
/// M6 页签栏（无头 Avalonia，真的页签栏 TabBarView 放在一个窗口里，窗口宽 = 页签栏宽）：
/// <list type="bullet">
/// <item>同一组页签、同一个宽度，不管之前「›」写没写个数，收起的都一样（窗口窄 → 宽回来：以前「全部曲线」留在「›」里）。</item>
/// <item>收进「›」的页签不是 Tab 键的停靠点（样稿 display: none），再显示出来又是；拿着焦点的页签收进「›」时焦点移到「›」上。</item>
/// <item>Tab 键的顺序同样稿（.pr-tabs、.pr-tab-add、.pr-tab-more）：最后一个页签之后是「+」再「›」。</item>
/// <item>「›」上的个数是 pr:PrText（等宽数字）：1–9 一样宽（以前 TextBlock 加墨迹外伸，「3」「4」宽 1–2 px）。</item>
/// </list>
/// </summary>
[Collection(M5ReviewHeadless.Collection)]
public sealed class M6TabsUiTests
{
    private static void Settle(TopLevel w)
    {
        for (int i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            w.UpdateLayout();
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>默认页签再加两个反应釜页签（1号机 · RV2 当前），只放页签栏的窗口。</summary>
    private static (Window Win, ShellVm Shell, TabBarView Bar) Bar(double width)
    {
        var shell = new ShellVm(FleetVm.CreateDemo(DemoParams.Parse("freeze=1")));
        shell.Toasts.Frozen = true;
        shell.Navigate(Route.Reactor(3, 13));
        shell.Navigate(Route.Reactor(1, 2));
        var bar = new TabBarView { DataContext = shell };
        var win = new Window { Width = width, Height = 44, Content = bar };
        win.Show();
        Settle(win);
        return (win, shell, bar);
    }

    private static List<NavToggle> Tabs(TabBarView bar) => bar.GetVisualDescendants().OfType<NavToggle>().ToList();

    private static string Hidden(ShellVm s) => string.Join(",", s.Tabs.Tabs.Where(t => t.IsHidden).Select(t => t.Id));

    [Fact]
    public void SameTabsSameWidth_SameHiddenSet_WhateverTheCountWas() => M5ReviewHeadless.Run(() =>
    {
        var (win, shell, bar) = Bar(3000);
        try
        {
            var tabs = Tabs(bar);
            Assert.Equal(0, shell.Tabs.HiddenCount);
            double total = tabs.Sum(t => t.Bounds.Width);
            var add = bar.FindControl<Button>("Add")!;
            var more = bar.FindControl<Button>("More")!;
            double fixedW = add.Bounds.Width + more.Bounds.Width;   // 「+」44 +「›」44（不写个数）
            Assert.Equal(88, fixedW);

            // 页签区（「›」不写个数）比总宽多 5：新打开就全显示
            double fits = total + fixedW + 5;
            win.Width = fits;
            Settle(win);
            Assert.True(shell.Tabs.HiddenCount == 0, "刚好放得下：全显示（" + Hidden(shell) + "）");

            // 窄 40：收起一些，「›」写上个数变宽
            win.Width = fits - 40;
            Settle(win);
            Assert.True(shell.Tabs.HiddenCount > 0, "窄 40 px：有收起的");
            double extra = more.Bounds.Width - 44;
            Assert.True(extra > 5, $"「›」写着个数时比不写宽 {extra}（要比 5 大，下面才是两种状态都自洽的宽度）");

            // 宽回来：「›」写着个数时页签区只有 total + 5 − extra，以前留在这个状态；现在按不写个数的宽度排 → 全显示
            win.Width = fits;
            Settle(win);
            Assert.True(shell.Tabs.HiddenCount == 0, $"宽回来：全显示，同新打开（还收着 {Hidden(shell)}，「›」{more.Bounds.Width}）");
            Assert.Equal(44, more.Bounds.Width);
            Assert.All(Tabs(bar), t => Assert.True(t.Bounds.Width > 0));
        }
        finally { win.Close(); }
    });

    [Fact]
    public void HiddenTabs_AreNotTabStops_UntilShownAgain() => M5ReviewHeadless.Run(() =>
    {
        var (win, shell, bar) = Bar(3000);
        try
        {
            double total = Tabs(bar).Sum(t => t.Bounds.Width);
            win.Width = total + 88 - 300;
            Settle(win);
            var hidden = Tabs(bar).Where(t => (t.DataContext as DocTabVm)?.IsHidden == true).ToList();
            var shown = Tabs(bar).Where(t => (t.DataContext as DocTabVm)?.IsHidden == false).ToList();
            Assert.NotEmpty(hidden);
            Assert.All(hidden, t => Assert.False(t.IsTabStop, (t.DataContext as DocTabVm)?.Label + " 收在「›」里还是 Tab 停靠点"));
            Assert.All(hidden.SelectMany(t => t.GetVisualDescendants().OfType<InputElement>()).Where(e => e.Focusable), e => Assert.False(e.IsTabStop));
            Assert.All(shown, t => Assert.True(t.IsTabStop));

            // Tab 键走一圈：只经过显示着的页签（和它们里面的按钮）、「+」「›」
            shown[0].Focus(NavigationMethod.Tab);
            var seen = new List<IInputElement>();
            for (int i = 0; i < 40; i++)
            {
                win.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
                win.KeyRelease(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
                Dispatcher.UIThread.RunJobs();
                if (win.FocusManager?.GetFocusedElement() is { } f) seen.Add(f);
            }
            Assert.DoesNotContain(seen, f => f is Visual v && hidden.Any(h => ReferenceEquals(h, v) || h.IsVisualAncestorOf(v)));
            Assert.Contains(bar.FindControl<Button>("Add")!, seen);
            Assert.Contains(bar.FindControl<Button>("More")!, seen);

            // 宽回来：又是停靠点
            win.Width = total + 200;
            Settle(win);
            Assert.Equal(0, shell.Tabs.HiddenCount);
            Assert.All(Tabs(bar), t => Assert.True(t.IsTabStop));
        }
        finally { win.Close(); }
    });

    private static void PressTab(Window w, RawInputModifiers mods = RawInputModifiers.None)
    {
        w.KeyPress(Key.Tab, mods, PhysicalKey.Tab, null);
        w.KeyRelease(Key.Tab, mods, PhysicalKey.Tab, null);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>从最后一个页签按 Tab（跳过它里面的 ×）：「+」再「›」；Shift+Tab 倒回来。以前 DockPanel 先放「›」「+」，最后一个页签之后直接出了页签栏。</summary>
    [Fact]
    public void TabOrder_LastTab_ThenAdd_ThenMore() => M5ReviewHeadless.Run(() =>
    {
        var (win, shell, bar) = Bar(3000);
        try
        {
            Assert.Equal(0, shell.Tabs.HiddenCount);
            var add = bar.FindControl<Button>("Add")!;
            var more = bar.FindControl<Button>("More")!;
            Assert.Same(more, bar.FindControl<DocTabStrip>("Strip")!.MoreButton);
            var last = Tabs(bar)[^1];
            bool InLast(IInputElement? f) => f is Visual v && (ReferenceEquals(v, last) || last.IsVisualAncestorOf(v));
            last.Focus(NavigationMethod.Tab);
            var after = new List<IInputElement?>();
            for (int i = 0; i < 6 && after.Count < 2; i++)
            {
                PressTab(win);
                var f = win.FocusManager?.GetFocusedElement();
                if (!InLast(f)) after.Add(f);
            }
            Assert.Equal(new IInputElement?[] { add, more }, after);
            PressTab(win, RawInputModifiers.Shift);
            Assert.Same(add, win.FocusManager?.GetFocusedElement());
            PressTab(win, RawInputModifiers.Shift);
            Assert.True(InLast(win.FocusManager?.GetFocusedElement()), "Shift+Tab 从「+」回到最后一个页签");
        }
        finally { win.Close(); }
    });

    /// <summary>
    /// 拿着焦点的页签收进「›」（窗口变窄）：焦点移到「›」上，不留在排成 0 × 0 的页签上（看不见，回车还会打开它；样稿 display: none 让焦点丢掉）。
    /// 键盘来的焦点在「›」上画焦点框，指针点出来的不画；没收起的页签拿着焦点时不动。
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FocusedTab_CollapsedIntoMore_FocusGoesToMore(bool keyboard) => M5ReviewHeadless.Run(() =>
    {
        var (win, shell, bar) = Bar(3000);
        try
        {
            var tabs = Tabs(bar);
            double total = tabs.Sum(t => t.Bounds.Width);
            var more = bar.FindControl<Button>("More")!;
            var target = tabs[^2];   // 当前页签（最后一个）左边那个：变窄时先收它
            target.Focus(keyboard ? NavigationMethod.Tab : NavigationMethod.Pointer);
            Settle(win);
            Assert.Same(target, win.FocusManager?.GetFocusedElement());
            win.Width = total + 88 - 300;
            Settle(win);
            Assert.True((target.DataContext as DocTabVm)?.IsHidden == true, "窄 300 px：当前页签左边那个收进「›」");
            Assert.Same(more, win.FocusManager?.GetFocusedElement());
            Assert.False(target.IsFocused);
            Assert.Equal(keyboard, more.Classes.Contains(":focus-visible"));
            Assert.Equal("reactor/1/2", shell.Tabs.Active?.Id);

            // 拿着焦点的是显示着的页签（总览）：收起别的页签时焦点不动
            win.Width = 3000;
            Settle(win);
            tabs[0].Focus(NavigationMethod.Tab);
            win.Width = total + 88 - 300;
            Settle(win);
            Assert.True(shell.Tabs.HiddenCount > 0);
            Assert.Same(tabs[0], win.FocusManager?.GetFocusedElement());
        }
        finally { win.Close(); }
    });

    [Fact]
    public void MoreCount_EveryDigitSameWidth() => M5ReviewHeadless.Run(() =>
    {
        var theme = (Avalonia.Styling.ControlTheme)Application.Current!.FindResource("PrTabBarButton")!;
        Button Make(string? count)
        {
            var b = new Button { Theme = theme, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
            b.Classes.Add("more");
            if (count != null) b.Classes.Add("has-more");
            Behaviors.Pr.SetIcon(b, "right");
            Behaviors.Pr.SetCount(b, count);
            return b;
        }
        var buttons = new[] { (string?)null, "1", "2", "3", "4", "5", "6", "7", "8", "9", "12" }.Select(Make).ToList();
        var panel = new StackPanel();
        foreach (var b in buttons) panel.Children.Add(b);
        var win = new Window { Width = 400, Height = 600, Content = panel };
        win.Show();
        try
        {
            Settle(win);
            var w = buttons.Select(b => b.Bounds.Width).ToList();
            Assert.Equal(44, w[0]);
            Assert.True(w.Skip(1).Take(9).Distinct().Count() == 1, "「›」写 1–9 的宽度：" + string.Join(" ", w.Skip(1).Take(9)));
            Assert.True(w[10] > w[1], $"两位数更宽（{w[10]} > {w[1]}）");
            Assert.Equal(TabOverflowPanel.DefaultExtra, w[1] - 44);       // 估计值 = 量到的（Inter 13 px 600 等宽数字）
        }
        finally { win.Close(); }
    });
}
#endif
