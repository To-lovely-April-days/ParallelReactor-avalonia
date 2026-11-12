using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Shell;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M5 复核 c2-ui-1（视图模型这一半，不起 Avalonia）：换程序 / 换预览时步骤卡片的集合一次换完（一个 Reset，新卡片换进去之前已经填好内容），
/// 每秒的刷新不动集合。控件那一半（画布只建新的卡片、拆掉的放开视图模型）在 <c>M5ReviewStepFlowUiTests</c>（无头 Avalonia）。
/// </summary>
public class M5ReviewStepFlowTests
{
    internal static (ShellVm Shell, ReactorPageVm Page) Open(int k, int n, string query)
    {
        var p = DemoParams.Parse(query);
        if (!p.Freeze) p = p with { Freeze = true };
        var s = new ShellVm(FleetVm.CreateDemo(p));
        s.Toasts.Frozen = true;
        s.Layout.Update(new Avalonia.Size(1920, 1080));
        s.ApplyDemoParams(p, Route.Reactor(k, n));
        return (s, Assert.IsType<ReactorPageVm>(s.Page));
    }

    [Fact]
    public void ProgramAndPreview_ChangeOnce_FilledBeforeTheSwap()
    {
        var (shell, page) = Open(3, 13, "freeze=1&sel=3/13&lease=3:remote");
        var vm = page.Program;
        var stepsEvents = new List<NotifyCollectionChangedAction>();
        var previewEvents = new List<NotifyCollectionChangedAction>();
        var titlesAtSwap = new List<string>();
        vm.Steps.CollectionChanged += (_, e) =>
        {
            stepsEvents.Add(e.Action);
            titlesAtSwap.AddRange(vm.Steps.Select(c => c.Title));
        };
        vm.Preview.CollectionChanged += (_, e) =>
        {
            previewEvents.Add(e.Action);
            titlesAtSwap.AddRange(vm.Preview.Select(c => c.Title));
        };

        // 单击程序库的一行（预览 4 步 → 3 步）：预览一次换完
        vm.Library[2].PickCommand.Execute(null);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, previewEvents);
        Assert.Empty(stepsEvents);
        Assert.Equal(new[] { "1: 吹扫", "2: 等待", "3: 结束" }, titlesAtSwap);

        // 每秒的刷新：集合不动（卡片就地更新）
        previewEvents.Clear();
        titlesAtSwap.Clear();
        var before = vm.Preview.ToList();
        shell.Fleet.RefreshNow();
        Assert.Empty(previewEvents);
        Assert.Equal(before, vm.Preview);

        // 下发（没有程序 → 加氢-标准）：步骤一次换完，预览清空一次
        vm.Library[1].LoadCommand.Execute(null);
        shell.Fleet.RefreshNow();
        Assert.True(vm.HasProgram);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, stepsEvents);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, previewEvents);
        Assert.Equal(new[] { "1: 升温", "2: 充压", "3: 反应", "4: 结束" }, titlesAtSwap);
        Assert.Equal(4, vm.FlowSteps);

        // 再刷新：不动
        stepsEvents.Clear();
        var cards = vm.Steps.ToList();
        shell.Fleet.RefreshNow();
        Assert.Empty(stepsEvents);
        Assert.Equal(cards, vm.Steps);
    }

    [Fact]
    public void StepCardList_ReplaceAll_OneReset_CountAndIndexerNotified()
    {
        var list = new StepCardList();
        var events = new List<NotifyCollectionChangedAction>();
        var props = new List<string?>();
        list.CollectionChanged += (_, e) => events.Add(e.Action);
        ((System.ComponentModel.INotifyPropertyChanged)list).PropertyChanged += (_, e) => props.Add(e.PropertyName);
        var cards = Enumerable.Range(0, 30).Select(i => new StepCardVm(i)).ToList();
        list.ReplaceAll(cards);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, events);
        Assert.Equal(new[] { "Count", "Item[]" }, props);
        Assert.Equal(cards, list);
        list.ReplaceAll(Array.Empty<StepCardVm>());
        Assert.Empty(list);
        Assert.Equal(2, events.Count);
    }
}
