#if DEBUG
using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Views.Pages.Reactor;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M5 复核 v4-program-2（无头 Avalonia）：程序库的一行，「下发」左边整行都是选择按钮（样稿 .reactor-lib-pick flex: 1 1 auto; align-self: stretch，
/// rv-noprog313-1920 在 WebKit 里量的 509.95 × 59）—— 以前只有文字那么宽（268 / 262 / 171 px），行的右半边悬停有底色却点不动、也不是手形。
/// 面板 564 宽（同 1920 的程序栏）：本地只读时按钮 510 × 59 铺满整行；远程控制中（每行「下发」）按钮到「下发」左边 10 px（样稿 422.17）。
/// </summary>
[Collection(M5ReviewHeadless.Collection)]
public class M5ReviewLibraryPickTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WholeRowLeftOfLoad_PicksTheProgram(bool remote) => M5ReviewHeadless.Run(() =>
    {
        var (_, page) = M5ReviewStepFlowTests.Open(3, 13, "freeze=1&sel=3/13" + (remote ? "&lease=3:remote" : ""));
        var vm = page.Program;
        Assert.Equal(3, vm.Library.Count);
        var panel = new ReactorProgramPanel { DataContext = vm };
        var win = M5ReviewHeadless.ShellWindow(panel, 564, 752);
        win.Show();
        try
        {
            M5ReviewStepFlowUiTests.Settle(win);
            var hover = Assert.IsAssignableFrom<ISolidColorBrush>(Application.Current!.FindResource("BgHoverBrush")).Color;
            foreach (var row in vm.Library.Reverse())   // 从最后一行点起（第一行默认选中）
            {
                M5ReviewStepFlowUiTests.Settle(win);
                var pick = M5ReviewHeadless.PickButton(panel, row);
                var rowBorder = pick.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("librow"));
                var inner = rowBorder.Bounds.Width - rowBorder.Padding.Right;   // 行宽 − 右内边距 12
                Assert.Equal(59, pick.Bounds.Height, 0.01);
                if (remote)
                {
                    var load = M5ReviewHeadless.LoadButton(panel, row);
                    Assert.True(load.IsVisible);
                    Assert.Equal(inner - 10 - load.Bounds.Width, pick.Bounds.Width, 0.01);
                    Assert.Equal(pick.Bounds.Right + 10, load.Bounds.Left, 0.01);
                    Assert.Equal(422.17, pick.Bounds.Width, 1.0);
                }
                else
                {
                    Assert.Equal(inner, pick.Bounds.Width, 0.01);
                    Assert.Equal(509.95, pick.Bounds.Width, 0.1);
                }

                // 行的右端（文字右边很远）：命中的是选择按钮，手形；悬停时整行底色
                var pt = M5ReviewHeadless.RightEnd(win, pick);
                var hit = Assert.IsAssignableFrom<InputElement>(win.InputHitTest(pt));
                Assert.Same(pick, hit.GetSelfAndVisualAncestors().OfType<Button>().First());
                Assert.Equal("Hand", pick.Cursor?.ToString());
                Assert.Same(pick.Cursor, hit.Cursor);   // Cursor 往下继承：按钮里哪儿都是手形
                win.MouseMove(pt, RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                Assert.True(rowBorder.IsPointerOver);
                if (!row.IsSelected) Assert.Equal(hover, Assert.IsAssignableFrom<ISolidColorBrush>(rowBorder.Background).Color);

                // 点那里：选中这一行、预览它
                M5ReviewHeadless.ClickRight(win, pick);
                Assert.Equal(row.Id, vm.LibSel);
                Assert.True(row.IsSelected);
                Assert.Equal(ReactorPageText.PreviewTitle(row.Name), vm.PreviewTitle);
            }
        }
        finally
        {
            win.Close();
        }
    });
}
#endif
