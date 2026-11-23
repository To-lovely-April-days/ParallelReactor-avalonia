#if DEBUG
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages.Programs;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Views.Pages.Programs;
using ParallelReactor.Models;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 程序步骤的编辑（用户 2026-11-19：新建程序不是空的、新加的步骤删不掉；要右键删除、左键按住上下拖动排序）：
/// 新建 = 只有「结束」；右键菜单 上移 / 下移 / 删除这一步（不能做时灰掉写原因）；按住拖动到插入线处；「结束」不能拖、也不能放到它后面。
/// </summary>
[Collection(M5ReviewHeadless.Collection)]
public class F9StepEditTests
{
    private static ShellVm Shell()
    {
        var p = DemoParams.Parse("fusion=1&freeze=1");
        var s = new ShellVm(FleetVm.CreateDemo(p));
        s.Toasts.Frozen = true;
        s.Layout.Update(new Size(1920, 1080));
        s.ApplyDemoParams(DemoParams.Parse("fusion=1&freeze=1&page=prog&dev=1&apop=0&pgm=one"));
        return s;
    }

    private static (ShellVm S, ProgramPageVm P) NewProgram()
    {
        var s = Shell();
        var pg = Assert.IsType<ProgramPageVm>(s.Page);
        pg.Tools.SelectMany(g => g.Tools).First(t => t.Label == "新建程序").Command.Execute(null);
        // 2026-11-23 模板库：「新建程序」先开窗口选模板或空白 —— 这里选空白
        var dlg = Assert.IsType<NewProgramDialogVm>(s.Dialogs.Current);
        dlg.Blank.PickCommand.Execute(null);
        dlg.CreateCommand.Execute(null);
        return (s, pg);
    }

    private static StepType[] Types(ProgramPageVm pg) => pg.Sel!.Steps.Select(x => x.Type).ToArray();

    private static void Add(ProgramPageVm pg, StepType t) => pg.One.AddTypes.First(a => a.Type == t).Command.Execute(null);

    [Fact]
    public void NewProgram_IsEmpty_OnlyEnd()
    {
        var (s, pg) = NewProgram();
        Assert.Equal(new[] { StepType.End }, Types(pg));
        Assert.Equal("未命名程序", pg.Sel!.Name);
        Assert.StartsWith("已新建空程序", s.Toasts.Items.Last().Text);
        // 添加的步骤插在「结束」前面，选中新加的那一步
        Add(pg, StepType.Leak);
        Add(pg, StepType.Stir);
        Assert.Equal(new[] { StepType.Leak, StepType.Stir, StepType.End }, Types(pg));
        Assert.Equal(1, pg.One.StepIndex);
    }

    [Fact]
    public void RowMenu_DeleteMove_AndReasons()
    {
        var (sh, pg) = NewProgram();
        Add(pg, StepType.Leak); Add(pg, StepType.Stir); Add(pg, StepType.Heat);
        var rows = pg.One.Steps;
        // 「结束」那一行：都灰掉、写原因；第一行不能上移；结束前一行不能下移
        Assert.False(rows[3].CanDelete); Assert.False(rows[3].CanUp); Assert.False(rows[3].CanDown); Assert.False(rows[3].CanDrag);
        Assert.False(rows[0].CanUp); Assert.True(rows[0].CanDown); Assert.True(rows[0].CanDelete); Assert.True(rows[0].CanDrag);
        Assert.False(rows[2].CanDown);
        Assert.Contains("结束", rows[2].DownReason);
        // 右键「删除这一步」：删的是那一行（不是当前选中的）
        pg.One.StepIndex = 0;
        rows[1].DeleteCommand.Execute(null);
        Assert.Equal(new[] { StepType.Leak, StepType.Heat, StepType.End }, Types(pg));
        rows[1].UpCommand.Execute(null);
        Assert.Equal(new[] { StepType.Heat, StepType.Leak, StepType.End }, Types(pg));
        Assert.Equal(0, pg.One.StepIndex);
        // 运行中的程序：锁住，都不能做
        pg.SelId = pg.Store.Programs.First(x => pg.UsedBy(x.Id).Count > 0).Id;
        pg.Refresh(sh.Fleet);
        Assert.All(pg.One.Steps, r => Assert.False(r.CanDelete || r.CanDrag));
    }

