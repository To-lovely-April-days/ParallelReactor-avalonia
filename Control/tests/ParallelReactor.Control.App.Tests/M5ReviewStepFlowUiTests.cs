#if DEBUG
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Views.Pages.Reactor;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M5 复核 c2-ui-1（控件这一半，无头 Avalonia）：步骤卡片画布（StepFlowPanel）按视图模型认卡片 —— 只给新的视图模型建卡片 / 条件框，拆掉的清掉 DataContext；
/// 条件框（StepCondView）只在可视树里时订阅视图模型。以前每次集合变化整批重建：逐张 Add 30 步要建 465 对，435 个拆掉的条件框还挂在视图模型上（复核的探针）。
/// 这里 30 步：任何时候每个视图模型正好一个条件框订阅着它（就是画布上那一个），换程序、点程序库预览之后旧的视图模型一个订阅也不剩。
/// </summary>
[Collection(M5ReviewHeadless.Collection)]
public class M5ReviewStepFlowUiTests
{
    private static readonly FieldInfo PropertyChangedField =
        typeof(ObservableObject).GetField("PropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ObservableObject 没有 PropertyChanged 字段（CommunityToolkit.Mvvm 换了实现？）");

    /// <summary>订阅着这个视图模型的条件框。</summary>
    internal static List<StepCondView> CondHandlers(StepCardVm vm) =>
        (PropertyChangedField.GetValue(vm) as Delegate)?.GetInvocationList().Select(d => d.Target).OfType<StepCondView>().ToList() ?? new List<StepCondView>();

    /// <summary>
    /// 画布上的卡片正好是 <paramref name="items"/>（同序：Children 里第 i 步在 1 + 2i / 2 + 2i），每个视图模型正好一个条件框订阅着（画布上那个）；
    /// <paramref name="seen"/> 里不在画布上的视图模型一个订阅也没有。
    /// </summary>
    internal static void AssertLive(StepFlowPanel flow, IReadOnlyList<StepCardVm> items, IEnumerable<StepCardVm> seen)
    {
        Assert.Equal(items.Count, flow.Pairs.Count);
        for (int i = 0; i < items.Count; i++)
        {
            var (card, cond) = flow.Pairs[i];
            Assert.Same(items[i], card.DataContext);
            Assert.Same(items[i], cond.DataContext);
            Assert.Same(card, flow.Children[1 + 2 * i]);
            Assert.Same(cond, flow.Children[2 + 2 * i]);
            Assert.NotNull(cond.GetVisualRoot());
            Assert.Same(cond, Assert.Single(CondHandlers(items[i])));
        }
        var live = new HashSet<StepCardVm>(items, ReferenceEqualityComparer.Instance);
        foreach (var vm in seen)
            if (!live.Contains(vm)) Assert.Empty(CondHandlers(vm));
    }

