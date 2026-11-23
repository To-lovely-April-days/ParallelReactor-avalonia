#if DEBUG
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Views.Pages.Alarms;
using ParallelReactor.Control.Views.Shell;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 报警列表的竖滚动条：按住拖动要能滚（用户 2026-11-23：拖不动，滚轮可以）。报警页用的是 12 px 的宽滚动条 —— 拇指画出来只有中间 6 px，
/// 以前两边各 3 px 是拇指的外边距，按在那里什么都不发生；现在整条宽度都是拇指（<paramref name="dx"/> = 离滚动条左边几 px）。
/// 数据在走（每拍刷新）时也要拖得动。
/// </summary>
[Collection(M5ReviewHeadless.Collection)]
public class AlarmScrollTests
{
    [Theory]
    [InlineData(false, 6)]    // 报警页：正中
    [InlineData(false, 1)]    // 报警页：贴着左边
    [InlineData(false, 11)]   // 报警页：贴着右边
    [InlineData(true, 4)]     // 标题栏弹出的报警列表（8 px 细条）
    public void Headless_DragThumb_ScrollsTheList(bool pop, double dx) => M5ReviewHeadless.Run(() =>
    {
        var fleet = FleetVm.CreateDemo(DemoParams.Parse("fusion=1&apop=0"));
        var s = new ShellVm(fleet);
        s.ApplyDemoParams(DemoParams.Parse("fusion=1&apop=0&page=alarm&dev=0"));
        Avalonia.Controls.Control root = pop ? new AlarmPopover { DataContext = s, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right } : new AlarmPage { DataContext = s.Page };
        var win = M5ReviewHeadless.ShellWindow(root, 1400, 420);
        win.Show();
        try
        {
            if (pop) s.Alarms.IsOpen = true;
            for (int t = 0; t < 4; t++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
            Dispatcher.UIThread.RunJobs();
            var scroll = root.GetVisualDescendants().OfType<ScrollViewer>().OrderByDescending(x => x.Extent.Height - x.Viewport.Height).First();
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height + 30, "列表要能滚");
            var bar = scroll.GetVisualDescendants().OfType<ScrollBar>().Single(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
            var thumb = bar.GetVisualDescendants().OfType<Thumb>().Single();
            double y = thumb.TranslatePoint(new Point(0, thumb.Bounds.Height / 2), bar)!.Value.Y;
            var from = bar.TranslatePoint(new Point(dx, y), win)!.Value;
            win.MouseDown(from, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            for (int i = 1; i <= 6; i++)
            {
                win.MouseMove(from + new Point(0, 5 * i), RawInputModifiers.LeftMouseButton);
                fleet.OnTick(true);
                Dispatcher.UIThread.RunJobs();
            }
            double mid = scroll.Offset.Y;
            win.MouseUp(from + new Point(0, 30), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.True(mid > 10 && scroll.Offset.Y > 10, "拖了 30 px，偏移 = " + mid + " → " + scroll.Offset.Y);
        }
        finally { win.Close(); }
    });
}
#endif