    [Fact]
    public void MoveTo_GapsAndEndStaysLast()
    {
        var (_, pg) = NewProgram();
        Add(pg, StepType.Leak); Add(pg, StepType.Stir); Add(pg, StepType.Heat);   // Leak Stir Heat End
        Assert.Equal(2, pg.One.MoveTo(0, 3));            // Leak 拖到 Heat 后面（第 3 个空隙）
        Assert.Equal(new[] { StepType.Stir, StepType.Heat, StepType.Leak, StepType.End }, Types(pg));
        Assert.Equal(2, pg.One.StepIndex);
        Assert.Equal(-1, pg.One.MoveTo(2, 4));            // 放到「结束」后面 = 放在它前面 = 没动
        Assert.Equal(0, pg.One.MoveTo(2, 0));             // 拖到最前
        Assert.Equal(new[] { StepType.Leak, StepType.Stir, StepType.Heat, StepType.End }, Types(pg));
        Assert.Equal(-1, pg.One.MoveTo(3, 0));            // 「结束」不能拖
        Assert.Equal(StepType.End, Types(pg)[^1]);
    }

    /// <summary>真实的鼠标：左键按住第一行往下拖到第三行下面 → 换位；拖动不算点击；右键先选中那一行并打开菜单。</summary>
    [Fact]
    public void Headless_DragAndRightClick() => M5ReviewHeadless.Run(() =>
    {
        var (_, pg) = NewProgram();
        Add(pg, StepType.Leak); Add(pg, StepType.Stir); Add(pg, StepType.Heat);
        var view = new OneView { DataContext = pg };
        var win = M5ReviewHeadless.ShellWindow(view, 1400, 900);
        win.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var list = view.GetVisualDescendants().OfType<ItemsControl>().First(x => x.Name == "StepList");
            Point Mid(int i)
            {
                var c = list.ContainerFromIndex(i)!;
                return c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), win)!.Value;
            }
            Point Below(int i)
            {
                var c = list.ContainerFromIndex(i)!;
                return c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height - 3), win)!.Value;
            }
            var from = Mid(0);
            win.MouseDown(from, MouseButton.Left, RawInputModifiers.None);
            win.MouseMove(from + new Point(0, 10), RawInputModifiers.LeftMouseButton);
            win.MouseMove(Below(2), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            var line = view.GetVisualDescendants().OfType<Border>().First(x => x.Name == "DropLine");
            Assert.True(line.IsVisible);
            win.MouseUp(Below(2), MouseButton.Left, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.False(line.IsVisible);
            Assert.Equal(new[] { StepType.Stir, StepType.Heat, StepType.Leak, StepType.End }, Types(pg));
            Assert.Equal(2, pg.One.StepIndex);

            // 不拖、只点：选中那一行，不改顺序
            var p1 = Mid(1);
            win.MouseDown(p1, MouseButton.Left, RawInputModifiers.None);
            win.MouseUp(p1, MouseButton.Left, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, pg.One.StepIndex);
            Assert.Equal(new[] { StepType.Stir, StepType.Heat, StepType.Leak, StepType.End }, Types(pg));

            // 右键第一行：先选中它，菜单打开
            var p0 = Mid(0);
            win.MouseDown(p0, MouseButton.Right, RawInputModifiers.None);
            win.MouseUp(p0, MouseButton.Right, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, pg.One.StepIndex);
            var btn = list.ContainerFromIndex(0)!.GetVisualDescendants().OfType<Button>().Prepend(list.ContainerFromIndex(0) as Button).First(b => b != null)!;
            Assert.True(btn.ContextMenu?.IsOpen);
            btn.ContextMenu!.Close();
        }
        finally { win.Close(); }
    });
}
#endif