    /// <summary>排版、绑定、排着的滚动都做完，再画一帧。</summary>
    internal static void Settle(Window win)
    {
        Dispatcher.UIThread.RunJobs();
        win.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();   // 画一帧：命中测试按画出来的场景
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>30 步的程序（筛选-80 ℃ 的前三步反复，最后一步结束）：卡片与条件框都有内容。</summary>
    internal static ProgramDef LongProgram(DemoDevice d)
    {
        var src = d.Programs.First(x => x.Id == "screen-80").Steps;
        var steps = Enumerable.Range(0, 30).Select(i => i == 29 ? src[3] : src[i % 3]).ToList();
        return new ProgramDef("long-30", "长程序-30 步", "30 步（测试）", steps);
    }

    [Fact]
    public void Flow_ThirtySteps_OneSubscriptionEach_ReusedOnEdits_LetGoWhenRemovedOrDetached() => M5ReviewHeadless.Run(() =>
    {
        var fleet = new DemoFleet(DemoParams.Default with { Freeze = true });
        var d3 = fleet.Device(3)!;
        var def = LongProgram(d3);
        var rec = new ProgramRecord { Id = def.Id, Name = def.Name, Steps = def.Steps, Status = ProgramStatus.Loaded };
        StepCardVm Card(int i)
        {
            var c = new StepCardVm(i);
            c.Apply(rec, d3.Model, 13, d3.Alias, null, online: true, preview: false, plain: false);
            return c;
        }
        var seen = new List<StepCardVm>();
        StepCardVm Seen(StepCardVm c) { seen.Add(c); return c; }

        var items = new ObservableCollection<StepCardVm>();
        var flow = new StepFlowPanel { Items = items };
        var scroll = new ScrollViewer { Content = flow };
        var win = new Window { Width = 600, Height = 900, Content = scroll };
        win.Show();
        try
        {
            // 以前视图模型的写法：Clear + 逐张 Add —— 每加一张只建这一张
            foreach (var c in Enumerable.Range(0, 30).Select(Card)) items.Add(Seen(c));
            Settle(win);
            AssertLive(flow, items, seen);
            var byVm = new Dictionary<StepCardVm, (StepCardView Card, StepCondView Cond)>(ReferenceEqualityComparer.Instance);
            foreach (var p in flow.Pairs) byVm.Add((StepCardVm)p.Card.DataContext!, p);

            // 挪、删、插、换：留下来的视图模型还是原来的卡片和条件框（只挪位置），删掉 / 换掉的放开视图模型
            items.Move(29, 0);
            var removed = items[10];
            var removedPair = byVm[removed];
            items.RemoveAt(10);
            items.Insert(5, Seen(Card(5)));
            var replaced = items[3];
            var replacedPair = byVm[replaced];
            items[3] = Seen(Card(3));
            Settle(win);
            AssertLive(flow, items, seen);
            for (int i = 0; i < items.Count; i++)
                if (byVm.TryGetValue(items[i], out var p)) Assert.Equal(p, flow.Pairs[i]);
            foreach (var (card, cond) in new[] { removedPair, replacedPair })
            {
                Assert.Null(card.DataContext);
                Assert.Null(cond.DataContext);
                Assert.Null(card.GetVisualParent());
                Assert.Null(cond.GetVisualParent());
            }
            Assert.Equal(1 + 2 * 30, flow.Children.Count);

            // 换程序（同以前的写法：Clear + 逐张 Add）：旧的 30 个视图模型一个订阅也不剩
            var old = items.ToList();
            items.Clear();
            foreach (var c in Enumerable.Range(0, 30).Select(Card)) items.Add(Seen(c));
            Settle(win);
            AssertLive(flow, items, seen);
            Assert.All(old, vm => Assert.Empty(CondHandlers(vm)));
            Assert.Equal(30, seen.Sum(vm => CondHandlers(vm).Count));

            // 整个画布摘下：条件框全部退订；摘下期间改了的条件，挂回去时照新的显示
            win.Content = null;
            Settle(win);
            Assert.All(items, vm => Assert.Empty(CondHandlers(vm)));
            items[0].CondLabel = "";
            items[0].CondValue = "压力 ≥ 9.999 MPa";
            win.Content = scroll;
            Settle(win);
            AssertLive(flow, items, seen);
            var value = flow.Pairs[0].Cond.FindControl<TextBlock>("Value");
            Assert.Equal("压力 ≥ 9.999 MPa", value?.Text);
        }
        finally
        {
            win.Close();
        }
    });

    [Fact]
    public void ProgramPanel_ThirtySteps_NoOrphans_AfterPreviewClickAndProgramChange() => M5ReviewHeadless.Run(() =>
    {
        var (shell, page) = M5ReviewStepFlowTests.Open(3, 13, "freeze=1&sel=3/13&lease=3:remote");
        var demo = shell.Fleet.DemoData!;
        var d3 = demo.Device(3)!;
        M5ReviewHeadless.AddToLibrary(d3, LongProgram(d3));
        shell.Fleet.RefreshNow();
        var vm = page.Program;
        Assert.Equal("long-30", vm.Library[^1].Id);

        var panel = new ReactorProgramPanel { DataContext = vm };
        var win = M5ReviewHeadless.ShellWindow(panel, 564, 752);
        win.Show();
        var seen = new List<StepCardVm>();
        void Check(StepFlowPanel flow, IReadOnlyList<StepCardVm> items, int n)
        {
            Settle(win);
            seen.AddRange(vm.Steps);
            seen.AddRange(vm.Preview);
            Assert.Equal(n, items.Count);
            AssertLive(flow, items, seen);
            // 活着的条件框订阅 = 画布上的步数（另一块画布是空的）
            Assert.Equal(n, seen.Distinct<StepCardVm>(ReferenceEqualityComparer.Instance).Sum(c => CondHandlers(c).Count));
        }
        try
        {
            Check(panel.PreviewControl, vm.Preview, 4);   // 程序库默认选第一项（筛选-80 ℃）

            // 在程序库里点「长程序」那一行（点在行的右半边：整行都能点，复核 v4-program-2）
            M5ReviewHeadless.ClickRight(win, M5ReviewHeadless.PickButton(panel, vm.Library[^1]));
            Assert.Equal("long-30", vm.LibSel);
            Check(panel.PreviewControl, vm.Preview, 30);
            M5ReviewHeadless.ClickRight(win, M5ReviewHeadless.PickButton(panel, vm.Library[2]));
            Check(panel.PreviewControl, vm.Preview, 3);
            M5ReviewHeadless.ClickRight(win, M5ReviewHeadless.PickButton(panel, vm.Library[^1]));
            Check(panel.PreviewControl, vm.Preview, 30);

            // 换程序：下发「长程序」（没有程序 → 30 步），再换成「筛选-80 ℃」、再换回来
            vm.Library[^1].LoadCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();   // 下发是异步命令：续体排在这条线程的调度器上
            shell.Fleet.RefreshNow();
            Assert.True(vm.HasProgram);
            Check(panel.FlowControl, vm.Steps, 30);
            Assert.Empty(panel.PreviewControl.Pairs);
            demo.Act(d3, DeviceAction.ProgLoad, new ActionPayload(N: 13, Id: "screen-80"));
            shell.Fleet.RefreshNow();
            Check(panel.FlowControl, vm.Steps, 4);
            demo.Act(d3, DeviceAction.ProgLoad, new ActionPayload(N: 13, Id: "long-30"));
            shell.Fleet.RefreshNow();
            Check(panel.FlowControl, vm.Steps, 30);

            // 每秒的刷新：同一批卡片、同一批视图
            var views = panel.FlowControl.Pairs.ToList();
            shell.Fleet.RefreshNow();
            Check(panel.FlowControl, vm.Steps, 30);
            Assert.Equal(views, panel.FlowControl.Pairs);
        }
        finally
        {
            win.Close();
        }
    });
}
#endif
