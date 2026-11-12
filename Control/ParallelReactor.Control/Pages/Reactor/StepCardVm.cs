using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs;

namespace ParallelReactor.Control.Pages;

/// <summary>步骤卡片标题右侧标记的样式（样稿 .pr-step-mark.is-*）：没有、已完成（✓ 14 t-3）、已中止（✕ 14/600 t-2）、报警暂停（⚠ 14/600 r-ink，图标 r）、当前（15/600 t-1）。</summary>
public enum StepMarkKind { None, Done, Abort, Held, Cur }

/// <summary>步骤卡片里的一节（样稿 ui.stepSections 的一项）：设备名「加热器 :」、方式、标签、数值；设备名的悬停是完整写法（搅拌电机（全局 RV1–RV8 共用））。</summary>
public sealed partial class StepSectionVm : ObservableObject
{
    [ObservableProperty] private string _dev = "";
    [ObservableProperty] private string _devTip = "";
    [ObservableProperty] private string _mode = "";
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _value = "";

    internal void Apply(StepSection s)
    {
        Dev = s.Dev + " :";
        DevTip = s.DevTip;
        Mode = s.Mode;
        Label = s.Label;
        Value = s.Value;
    }
}

/// <summary>
/// 一步（样稿 ui.stepCard + reactor.js condHTML / 组件陈列的 ui.stepCond）：卡片（标题「3: 反应」+ 标记 + 三节）和下面的条件框（标签 + 值 + 当前步的进度线）。
/// 身份 = (程序, 第几步)，就地更新（R10：每秒只改标记的剩余时间和进度线）。条件框的「结束条件 → 条件 → 16 px → 只留值」由视图按卡片宽度量字决定
/// （<see cref="StepFlowLayout.CondFit"/>），这里只给原文。
/// </summary>
public sealed partial class StepCardVm : ObservableObject
{
    public StepCardVm(int index)
    {
        Index = index;
        Sections = new[] { new StepSectionVm(), new StepSectionVm(), new StepSectionVm() };
    }

    /// <summary>第几步（0 起）。</summary>
    public int Index { get; }

    /// <summary>三节：加热器、进气阀 / 排空阀、搅拌电机。</summary>
    public IReadOnlyList<StepSectionVm> Sections { get; }

    /// <summary>'3: 反应'。</summary>
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private SegmentState _state = SegmentState.Next;
    /// <summary>当前步而程序报警暂停（白卡 + r-line 边，标记「⚠ 报警暂停」）。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CondHeld))] private bool _held;
    [ObservableProperty] private string _mark = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MarkIcon), nameof(HasMarkIcon), nameof(MarkCur), nameof(MarkHeld), nameof(MarkAbort), nameof(MarkDone))]
    private StepMarkKind _markKind;

    /// <summary>标记前的图标：已完成 ✓、已中止 ✕、报警暂停 ⚠；当前步没有。</summary>
    public string MarkIcon => MarkKind switch { StepMarkKind.Done => "check", StepMarkKind.Abort => "x", StepMarkKind.Held => "alert", _ => "" };
    public bool HasMarkIcon => MarkIcon.Length > 0;
    public bool MarkCur => MarkKind == StepMarkKind.Cur;
    public bool MarkHeld => MarkKind == StepMarkKind.Held;
    public bool MarkAbort => MarkKind == StepMarkKind.Abort;
    public bool MarkDone => MarkKind == StepMarkKind.Done;
    /// <summary>程序库的预览（虚线卡、没有阴影）。</summary>
    [ObservableProperty] private bool _preview;
    /// <summary>设备断线（卡片和进度去色，样稿 .reactor-flow.is-stale）。</summary>
    [ObservableProperty] private bool _stale;
    /// <summary>组件陈列的条件框（样稿 ui.stepCond：不按宽度改写标签、没有进度线、一律普通的浅灰框）。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CondCur), nameof(CondHeld), nameof(CondAbort))] private bool _plainCond;

    /// <summary>条件框画成当前步（白底 + g-line 边）：反应釜页的当前步；组件陈列的普通框不画（样稿 ui.stepCond 没有状态类）。</summary>
    public bool CondCur => IsCur && !PlainCond;
    /// <summary>条件框画成报警暂停（白底 + line-soft 边）：同上，组件陈列不画。</summary>
    public bool CondHeld => Held && !PlainCond;
    /// <summary>条件框画成中止（虚线框）：同上，组件陈列不画。</summary>
    public bool CondAbort => IsAbort && !PlainCond;

    // ———— 条件框 ————
    [ObservableProperty] private string _condLabel = "";
    [ObservableProperty] private string _condValue = "";
    [ObservableProperty] private string _condTip = "";
    /// <summary>当前步的进度（0–1，样稿 max(0.04, within)）；不是当前步为 0（不画进度线）。</summary>
    [ObservableProperty] private double _condFill;
    /// <summary>进度线在呼吸（运行中、在线、不在等进气）。</summary>
    [ObservableProperty] private bool _condRun;

    public bool IsCur => State == SegmentState.Cur;
    public bool IsDone => State == SegmentState.Done;
    public bool IsAbort => State == SegmentState.Abort;

    partial void OnStateChanged(SegmentState value)
    {
        OnPropertyChanged(nameof(IsCur));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsAbort));
        OnPropertyChanged(nameof(CondCur));
        OnPropertyChanged(nameof(CondAbort));
    }

    /// <summary>
    /// 按程序记录就地更新（样稿 stepCard / condHTML）。<paramref name="n"/> = 反应釜号（进气阀 SV13）；<paramref name="pr"/> = 进度（当前步的标记与进度线）；
    /// <paramref name="online"/> 断线时进度线不呼吸、卡片去色。<paramref name="plain"/> = 组件陈列（ui.stepFlow 的普通条件框）。
    /// </summary>
    public void Apply(ProgramRecord p, ModelProfile model, int n, string alias, ProgressInfo? pr, bool online, bool preview, bool plain)
    {
        var step = p.Steps[Index];
        var state = Progress.StepState(p, Index);
        bool held = state == SegmentState.Cur && p.Status == ProgramStatus.Held;
        State = state;
        Held = held;
        Preview = preview;
        Stale = !online && !preview;
        PlainCond = plain;
        Title = (Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ": " + StepText.Name(step);
        Mark = Progress.StepMark(p, Index, pr);
        MarkKind = state switch
        {
            SegmentState.Done => StepMarkKind.Done,
            SegmentState.Abort => StepMarkKind.Abort,
            SegmentState.Cur => held ? StepMarkKind.Held : StepMarkKind.Cur,
            _ => StepMarkKind.None,
        };
        var secs = StepText.Sections(model, n, step);
        for (int i = 0; i < Sections.Count && i < secs.Count; i++) Sections[i].Apply(secs[i]);
        var c = StepText.Cond(step, model);
        CondLabel = c.Label;
        CondValue = c.Value;
        if (plain)
        {
            CondTip = alias + " · RV" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + " · " + c.Label + " " + c.Value;
            CondFill = 0;
            CondRun = false;
            return;
        }
        CondTip = ProgramPanelText.CondTip(alias, n, p, Index, pr, model);
        bool cur = state == SegmentState.Cur;
        CondFill = cur ? ProgramPanelText.CondFill(pr) : 0;
        CondRun = cur && p.Status == ProgramStatus.Running && online && !(pr?.WaitGas ?? false);
    }

}
