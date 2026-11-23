#if DEBUG
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Shell.Dialogs;
using ParallelReactor.Control.Views.Shell.Dialogs;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 曲线窗口：预览图、变化、合计跟着打的数走，不用等点别处（用户 2026-11-23：以前要点「添加一段」曲线才更新 —— 那一下只是让输入框失焦）。
/// 打到一半 / 超出上限时值先不动，失焦时照旧限幅。
/// </summary>
[Collection(M5ReviewHeadless.Collection)]
public class CurveLiveTypingTests
{
    [Fact]
    public void Headless_TypingUpdatesThePreview_BeforeFocusLeaves() => M5ReviewHeadless.Run(() =>
    {
        var p = DemoParams.Parse("fusion=1&freeze=1&apop=0&ch=5&cv=5");
        var s = new ShellVm(FleetVm.CreateDemo(p));
        s.Toasts.Frozen = true;
        s.Layout.Update(new Size(1920, 1080));
        s.ApplyDemoParams(DemoParams.Parse("page=main&dev=1&" + p));
        var vm = Assert.IsType<CurveDialogVm>(CurveDialogVm.Create(s, "1:5"));
        var view = new CurveDialogView { DataContext = vm };
        var win = M5ReviewHeadless.ShellWindow(view, 1000, 800);
        win.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new double?[] { 80, 80, 150, 150 }, vm.Rows.Select(r => r.Temp));
            var boxes = view.GetVisualDescendants().OfType<NumBox>().Where(b => b.IsEffectivelyVisible).ToList();
            var temp = boxes[0].GetVisualDescendants().OfType<TextBox>().First();
            var mins = boxes[1].GetVisualDescendants().OfType<TextBox>().First();

            // 目标温度：打 100（还在输入框里，没有失焦）→ 这一段、预览图、变化马上是 100
            temp.Focus();
            temp.Text = "1";
            Assert.Equal(1, vm.Rows[0].Temp);                 // 打到一半也是一个数：图先跟着
            temp.Text = "100";
            Dispatcher.UIThread.RunJobs();
            Assert.True(temp.IsFocused);
            Assert.Equal(100, vm.Rows[0].Temp);
            Assert.Equal(100, vm.Plot!.Rows[0].TempC);
            Assert.Equal("100", temp.Text);                   // 输入框里的字不被改写
            Assert.Contains("最高 150 °C", vm.SumText);

            // 超出上限：值先不动（图不乱跳），失焦时限幅
            temp.Text = "1000";
            Assert.Equal(100, vm.Rows[0].Temp);
            // 清空 = 没填：保存按钮写明原因
            temp.Text = "";
            Assert.Null(vm.Rows[0].Temp);
            temp.Text = "90";
            Assert.Equal(90, vm.Rows[0].Temp);

            // 用时：打 40 → 合计马上变
            string before = vm.SumText;
            mins.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("90", temp.Text);
            mins.Text = "40";
            Assert.Equal(40, vm.Rows[0].Minutes);
            Assert.Equal(40, vm.Plot!.Rows[0].Minutes);
            Assert.NotEqual(before, vm.SumText);
        }
        finally { win.Close(); }
    });
}
#endif
